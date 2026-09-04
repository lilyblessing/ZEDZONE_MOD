using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using ZedZoneShared;

namespace TeleportStationPlugin;

/// <summary>
/// P5 Step4 传送执行（复用 Probe v0.13 优化版）。
/// 角色/载具双路径，照 Probe 10步但做优化：
///   IsActive 单帧 + lastSane/lastSaneInit/streak/isPhysicsAnomalous 全量同步，不做 maxSane 100，
///   仅 20帧守卫（drift&gt;1.5m 重写 quad+rb+transform），Harmony 双拦截 EnforcePhysicsSafety 与 NetMovePuppet。
/// 锚定：缓存 vehicleQuadObj Transform 句柄（FieldRef），守卫期零反射，20帧后结束。
/// 高度：复用 MapController.GetTerrainTempHeightByWorldPosition。
/// VehicleData 3件套同步：worldCoordinate/coordinateX/Y 保留三写但改直访（优先直访，失败回退 Reflect）。
/// 目标点：取对方圆盘 transform.position（或控制台附近空地），未找到则取消。
/// </summary>
public static class TeleportExecutionManager
{
    // ========== 常量 ==========
    private const int PadId = 900102;
    private const int ConsoleId = 900101;
    private const float DriftThreshold = 1.5f;
    private const int AnchorDuration = 20;
    private const int PhysGuardFrames = 2; // IsActive 单帧保险，覆盖至少 1 个 FixedUpdate

    // ========== IsActive 守卫（Harmony 双拦截读取） ==========
    private static int _physGuardRemaining = 0;
    public static bool IsActive => _physGuardRemaining > 0;

    // ========== 锚定状态（零反射守卫期） ==========
    private static int _anchorFrames = 0;
    private static BasicVehicle _anchorVehicle = null;
    private static Vector3 _anchorPos;
    private static Vector2 _anchorXY;
    private static Transform _anchorQuadTrans = null; // 缓存 Transform 句柄，守卫期零反射

    // ========== FieldInfo 缓存（传送时一次反射，守卫期零反射） ==========
    private static readonly FieldInfo FiLastSane = AccessTools.Field(typeof(BasicVehicle), "lastSanePosition");
    private static readonly FieldInfo FiLastSaneInit = AccessTools.Field(typeof(BasicVehicle), "lastSanePositionInit");
    private static readonly FieldInfo FiStreak = AccessTools.Field(typeof(BasicVehicle), "physicsAnomalyStreak");
    private static readonly FieldInfo FiLastStepSpeed = AccessTools.Field(typeof(BasicVehicle), "lastStepSpeed");
    private static readonly FieldInfo FiQuadObj = AccessTools.Field(typeof(BasicVehicle), "vehicleQuadObj");
    private static readonly FieldInfo FiAnomBacking = AccessTools.Field(typeof(BasicVehicle), "<isPhysicsAnomalous>k__BackingField");
    private static readonly PropertyInfo PiAnom = AccessTools.Property(typeof(BasicVehicle), "isPhysicsAnomalous");
    private static readonly FieldInfo FiVehicleData = AccessTools.Field(typeof(BasicVehicle), "vehicleData");
    // VehicleData 字段（运行时类型上解析，缓存首次）
    private static FieldInfo _vdWorld = null;
    private static FieldInfo _vdX = null;
    private static FieldInfo _vdY = null;
    private static PropertyInfo _vdWorldProp = null;
    private static PropertyInfo _vdXProp = null;
    private static PropertyInfo _vdYProp = null;
    private static bool _vdCacheTried = false;

    private static bool _patchesApplied = false;

    // ========== 对外 API ==========

