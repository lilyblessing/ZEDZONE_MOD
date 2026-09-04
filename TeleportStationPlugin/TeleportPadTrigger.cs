using System;
using System.Collections.Generic;
using UnityEngine;
using HarmonyLib;
using Il2CppInterop.Runtime.Attributes;

namespace TeleportStationPlugin;

/// <summary>
/// P5 进入检测：扫描所有 900102 圆盘，检测玩家/载具是否进入半径 4.9m（9.8/2），满足激活条件则触发 5s 倒计时。
/// 因 900102 克隆体为 HideAndDontSave 且 NoCollision，无原生 trigger，采用距离轮询（0.2s 节流，零分配）。
/// </summary>
public class TeleportPadTrigger : MonoBehaviour
{
    private const float PadRadius = 5f;
    private const float PadRadiusSqr = 5f * 5f;
    private float _nextScan = -1f;
    private TerrainObject _activePad = null;
    private Component _activeEntrant = null; // HumanCharacterController or BasicVehicle
    private Transform _activeEntrantTr = null;

    // P2-4 ShowBubble 缓存（抄 TeleportBindingManager.cs:502-503 范式，只读参考未改它）：
    // Bubble Type/MethodInfo 查一次常驻；另记最近一次已发送文案，状态未变不重发。
    // _activePad 切换/清空时重置 _lastBubbleMsg（见 Update），换盘与重进提示行为与原来一致。
    private static System.Type _cachedBubbleType = null;
    private static System.Reflection.MethodInfo _cachedBubbleMethod = null;
    private static string _lastBubbleMsg = null;
    // P2-4 drivingVehicle 结论缓存：FieldInfo 查一次常驻，含"无字段"负结论（_drvField=null）。
    // 静默反射（Type.GetField，不走 AccessTools.Field）：线上玩家类型实为 BasicCharacterController、
    // 根本无 drivingVehicle 字段——缓存负结论后每 tick 不再探路，HarmonyX warning 消失；
    // 取不到时返回 null，走既有步行分支（行为不变）。玩家运行时类型变化时重查一次。
    private static System.Type _drvPlayerType = null;
    private static System.Reflection.FieldInfo _drvField = null;
    private static bool _drvResolved = false;
    // 落地电网自愈 workaround（09-05 更新后跨区块传送偶发丢连接）：传送成功记到达时间戳＋目标站 UID，
    // 到达窗口驱动（Update 0.5s 节流内检查，30s 窗口内单次评估，未供电则调 ProductionManager.MarkElectricGridDirty()）；
    // 未供电分支内的 TryFireArrivalGridDirty 调用保留作备份路径，共享 _arrivalDirtyFired 防重、天然互斥。
    private static float _lastArrivalTime = -999f;
    private static string _lastArrivalUid = null;
    private static bool _arrivalDirtyFired = true; // 初值 true：未传送过不触发

    private static TeleportPadTrigger _instance;
    public static TeleportPadTrigger EnsureExists()
    {
        if (_instance != null) return _instance;
        var go = new GameObject("TeleportPadTrigger");
        DontDestroyOnLoad(go);
        _instance = go.AddComponent<TeleportPadTrigger>();
        return _instance;
    }

    void Awake()
    {
        Plugin.L?.LogInfo("[TS][Teleport] PadTrigger Awake");
    }

