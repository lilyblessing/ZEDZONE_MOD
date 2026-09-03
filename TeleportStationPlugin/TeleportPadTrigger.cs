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
            // 诊断：已关闭高频扫描日志（原每5s刷屏，导致日志误判为错误）
            // 仅在需要时手动打开：if ((int)(now) % 30 == 0) Log...

            // 若倒计时进行中，跳过新触发，让 CountdownUI 处理脱离取消
            if (TeleportCountdownUI.Instance != null && TeleportCountdownUI.Instance.IsCounting) return;

            var player = GetPlayer();
            if (player == null) return;
            var playerTr = (player as Component)?.transform;
            if (playerTr == null) return;

            // 判断是否在驾驶载具
            BasicVehicle vehicle = null;
            try { vehicle = Reflect.Get(player, "drivingVehicle") as BasicVehicle; } catch {}
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
                    TryStartTeleport(nearestPad, entrant, entrantTr, inVehicle);
                }
            }
            else
            {
                // 离开所有 pad 范围
                _activePad = null;
                _activeEntrant = null;
                _activeEntrantTr = null;
            }
        }
        catch {}
    }

    [HideFromIl2Cpp]
    private void TryStartTeleport(TerrainObject pad, Component entrantComp, Transform entrantTr, bool inVehicle)
    {
        try
        {
            // P6 发送方判定：已绑 + 供电(AND) + 电池≥10000；接收方只需在线（活体实时 or 持久在线）。
            // v0.9.63 目的地以 UID 识别：有活体走活体坐标，无活体（未加载/读档后）且 persisted-online=true
            // 即按持久坐标传送（坐标直传 P0 验证期已验证：加载存档后直接传送可行）。无走近/未加载门控。
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
                if (targetLive == null && !viaPersisted)
                {
                    ShowBubble("目的地离线");
                    Plugin.L?.LogInfo($"[TS][Teleport] 目的地离线 {selectedUid}({dispTarget}) live=无 persistedOnline=False");
                }
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
            float diagSufficient = -1f; int diagListCount = -1; bool diagConsuming = false;
            if (pd != null && pad.attr != null)
            {
                try
                {
                    diagConsuming = pad.attr.electricConsuming;
                    var sufficient = Convert.ToSingle(Reflect.Get(pd, "powerInputSufficientFloat"));
                    diagSufficient = sufficient;
                    var list = Reflect.Get(pd, "connectedElectricGeneratorList") as Il2CppSystem.Collections.Generic.List<ProductionData>;
                    diagListCount = list != null ? list.Count : -1;
                    powered = diagConsuming && sufficient > 0.01f && list != null && list.Count > 0;
                } catch (Exception ex) { Plugin.L?.LogWarning($"[TS][Teleport] 通电判定异常: {ex.Message}"); }
            }
            else { diagSufficient = -999; diagListCount = -999; }
            Plugin.L?.LogInfo($"[TS][Teleport] 发送方通电判定 {senderUid} consuming={diagConsuming} sufficient={diagSufficient:F2} list={diagListCount} powered={powered} 目的地={selectedUid}({dispTarget})在线=True via={(viaPersisted ? "持久坐标" : "活体")}");
            if (!powered)
            {
                ShowBubble("未供电");
                Plugin.L?.LogInfo($"[TS][Teleport] 未供电 {senderUid}");
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
                // 二次校验：选中仍在线 & 发送方仍满足
                if (!ResolveTarget(selUidCap, pad, out var targetPos2, out var live2, out var via2))
                {
                    ShowBubble("目的地离线");
                    return;
                }
                if (!TeleportConsoleSelection.IsOnline(pad))
                {
                    // 发送方离线（供电丢失）
                    ShowBubble("未供电");
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
                }
            });
            Plugin.L?.LogInfo($"[TS][Teleport] 开始倒计时 {senderUid} entrant={(inVehicle?"vehicle":"player")} target={selectedUid}({dispTarget})");
        }
        catch (Exception e) { Plugin.L?.LogWarning($"[TS][Teleport] TryStart异常: {e.Message}"); }
    }

    // v0.9.63 目标解析：有活体→活体坐标；无活体但 persisted-online → 持久坐标（+1.2m 偏移+地形高）。
    // 返回 false = 离线/从未在线（调用方气泡"目的地离线"）。targetLive 仅供调用方区分日志。
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
                if (!TeleportConsoleSelection.IsOnlineUid(selectedUid)) return false;
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
            // 无活体：持久在线即按持久坐标传送（删"走近/未加载"门控）
            if (!TeleportConsoleSelection.IsOnlineUid(selectedUid)) return false;
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

    private static TerrainObject FindByKey(long key)
    {
        try
        {
            var all = UnityEngine.Resources.FindObjectsOfTypeAll<TerrainObject>();
            if (all != null) foreach (var t in all) if (t!=null && GetInstanceKey(t)==key) return t;
        } catch {}
        return null;
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
            var t = AccessTools.TypeByName("BasicCharacterController");
            var m = t?.GetMethod("ShowDialogueBubble", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            var player = GetPlayer() as Component;
            if (player!=null && m!=null) m.Invoke(player, new object[]{ msg, 4f });
        } catch {}
    }
}