    /// <summary>确保 Harmony 双拦截已挂钩（幂等）。</summary>
    public static void EnsurePatches()
    {
        if (_patchesApplied) return;
        try
        {
            var h = new Harmony("com.zedzone.teleportstation.exec");
            var enforce = AccessTools.Method(typeof(BasicVehicle), "EnforcePhysicsSafety");
            if (enforce != null)
            {
                var pre = typeof(TeleportPhysicsPatches).GetMethod(nameof(TeleportPhysicsPatches.EnforcePrefix), BindingFlags.Public | BindingFlags.Static);
                h.Patch(enforce, prefix: new HarmonyMethod(pre));
                Plugin.L.LogInfo("[TS][Tele] 已挂钩 BasicVehicle.EnforcePhysicsSafety（IsActive 单帧拦截）");
            }
            else Plugin.L.LogWarning("[TS][Tele] 未找到 EnforcePhysicsSafety，拦截未生效");

            var puppet = AccessTools.Method(typeof(BasicVehicle), "NetMovePuppet");
            if (puppet != null)
            {
                var pre2 = typeof(TeleportPhysicsPatches).GetMethod(nameof(TeleportPhysicsPatches.NetMovePuppetPrefix), BindingFlags.Public | BindingFlags.Static);
                h.Patch(puppet, prefix: new HarmonyMethod(pre2));
                Plugin.L.LogInfo("[TS][Tele] 已挂钩 BasicVehicle.NetMovePuppet（IsActive 单帧拦截）");
            }
            else Plugin.L.LogWarning("[TS][Tele] 未找到 NetMovePuppet，拦截未生效（不影响单机传送主路径）");

            _patchesApplied = true;
        }
        catch (Exception e) { Plugin.L.LogWarning($"[TS][Tele] EnsurePatches 异常: {e.Message.Split('\n')[0]}"); }
    }

    /// <summary>确保锚定 Ticker 存在（DontDestroy）。</summary>
    public static void EnsureTicker()
    {
        try
        {
            if (TeleportAnchorTicker.Instance != null) return;
            var go = new GameObject("TeleportExecutionTicker");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<TeleportAnchorTicker>();
        }
        catch { }
    }

    /// <summary>
    /// 通用入口：entrant 为玩家或载具根 GameObject，targetWorldPos 为世界坐标（含 z）。
    /// 自动分流角色/载具双路径，保留驾驶态。
    /// </summary>
    public static bool TryTeleport(GameObject entrant, Vector3 targetWorldPos)
    {
        if (entrant == null) return false;
        EnsurePatches();
        EnsureTicker();
        try
        {
            // 判定是否驾驶中：优先从 entrant 上取 HumanCharacterController，否则从 GameController.instance.playerCharacter 兜底
            BasicVehicle veh = null;
            HumanCharacterController hcc = null;
            // 1) entrant 自身或父链上找 HCC
            try { hcc = entrant.GetComponentInParent<HumanCharacterController>(); } catch { }
            if (hcc == null)
            {
                try
                {
                    var gc = GameController.instance;
                    var pc = gc != null ? gc.playerCharacter : null;
                    if (pc != null) hcc = pc as HumanCharacterController;
                    if (hcc == null && pc != null) hcc = pc.GetComponent<HumanCharacterController>();
                }
                catch { }
            }
            if (hcc != null)
            {
                try
                {
                    var drv = Reflect.Get(hcc, "drivingVehicle") as BasicVehicle;
                    if (drv == null)
                    {
                        // 编译期直访兜底（若 public）
                        var fi = AccessTools.Field(hcc.GetType(), "drivingVehicle");
                        if (fi != null) drv = fi.GetValue(hcc) as BasicVehicle;
                    }
                    var isDrivingObj = Reflect.Get(hcc, "isDriving");
                    bool isDriving = isDrivingObj is bool b ? b : drv != null;
                    if (isDriving && drv != null) veh = drv;
                }
                catch { }
            }
            // 2) 若 entrant 本身就是 BasicVehicle
            if (veh == null)
            {
                try { veh = entrant.GetComponentInParent<BasicVehicle>(); } catch { }
            }

            Vector2 targetXY = new Vector2(targetWorldPos.x, targetWorldPos.y);
            if (veh != null)
            {
                return TryTeleportVehicle(veh, targetXY, targetWorldPos.z, targetWorldPos);
            }
            else
            {
                return TryTeleportPlayer(entrant, targetWorldPos);
            }
        }
        catch (Exception e)
        {
            Plugin.L.LogWarning($"[TS][Tele] TryTeleport 异常: {e.Message.Split('\n')[0]}");
            _physGuardRemaining = 0;
            return false;
        }
    }