    void Update()
    {
        try
        {
            float now = Time.unscaledTime;
            if (now < _nextScan) return;
            _nextScan = now + 0.5f; // P6.1 性能：0.2→0.5s，结合 0.5s 缓存，总扫描从 ~9 次/秒 降至 ~2 次/秒
            // 落地电网自愈（到达窗口驱动）：传送成功后 30s 内对到达站做一次供电评估，未供电则单次重算。
            // 只在 _arrivalDirtyFired==false 时跑（平时一个 if 即过，无新增每帧工作）；评估过即关，不管供电与否。
            try
            {
                if (!_arrivalDirtyFired && _lastArrivalUid != null && now - _lastArrivalTime < 30f)
                {
                    TerrainObject arrivalPad = null;
                    try { arrivalPad = TeleportConsoleSelection.ResolveLivePad(_lastArrivalUid); } catch { arrivalPad = null; }
                    if (arrivalPad == null)
                    {
                        _arrivalDirtyFired = true;
                    }
                    else
                    {
                        bool online = true;
                        bool evalOk = true;
                        try { online = TeleportConsoleSelection.IsOnline(arrivalPad); } catch { evalOk = false; }
                        if (evalOk && !online)
                        {
                            try { ProductionManager.MarkElectricGridDirty(); }
                            catch { }
                            Plugin.L?.LogInfo($"[TS][Tele] 落地电网重算已触发（窗口） 站={_lastArrivalUid ?? "?"}");
                        }
                        _arrivalDirtyFired = true;
                    }
                }
            }
            catch { }
            // 诊断：已关闭高频扫描日志（原每5s刷屏，导致日志误判为错误）
            // 仅在需要时手动打开：if ((int)(now) % 30 == 0) Log...

            // 若倒计时进行中，跳过新触发，让 CountdownUI 处理脱离取消
            if (TeleportCountdownUI.Instance != null && TeleportCountdownUI.Instance.IsCounting) return;

            var player = GetPlayer();
            if (player == null) return;
            var playerTr = (player as Component)?.transform;
            if (playerTr == null) return;

            // 判断是否在驾驶载具（P2-4：结论常驻缓存，命中负结论直接 null，不再每 tick 反射）
            BasicVehicle vehicle = null;
            try { vehicle = GetDrivingVehicle(player); } catch {}
            Transform entrantTr = playerTr;
            Component entrant = player as Component;
            bool inVehicle = false;
            if (vehicle != null)
            {
                entrant = vehicle;
                entrantTr = vehicle.transform;
                inVehicle = true;
            }

            // 扫描所有圆盘，找最近且在半径内的
            var pads = FindAllPads();
            TerrainObject nearestPad = null;
            float bestD2 = float.MaxValue;
            foreach (var pad in pads)
            {
                if (pad == null || pad.transform == null) continue;
                var d = pad.transform.position - entrantTr.position;
                float d2 = d.x*d.x + d.y*d.y;
                if (d2 < PadRadiusSqr && d2 < bestD2)
                {
                    bestD2 = d2;
                    nearestPad = pad;
                }
            }

            if (nearestPad != null)
            {
                // 首次进入或换盘
                if (_activePad != nearestPad)
                {
                    _activePad = nearestPad;
                    _activeEntrant = entrant;
                    _activeEntrantTr = entrantTr;
                    _lastBubbleMsg = null; // P2-4：换盘重置气泡状态，新盘提示不受旧盘文案抑制
                    TryStartTeleport(nearestPad, entrant, entrantTr, inVehicle);
                }
            }
            else
            {
                // 离开所有 pad 范围
                _activePad = null;
                _activeEntrant = null;
                _activeEntrantTr = null;
                _lastBubbleMsg = null; // P2-4：离盘重置，重进同盘提示行为不变
            }
        }
        catch {}
    }