    /// <summary>从圆盘触发：entrant 进入 sourcePad，自动解析对方圆盘/控制台附近为空地，未找到则取消。</summary>
    public static bool TryTeleportFromPad(GameObject entrant, TerrainObject sourcePad)
    {
        if (entrant == null || sourcePad == null) return false;
        if (!ResolveTargetPosition(sourcePad, out var targetPos))
        {
            Plugin.L.LogInfo($"[TS][Tele] 无可用目标，取消传送 sourcePad={sourcePad.name} pos={sourcePad.transform.position}");
            return false;
        }
        return TryTeleport(entrant, targetPos);
    }

    /// <summary>解析目标点：优先对方圆盘 transform.position，否则控制台附近空地。</summary>
    public static bool ResolveTargetPosition(TerrainObject sourcePad, out Vector3 targetPos)
    {
        targetPos = default;
        if (sourcePad == null) return false;
        try
        {
            long srcKey = GetInstanceKey(sourcePad);
            // 1) 找所有已绑定的其他圆盘（排除源盘）
            var candidates = FindAllTerrainObjectsById(PadId);
            TerrainObject best = null;
            float bestD2 = float.MaxValue;
            var srcPos = sourcePad.transform.position;
            foreach (var pad in candidates)
            {
                if (pad == null || pad.transform == null) continue;
                long k = GetInstanceKey(pad);
                if (k == srcKey) continue;
                if (!TeleportBindingManager.IsPadBound(k)) continue;
                var d = pad.transform.position - srcPos;
                float d2 = d.x * d.x + d.y * d.y;
                // 选最近的其他已绑定盘
                if (d2 < bestD2) { bestD2 = d2; best = pad; }
            }
            if (best != null)
            {
                // 取对方圆盘位置，叠加 1.2m 偏移避免重叠
                var p = best.transform.position;
                float z = GetGroundHeight(new Vector2(p.x, p.y));
                // 若地形高度取到则用，否则保持原 z
                if (Math.Abs(z) < 0.01f) z = p.z;
                targetPos = new Vector3(p.x + 1.2f, p.y, z);
                Plugin.L.LogInfo($"[TS][Tele] 目标=对方圆盘 {best.name} -> {targetPos.x:F1},{targetPos.y:F1},{targetPos.z:F1}");
                return true;
            }
            // 2) 回退：源盘所绑定的控制台附近空地（控制台前方 2m）
            long consoleKey = TeleportBindingManager.GetBoundConsole(srcKey);
            TerrainObject console = null;
            if (consoleKey != 0) console = FindByKey(consoleKey) as TerrainObject;
            if (console == null)
            {
                // 源盘未绑定，尝试找距离源盘最近的控制台（20m 内）
                var consoles = FindAllTerrainObjectsById(ConsoleId);
                float bestC = float.MaxValue;
                foreach (var c in consoles)
                {
                    if (c == null) continue;
                    var d = c.transform.position - srcPos;
                    float d2 = d.x * d.x + d.y * d.y;
                    if (d2 < 400f && d2 < bestC) { bestC = d2; console = c; }
                }
            }
            if (console != null)
            {
                var cp = console.transform.position;
                // 控制台前方 2m 空地（朝 +X）
                Vector2 off = new Vector2(cp.x + 2f, cp.y);
                float z = GetGroundHeight(off);
                if (Math.Abs(z) < 0.01f) z = cp.z;
                targetPos = new Vector3(off.x, off.y, z);
                Plugin.L.LogInfo($"[TS][Tele] 目标=控制台附近 {console.name} -> {targetPos.x:F1},{targetPos.y:F1},{targetPos.z:F1}");
                return true;
            }
        }
        catch (Exception e) { Plugin.L.LogWarning($"[TS][Tele] ResolveTarget 异常: {e.Message.Split('\n')[0]}"); }
        return false;
    }