    [HideFromIl2Cpp]
    private void TryStartTeleport(TerrainObject pad, Component entrantComp, Transform entrantTr, bool inVehicle)
    {
        try
        {
            // P6 发送方判定：已绑 + 供电(AND) + 电池≥10000；接收方无门控（用户定案 v0.9.64）。
            // v0.9.64 目的地以 UID 识别：有活体走活体坐标，无活体走持久坐标（无在线/走近门控）。
            long padKey = GetInstanceKey(pad);
            string senderUid = TeleportStationUid.UidFor(pad);
            long consoleKey = TeleportBindingManager.GetBoundConsole(padKey);
            if (consoleKey == 0)
            {
                ShowBubble("未绑定");
                Plugin.L?.LogInfo($"[TS][Teleport] 未绑定 {senderUid}");
                return;
            }
            var console = FindByKey(consoleKey) as TerrainObject;
            if (console == null)
            {
                ShowBubble("未绑定");
                return;
            }
            string consoleUid = TeleportStationUid.UidFor(console);
            // P6 新增：未选时不触发任何传送逻辑（仅提示，不进倒计时）
            string selectedUid = TeleportConsoleSelection.GetSelectedUid(consoleUid);
            if (string.IsNullOrEmpty(selectedUid))
            {
                // 节流：每进入一次提示一次（由 _activePad 去重保证）
                ShowBubble("请选择目的地");
                Plugin.L?.LogInfo($"[TS][Teleport] 未选择目的地 {consoleUid} sender={senderUid}");
                return;
            }
            string dispTarget = TeleportStationUid.DisplayForUid(selectedUid);
            if (!ResolveTarget(selectedUid, pad, out var targetPos, out var targetLive, out var viaPersisted))
            {
                // v0.9.64：目的地无在线门控；此处仅 UID 非法/无坐标记录才到（几乎不可达）。
                ShowBubble("目的地未知，请重选");
                Plugin.L?.LogInfo($"[TS][Teleport] 目标无法解析 {selectedUid}({dispTarget}) live=无 persistedPos=无");
                return;
            }
            if (!string.IsNullOrEmpty(senderUid) && selectedUid == senderUid)
            {
                ShowBubble("不能传送至本站");
                return;
            }
            // 发送方供电判定（AND）
            var pd = GetProductionData(pad);
            bool powered = false;
            float diagSufficient = -1f; float diagGridFactor = -1f; bool diagConsuming = false; bool diagPowerOff = false;
            if (pd != null && pad.attr != null)
            {
                try
                {
                    diagConsuming = pad.attr.electricConsuming;
                    var sufficient = Convert.ToSingle(Reflect.Get(pd, "powerInputSufficientFloat"));
                    diagSufficient = sufficient;
                    var powerOff = Convert.ToBoolean(Reflect.Get(pd, "powerSwitchOff"));
                    diagPowerOff = powerOff;
                    try { diagGridFactor = Convert.ToSingle(Reflect.Get(pd, "gridSupplyFactor")); } catch { }
                    powered = diagConsuming && !powerOff && sufficient > 0.01f;
                } catch (Exception ex) { Plugin.L?.LogWarning($"[TS][Teleport] 通电判定异常: {ex.Message}"); }
            }
            else { diagSufficient = -999; diagGridFactor = -999; }
            Plugin.L?.LogInfo($"[TS][Teleport] 发送方通电判定 {senderUid} consuming={diagConsuming} sufficient={diagSufficient:F2} gridFactor={diagGridFactor:F2} powerOff={diagPowerOff} powered={powered} 目的地={selectedUid}({dispTarget}) via={(viaPersisted ? "持久坐标" : "活体")}");
            if (!powered)
            {
                ShowBubble("未供电");
                Plugin.L?.LogInfo($"[TS][Teleport] 未供电 {senderUid}");
                TryFireArrivalGridDirty(senderUid);
                return;
            }
            if (!TeleportBatteryManager.HasEnoughCharge(pad))
            {
                ShowBubble("电量不足");
                Plugin.L?.LogInfo($"[TS][Teleport] 电量不足 {senderUid} sum={TeleportBatteryManager.GetTotalCharge(GetBatteryInventory(pad)):F0}");
                return;
            }

            // 通过 → 启动 5s 倒计时
            var ui = TeleportCountdownUI.EnsureExists();
            var sumBefore = TeleportBatteryManager.GetTotalCharge(TeleportBatteryManager.GetBatteryInventory(pad));
            Plugin.L?.LogInfo($"[TS][Teleport] 倒计时开始 {senderUid} sumBefore={sumBefore:F0} entrant={(inVehicle?"vehicle":"player")} target={selectedUid}({dispTarget})");
            // 捕获 UID/发送方键供回调使用（二次解析，不持有跨帧活体假设）
            string selUidCap = selectedUid;
            string senderUidCap = senderUid;
            ui.ShowCountdown(pad.transform, entrantTr, () =>
            {
                Plugin.L?.LogInfo($"[TS][Teleport] 倒计时完成回调 {senderUidCap} sumBefore2={TeleportBatteryManager.GetTotalCharge(TeleportBatteryManager.GetBatteryInventory(pad)):F0}");
                // 二次校验：选中可解析 & 发送方仍满足（目的地无在线门控，只验可解析）
                if (!ResolveTarget(selUidCap, pad, out var targetPos2, out var live2, out var via2))
                {
                    ShowBubble("目的地未知，请重选");
                    return;
                }
                if (!TeleportConsoleSelection.IsOnline(pad))
                {
                    // 发送方离线（供电丢失）
                    ShowBubble("未供电");
                    TryFireArrivalGridDirty(senderUidCap);
                    return;
                }
                if (!TeleportBatteryManager.HasEnoughCharge(pad))
                {
                    ShowBubble("电量不足");
                    return;
                }
                if (!TeleportBatteryManager.ConsumeCharge(pad, 10000f))
                {
                    ShowBubble("电量不足");
                    Plugin.L?.LogInfo($"[TS][Teleport] 扣电失败 {senderUidCap} sumAfter={TeleportBatteryManager.GetTotalCharge(TeleportBatteryManager.GetBatteryInventory(pad)):F0}");
                    return;
                }
                Plugin.L?.LogInfo($"[TS][Teleport] 扣电成功 {senderUidCap} sumAfter={TeleportBatteryManager.GetTotalCharge(TeleportBatteryManager.GetBatteryInventory(pad)):F0}");

                bool ok = false;
                try
                {
                    GameObject go = entrantComp?.gameObject ?? entrantTr?.gameObject;
                    if (go != null) ok = TeleportExecutionManager.TryTeleport(go, targetPos2);
                } catch {}
                Plugin.L?.LogInfo($"[TS][Teleport] 传送 {(ok?"成功":"失败")} entrant={(inVehicle?"vehicle":"player")} target={targetPos2.x:F1},{targetPos2.y:F1} 选中={selUidCap}({TeleportStationUid.DisplayForUid(selUidCap)}) via={(via2 ? "持久坐标" : "活体")}");
                if (!ok) ShowBubble("传送失败");
                else
                {
                    // 传后清空选择（发送方控制台）
                    try { TeleportConsoleSelection.ClearByKey(consoleKey); Plugin.L.LogInfo($"[TS][Teleport] 已清空选择 {consoleUid}"); } catch {}
                    // 落地电网自愈记点：目标站 UID＋到达时间，本次 dirty 未触发
                    try { _lastArrivalUid = selUidCap; _lastArrivalTime = Time.unscaledTime; _arrivalDirtyFired = false; } catch {}
                }
            });
            Plugin.L?.LogInfo($"[TS][Teleport] 开始倒计时 {senderUid} entrant={(inVehicle?"vehicle":"player")} target={selectedUid}({dispTarget})");
        }
        catch (Exception e) { Plugin.L?.LogWarning($"[TS][Teleport] TryStart异常: {e.Message}"); }
    }