    // ========== 角色路径（5步 Probe 简化） ==========
    private static bool TryTeleportPlayer(GameObject entrant, Vector3 targetPos)
    {
        try
        {
            // IsActive 单帧保险（玩家路径无 Enforce，但防御性开启）
            _physGuardRemaining = PhysGuardFrames;
            var trans = entrant.transform;
            if (trans == null) trans = entrant.GetComponent<Transform>();
            if (trans == null) { _physGuardRemaining = 0; return false; }

            Vector3 old = trans.position;
            // 地形高度：targetPos 已含 z，此处仅在 z 异常时重取
            if (Math.Abs(targetPos.z) < 0.01f)
            {
                float gz = GetGroundHeight(new Vector2(targetPos.x, targetPos.y));
                if (Math.Abs(gz) > 0.01f) targetPos.z = gz;
            }

            // rb 双写：优先 HumanCharacterController.m_rigidbody 直访，回退 GetComponent
            bool rbWritten = false;
            try
            {
                Rigidbody2D rb = null;
                var hcc = entrant.GetComponentInParent<HumanCharacterController>();
                if (hcc != null)
                {
                    try { rb = hcc.m_rigidbody; } catch { }
                    if (rb == null) rb = hcc.GetComponent<Rigidbody2D>();
                }
                if (rb == null) rb = entrant.GetComponentInParent<Rigidbody2D>();
                if (rb == null) rb = trans.GetComponent<Rigidbody2D>();
                if (rb != null)
                {
                    rb.position = new Vector2(targetPos.x, targetPos.y);
                    rb.velocity = Vector2.zero;
                    try { rb.angularVelocity = 0f; } catch { }
                    rbWritten = true;
                }
            }
            catch (Exception e) { Plugin.L.LogWarning($"[TS][Tele] 玩家 rb 双写异常: {e.Message.Split('\n')[0]}"); }

            trans.position = targetPos;
            Plugin.L.LogInfo($"[TS][Tele] 角色传送 {old.x:F1},{old.y:F1} -> {targetPos.x:F1},{targetPos.y:F1} rb={rbWritten}");
            // IsActive 1-2 帧后由 Ticker 自动清零，此处不立即清
            return true;
        }
        catch (Exception e)
        {
            Plugin.L.LogWarning($"[TS][Tele] TryTeleportPlayer 异常: {e.Message.Split('\n')[0]}");
            _physGuardRemaining = 0;
            return false;
        }
    }