    // 落地电网自愈：到达后 30s 内首次判未供电→单次 MarkElectricGridDirty() 逼原生重算。
    // 失败安全：直接调用包 try/catch（签名不符抛异常也不炸传送流程，且只试一次）。
    [HideFromIl2Cpp]
    private static void TryFireArrivalGridDirty(string stationUid)
    {
        try
        {
            if (_arrivalDirtyFired) return;
            if (Time.unscaledTime - _lastArrivalTime >= 30f) return;
            _arrivalDirtyFired = true; // 先置位：只试一次，不重试
            try { ProductionManager.MarkElectricGridDirty(); }
            catch { return; }
            Plugin.L?.LogInfo($"[TS][Tele] 落地电网重算已触发 站={stationUid ?? "?"} 到达站={_lastArrivalUid ?? "?"}");
        }
        catch { }
    }

    // v0.9.64 目标解析（用户定案：放弃接收方在线/离线判断）：有活体→活体坐标；
    // 无活体→持久坐标（TeleportMapStations.json 记录；无记录则 UID 自带坐标回退解析）。
    // 返回 false 仅当 UID 非法且无任何坐标来源（调用方中性气泡）。targetLive 仅供日志区分。
    private bool ResolveTarget(string selectedUid, TerrainObject senderPad, out Vector3 targetPos, out TerrainObject targetLive, out bool viaPersisted)
    {
        targetPos = default;
        targetLive = null;
        viaPersisted = false;
        try
        {
            if (!TeleportStationUid.IsUid(selectedUid)) return false;
            var live = TeleportConsoleSelection.ResolveLivePad(selectedUid);
            if (live != null)
            {
                if (senderPad != null && live == senderPad) { targetLive = live; targetPos = live.transform.position; return true; }
                targetLive = live;
                targetPos = live.transform.position + new Vector3(1.2f, 0f, 0f);
                try
                {
                    var mc = MapController.instance;
                    if (mc != null)
                    {
                        float gz = mc.GetTerrainTempHeightByWorldPosition(new Vector2(targetPos.x, targetPos.y));
                        if (Math.Abs(gz) > 0.01f) targetPos.z = gz;
                    }
                }
                catch { }
                return true;
            }
            // 无活体：按持久坐标传送（v0.9.64 删 persisted-online 门控；无记录则 UID 自带坐标回退）
            int px, py;
            if (!TeleportConsoleSelection.TryGetPersistedPos(selectedUid, out px, out py)) return false;
            Vector3 tp = new Vector3(px + 1.2f, py, 0f);
            try
            {
                var mc2 = MapController.instance;
                if (mc2 != null)
                {
                    float gz2 = mc2.GetTerrainTempHeightByWorldPosition(new Vector2(tp.x, tp.y));
                    if (Math.Abs(gz2) > 0.01f) tp.z = gz2;
                }
            }
            catch { }
            targetPos = tp;
            viaPersisted = true;
            return true;
        }
        catch { return false; }
    }

    private TerrainObject FindTargetPad(TerrainObject fromPad)
    {
        try
        {
            long padKey = GetInstanceKey(fromPad);
            long consoleKey = TeleportBindingManager.GetBoundConsole(padKey);
            if (consoleKey == 0) return null;
            // 取该控制台绑定的圆盘（应为 fromPad 自身，若控制台只绑一盘则返回自身；需找控制台绑定的另一盘？设计为 A↔a 单对，传送目标为对方控制台附近的另一盘？当前单对内无对方，暂返回配对控制台位置）
            // 简化：目标为配对控制台的位置偏移（若存在另一盘则取另一盘）
            // 尝试找与同一控制台绑定的其他圆盘（未来多对时）
            foreach (var pad in FindAllPads())
            {
                if (pad == fromPad) continue;
                long pk = GetInstanceKey(pad);
                long ck = TeleportBindingManager.GetBoundConsole(pk);
                if (ck == consoleKey) return pad;
            }
            // 无其他盘，返回控制台位置
            var console = FindByKey(consoleKey) as TerrainObject;
            return null; // 调用方会用 console 位置
        } catch { return null; }
    }

    // ===== 工具：复用 BindingManager 的查找 =====

    private static long GetInstanceKey(TerrainObject t)
    {
        try { return (long)t.GetInstanceID(); } catch { try { return (long)t.Pointer; } catch { return t.GetHashCode(); } }
    }

    // P2 收尾：只读委托统一入口（查 900101+900102 两张 0.5s TTL 缓存表，命中零扫描；未中返 null，语义同旧直扫）。
    private static TerrainObject FindByKey(long key)
    {
        try { return TeleportObjectCache.FindByKey(key); } catch { return null; }
    }