    // ========== 载具路径（10步 Probe 优化版） ==========
    private static bool TryTeleportVehicle(BasicVehicle v, Vector2 targetXY, float groundZ, Vector3 targetPos)
    {
        if (v == null) return false;
        try
        {
            // Step0 已获取 v
            // Step1 快照（日志用）
            Vector3 beforePos = v.transform.position;
            // Step2 目标已传入，补地面高度
            if (Math.Abs(groundZ) < 0.01f)
            {
                float gz = GetGroundHeight(targetXY);
                if (Math.Abs(gz) > 0.01f) groundZ = gz;
                targetPos = new Vector3(targetXY.x, targetXY.y, groundZ);
            }

            Plugin.L.LogInfo($"[TS][Tele] 载具传送 {v.name} {beforePos.x:F1},{beforePos.y:F1} -> {targetPos.x:F1},{targetPos.y:F1},{targetPos.z:F1}");

            // Step3 IsActive 单帧开启（覆盖至少 1 FixedUpdate）
            _physGuardRemaining = PhysGuardFrames;

            // Step5 rb + transform 三件套（先 rb 后 transform）
            try
            {
                v.m_rigidbody.position = targetXY;
                v.m_rigidbody.velocity = Vector2.zero;
                try { v.m_rigidbody.angularVelocity = 0f; } catch { }
            }
            catch (Exception e) { Plugin.L.LogWarning($"[TS][Tele] rb 写入异常: {e.Message.Split('\n')[0]}"); }
            v.transform.position = targetPos;

            // Step6 lastSane 全量同步（替 100 阈值）
            try
            {
                if (FiLastSane != null) FiLastSane.SetValue(v, targetPos);
                else Reflect.Set(v, "lastSanePosition", targetPos);
            }
            catch (Exception e) { Plugin.L.LogWarning($"[TS][Tele] lastSane 异常: {e.Message.Split('\n')[0]}"); }
            try
            {
                if (FiLastSaneInit != null) FiLastSaneInit.SetValue(v, true);
                else Reflect.Set(v, "lastSanePositionInit", true);
            }
            catch { }
            try
            {
                if (FiStreak != null) FiStreak.SetValue(v, 0);
                else Reflect.Set(v, "physicsAnomalyStreak", 0);
            }
            catch { }
            try
            {
                if (PiAnom != null && PiAnom.CanWrite) PiAnom.SetValue(v, false);
                else if (FiAnomBacking != null) FiAnomBacking.SetValue(v, false);
                else Reflect.Set(v, "isPhysicsAnomalous", false);
            }
            catch { }
            try
            {
                if (FiLastStepSpeed != null) FiLastStepSpeed.SetValue(v, 0f);
                else Reflect.Set(v, "lastStepSpeed", 0f);
            }
            catch { }

            // Step7 VehicleData 三件套（保留三写，优先直访）
            try { SyncVehicleData(v, targetXY); }
            catch (Exception e) { Plugin.L.LogWarning($"[TS][Tele] VehicleData 异常: {e.Message.Split('\n')[0]}"); }

            // Step8 SetVehicleChunk
            try { v.SetVehicleChunk(); } catch (Exception e) { Plugin.L.LogWarning($"[TS][Tele] SetVehicleChunk 异常: {e.Message.Split('\n')[0]}"); }

            // quad 同步 + 缓存 Transform 句柄（守卫期零反射）
            Transform quadTrans = null;
            try
            {
                GameObject quadGO = null;
                if (FiQuadObj != null) quadGO = FiQuadObj.GetValue(v) as GameObject;
                if (quadGO == null) quadGO = Reflect.Get(v, "vehicleQuadObj") as GameObject;
                if (quadGO == null)
                {
                    var comp = Reflect.Get(v, "vehicleQuadObj") as Component;
                    if (comp != null) quadGO = comp.gameObject;
                }
                if (quadGO != null)
                {
                    quadGO.transform.position = targetPos;
                    quadTrans = quadGO.transform;
                }
            }
            catch (Exception e) { Plugin.L.LogWarning($"[TS][Tele] quad 同步异常: {e.Message.Split('\n')[0]}"); }

            // Step9 IsActive 不立即关闭，由 Ticker 2帧后自动清（此处保留 guard）
            // Step10 锚定启动：20帧 drift>1.5m 重写 quad+rb+transform
            _anchorVehicle = v;
            _anchorPos = targetPos;
            _anchorXY = targetXY;
            _anchorQuadTrans = quadTrans;
            _anchorFrames = AnchorDuration;

            Plugin.L.LogInfo($"[TS][Tele] ★ 传送完成，进入 20帧锚定 drift>{DriftThreshold}m quad={(quadTrans!=null?"ok":"null")}");
            return true;
        }
        catch (Exception e)
        {
            Plugin.L.LogWarning($"[TS][Tele] TryTeleportVehicle 异常: {e.Message.Split('\n')[0]}");
            _physGuardRemaining = 0;
            return false;
        }
    }