    private static List<TerrainObject> FindAllPads()
    {
        try { return TeleportObjectCache.FindAllById(900102); } catch {}
        var list = new List<TerrainObject>();
        var seen = new HashSet<long>();
        try
        {
            var all = UnityEngine.Resources.FindObjectsOfTypeAll<TerrainObject>();
            if (all != null) foreach (var t in all) if (t!=null && t.attr!=null && t.attr.id==900102) { long k=GetInstanceKey(t); if(seen.Add(k)) list.Add(t); }
        } catch {}
        try
        {
            var f = typeof(ChargerPadFix).GetField("_knownClones", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var clones = f?.GetValue(null) as System.Collections.Generic.List<object>;
            if (clones != null) foreach (var o in clones) { var c=o as Component; if(c==null) continue; var t2 = FindTerrainObject(c.transform) as TerrainObject; if(t2!=null && t2.attr!=null && t2.attr.id==900102) { long k=GetInstanceKey(t2); if(seen.Add(k)) list.Add(t2); } }
        } catch {}
        return list;
    }

    private static Component FindTerrainObject(Transform tr)
    {
        int d=0;
        while (tr!=null && d++<16) { foreach(var c in tr.GetComponents<Component>()) if(c!=null && c.GetType().Name.Contains("TerrainObject")) return c; tr=tr.parent; }
        return null;
    }

    private static object GetProductionData(TerrainObject pad)
    {
        try
        {
            // 直访 pad.objectData.productionData（dump 0xA8 → TerrainObjectData.productionData），编译期直访可靠
            var od = pad.objectData;
            if (od != null && od.productionData != null) return od.productionData;
        } catch {}
        try
        {
            var od2 = Reflect.Get(pad, "objectData");
            if (od2 != null) return Reflect.Get(od2, "productionData");
        } catch {}
        return null;
    }

    private static Component FindProduction(TerrainObject pad)
    {
        try { var tr=pad.transform; int d=0; while(tr!=null && d++<8){ foreach(var c in tr.GetComponents<Component>()) if(c!=null && c.GetType().Name.Contains("Production")) return c; tr=tr.parent; } } catch {}
        return null;
    }

    private static InventoryData GetBatteryInventory(TerrainObject pad) => TeleportBatteryManager.GetBatteryInventory(pad);

    private static object GetPlayer()
    {
        try
        {
            var gc = GameController.instance;
            if (gc!=null)
            {
                // dump.cs:33935 public HumanCharacterController playerCharacter; // 0x298 唯一玩家字段（直访，零反射）
                var pc = gc.playerCharacter;
                if (pc != null) return pc;
            }
        } catch (Exception e) { Plugin.L?.LogWarning($"[TS][Teleport] GetPlayer 直访异常: {e.Message}"); }
        // 兜底：FindWithTag 仅作最后尝试（ZED ZONE 实际无 Player tag，探针从未使用）
        try
        {
            var go = GameObject.FindWithTag("Player");
            if (go!=null) return go.GetComponent<HumanCharacterController>() ?? (object)go.GetComponent<Component>();
        } catch {}
        return null;
    }

    private static void ShowBubble(string msg)
    {
        try
        {
            // P2-4 状态缓存：与最近一次已发送文案相同则不重发（字符串比较，无拼接零查找）
            try { if (msg != null && msg == _lastBubbleMsg) return; } catch {}
            // P2-4 Type/MethodInfo 常驻缓存（BindingManager:502-503 范式）：首次解析后零查找
            try
            {
                if (_cachedBubbleType == null)
                {
                    _cachedBubbleType = AccessTools.TypeByName("BasicCharacterController");
                    if (_cachedBubbleType != null) _cachedBubbleMethod = _cachedBubbleType.GetMethod("ShowDialogueBubble", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                }
            } catch {}
            var m = _cachedBubbleMethod;
            var player = GetPlayer() as Component;
            if (player!=null && m!=null)
            {
                try { m.Invoke(player, new object[]{ msg, 4f }); } catch {}
                _lastBubbleMsg = msg;
            }
            // 找不到玩家/方法时不记 _lastBubbleMsg：下次同文案仍会重试，降级语义与原来一致
        } catch {}
    }

    // P2-4 drivingVehicle 结论缓存读取：解析一次常驻（含负结论），后续 tick 零反射零 warning
    private static BasicVehicle GetDrivingVehicle(object player)
    {
        try
        {
            if (player == null) return null;
            var t = player.GetType();
            if (!_drvResolved || !ReferenceEquals(t, _drvPlayerType))
            {
                _drvPlayerType = t;
                _drvField = null;
                try { _drvField = t.GetField("drivingVehicle", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance); } catch { _drvField = null; }
                _drvResolved = true;
            }
            if (_drvField == null) return null; // 负结论：该玩家类型无此字段（如 BasicCharacterController），走步行分支
            try { return _drvField.GetValue(player) as BasicVehicle; } catch { return null; }
        } catch { return null; }
    }
}