    private static void SyncVehicleData(BasicVehicle v, Vector2 targetXY)
    {
        object vd = null;
        try { vd = FiVehicleData != null ? FiVehicleData.GetValue(v) : Reflect.Get(v, "vehicleData"); } catch { vd = Reflect.Get(v, "vehicleData"); }
        if (vd == null) { Plugin.L.LogWarning("[TS][Tele] vehicleData 为 null，跳过坐标同步"); return; }
        // 缓存 VehicleData 字段
        if (!_vdCacheTried)
        {
            _vdCacheTried = true;
            try
            {
                var t = vd.GetType();
                _vdWorld = t.GetField("worldCoordinate", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _vdX = t.GetField("coordinateX", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _vdY = t.GetField("coordinateY", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _vdWorldProp = t.GetProperty("worldCoordinate", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _vdXProp = t.GetProperty("coordinateX", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _vdYProp = t.GetProperty("coordinateY", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }
            catch { }
        }
        bool wrote = false;
        try
        {
            if (_vdWorldProp != null && _vdWorldProp.CanWrite) { _vdWorldProp.SetValue(vd, targetXY); wrote = true; }
            else if (_vdWorld != null) { _vdWorld.SetValue(vd, targetXY); wrote = true; }
            else Reflect.Set(vd, "worldCoordinate", targetXY);
        }
        catch { try { Reflect.Set(vd, "worldCoordinate", targetXY); } catch { } }
        try
        {
            if (_vdXProp != null && _vdXProp.CanWrite) _vdXProp.SetValue(vd, targetXY.x);
            else if (_vdX != null) _vdX.SetValue(vd, targetXY.x);
            else Reflect.Set(vd, "coordinateX", targetXY.x);
        }
        catch { try { Reflect.Set(vd, "coordinateX", targetXY.x); } catch { } }
        try
        {
            if (_vdYProp != null && _vdYProp.CanWrite) _vdYProp.SetValue(vd, targetXY.y);
            else if (_vdY != null) _vdY.SetValue(vd, targetXY.y);
            else Reflect.Set(vd, "coordinateY", targetXY.y);
        }
        catch { try { Reflect.Set(vd, "coordinateY", targetXY.y); } catch { } }
        if (wrote) Plugin.L.LogInfo($"[TS][Tele] VehicleData 同步 world=({targetXY.x:F1},{targetXY.y:F1})");
    }

    // ========== 锚定 Tick（由 Ticker 驱动，零反射） ==========
    internal static void TickAnchor()
    {
        // IsActive 守卫帧递减
        if (_physGuardRemaining > 0) _physGuardRemaining--;

        if (_anchorFrames <= 0 || _anchorVehicle == null) return;
        _anchorFrames--;
        try
        {
            var t = _anchorVehicle.transform;
            if (t == null) { _anchorFrames = 0; _anchorVehicle = null; _anchorQuadTrans = null; return; }
            float dx = t.position.x - _anchorPos.x;
            float dy = t.position.y - _anchorPos.y;
            float drift = Mathf.Sqrt(dx * dx + dy * dy);
            if (drift > DriftThreshold)
            {
                Plugin.L.LogWarning($"[TS][Tele] 锚定重写 drift={drift:F1}m （守卫剩{_anchorFrames}帧）");
                try { _anchorVehicle.m_rigidbody.position = _anchorXY; _anchorVehicle.m_rigidbody.velocity = Vector2.zero; try { _anchorVehicle.m_rigidbody.angularVelocity = 0f; } catch { } } catch { }
                t.position = _anchorPos;
                if (_anchorQuadTrans != null) _anchorQuadTrans.position = _anchorPos;
            }
        }
        catch (Exception e)
        {
            Plugin.L.LogWarning($"[TS][Tele] 锚定异常: {e.Message.Split('\n')[0]}");
            _anchorFrames = 0;
            _anchorVehicle = null;
            _anchorQuadTrans = null;
        }
        if (_anchorFrames == 0)
        {
            _anchorVehicle = null;
            _anchorQuadTrans = null;
        }
    }

    // ========== 工具 ==========
    private static float GetGroundHeight(Vector2 pos)
    {
        try
        {
            var mc = MapController.instance;
            if (mc != null) return mc.GetTerrainTempHeightByWorldPosition(pos);
        }
        catch { }
        return 0f;
    }

    private static long GetInstanceKey(TerrainObject t)
    {
        try { return (long)t.GetInstanceID(); } catch { try { return (long)t.Pointer; } catch { return t.GetHashCode(); } }
    }

    private static Component FindTerrainObject(Transform tr)
    {
        int d = 0;
        while (tr != null && d++ < 16)
        {
            foreach (var c in tr.GetComponents<Component>())
            {
                if (c == null) continue;
                string n = c.GetType().Name;
                if (n.Contains("TerrainObject")) return c;
            }
            tr = tr.parent;
        }
        return null;
    }

    // P2 收尾：只读委托统一入口（查 900101+900102 两张 0.5s TTL 缓存表，命中零扫描；未中返 null，语义同旧直扫）。
    private static TerrainObject FindByKey(long key)
    {
        try { return TeleportObjectCache.FindByKey(key); } catch { return null; }
    }

    private static List<TerrainObject> FindAllTerrainObjectsById(int attrId)
    {
        var result = new List<TerrainObject>();
        var seen = new HashSet<long>();
        try
        {
            var f = typeof(ChargerPadFix).GetField("_knownClones", BindingFlags.NonPublic | BindingFlags.Static);
            var list = f?.GetValue(null) as List<object>;
            if (list != null) foreach (var o in list) { var c = o as Component; if (c==null) continue; var t = FindTerrainObject(c.transform) as TerrainObject; if (t!=null && t.attr!=null && t.attr.id==attrId) { long k=GetInstanceKey(t); if(seen.Add(k)) result.Add(t); } }
        } catch {}
        try
        {
            var list2 = TerrainObject_Production.ActiveObjects_Production;
            if (list2 != null) for (int i=0;i<list2.Count;i++) { var g=list2[i]; if(g==null) continue; var t=FindTerrainObject(g.transform) as TerrainObject; if(t!=null && t.attr!=null && t.attr.id==attrId) { long k=GetInstanceKey(t); if(seen.Add(k)) result.Add(t); } }
        } catch {}
        try
        {
            var all = UnityEngine.Resources.FindObjectsOfTypeAll<TerrainObject>();
            if (all != null) foreach (var t in all) if (t!=null && t.attr!=null && t.attr.id==attrId) { long k=GetInstanceKey(t); if(seen.Add(k)) result.Add(t); }
        } catch {}
        return result;
    }
}

/// <summary>Harmony 双拦截：EnforcePhysicsSafety 与 NetMovePuppet 在 IsActive 单帧内直接跳过。</summary>
public static class TeleportPhysicsPatches
{
    public static bool EnforcePrefix(BasicVehicle __instance)
    {
        if (TeleportExecutionManager.IsActive) return false;
        return true;
    }
    public static bool NetMovePuppetPrefix(BasicVehicle __instance)
    {
        if (TeleportExecutionManager.IsActive) return false;
        return true;
    }
}

/// <summary>锚定与 IsActive 守卫的 Ticker（DontDestroy，LateUpdate 零反射）。</summary>
public class TeleportAnchorTicker : MonoBehaviour
{
    public static TeleportAnchorTicker Instance { get; private set; }
    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
    }
    void OnDestroy() { if (Instance == this) Instance = null; }
    void LateUpdate() { try { TeleportExecutionManager.TickAnchor(); } catch { } }
}

