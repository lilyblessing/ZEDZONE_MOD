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
            _nextScan = now + 0.2f;
            // 诊断：每 5s 打印一次扫描
            if ((int)(now) % 5 == 0 && now - _nextScan < 0.3f)
            {
                var diagPads = FindAllPads();
                Plugin.L?.LogInfo($"[TS][Teleport] 扫描 pads={diagPads.Count} player={(GetPlayer()!=null?"ok":"null")}");
            }

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
            // P6 发送方判定：已绑 + 供电(AND) + 电池≥10000；接收方只需已上线(已绑+供电)
            long padKey = GetInstanceKey(pad);
            long consoleKey = TeleportBindingManager.GetBoundConsole(padKey);
            if (consoleKey == 0)
            {
                ShowBubble("未绑定");
                Plugin.L?.LogInfo($"[TS][Teleport] 未绑定 pad={pad.name}");
                return;
            }
            var console = FindByKey(consoleKey) as TerrainObject;
            if (console == null)
            {
                ShowBubble("未绑定");
                return;
            }
            // P6 新增：未选时不触发任何传送逻辑（仅提示，不进倒计时）
            long selectedKey = TeleportConsoleSelection.GetSelectedKey(consoleKey);
            if (selectedKey == 0)
            {
                // 节流：每进入一次提示一次（由 _activePad 去重保证）
                ShowBubble("请选择目的地");
                Plugin.L?.LogInfo($"[TS][Teleport] 未选择目的地 console={consoleKey} pad={pad.name}");
                return;
            }
            var selectedPad = FindByKey(selectedKey) as TerrainObject;
            if (selectedPad == null)
            {
                ShowBubble("目的地失效");
                Plugin.L?.LogInfo($"[TS][Teleport] 目的地失效 selected={selectedKey}");
                TeleportConsoleSelection.ClearByKey(consoleKey);
                return;
            }
            // 接收方必须已上线（已绑+供电）
            if (!TeleportConsoleSelection.IsOnline(selectedPad))
            {
                ShowBubble("目的地离线");
                Plugin.L?.LogInfo($"[TS][Teleport] 目的地离线 pad={selectedPad.name} key={selectedKey}");
                return;
            }
            if (selectedPad == pad)
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
            Plugin.L?.LogInfo($"[TS][Teleport] 发送方通电判定 pad={pad.name} consuming={diagConsuming} sufficient={diagSufficient:F2} list={diagListCount} powered={powered} 目的地={selectedPad.name}在线={TeleportConsoleSelection.IsOnline(selectedPad)}");
            if (!powered)
            {
                ShowBubble("未供电");
                Plugin.L?.LogInfo($"[TS][Teleport] 未供电 pad={pad.name}");
                return;
            }
            if (!TeleportBatteryManager.HasEnoughCharge(pad))
            {
                ShowBubble("电量不足");
                Plugin.L?.LogInfo($"[TS][Teleport] 电量不足 pad={pad.name} sum={TeleportBatteryManager.GetTotalCharge(GetBatteryInventory(pad)):F0}");
                return;
            }

            // 通过 → 启动 5s 倒计时
            var ui = TeleportCountdownUI.EnsureExists();
            var sumBefore = TeleportBatteryManager.GetTotalCharge(TeleportBatteryManager.GetBatteryInventory(pad));
            Plugin.L?.LogInfo($"[TS][Teleport] 倒计时开始 pad={pad.name} sumBefore={sumBefore:F0} entrant={(inVehicle?"vehicle":"player")} target={selectedPad.name}");
            // 捕获 selectedPad/selectedKey/consoleKey 供回调使用
            TerrainObject targetPadCaptured = selectedPad;
            ui.ShowCountdown(pad.transform, entrantTr, () =>
            {
                Plugin.L?.LogInfo($"[TS][Teleport] 倒计时完成回调 pad={pad.name} sumBefore2={TeleportBatteryManager.GetTotalCharge(TeleportBatteryManager.GetBatteryInventory(pad)):F0}");
                // 二次校验：选中仍在线 & 发送方仍满足
                if (targetPadCaptured == null || !TeleportConsoleSelection.IsOnline(targetPadCaptured))
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
                    Plugin.L?.LogInfo($"[TS][Teleport] 扣电失败 pad={pad.name} sumAfter={TeleportBatteryManager.GetTotalCharge(TeleportBatteryManager.GetBatteryInventory(pad)):F0}");
                    return;
                }
                Plugin.L?.LogInfo($"[TS][Teleport] 扣电成功 pad={pad.name} sumAfter={TeleportBatteryManager.GetTotalCharge(TeleportBatteryManager.GetBatteryInventory(pad)):F0}");
                // 目标点：选中圆盘中心 +1.2m 偏移，补地形高度
                Vector3 targetPos = targetPadCaptured.transform.position + new Vector3(1.2f, 0f, 0f);
                try
                {
                    var mc = MapController.instance;
                    if (mc != null)
                    {
                        float gz = mc.GetTerrainTempHeightByWorldPosition(new Vector2(targetPos.x, targetPos.y));
                        if (Math.Abs(gz) > 0.01f) targetPos.z = gz;
                    }
                } catch {}

                bool ok = false;
                try
                {
                    GameObject go = entrantComp?.gameObject ?? entrantTr?.gameObject;
                    if (go != null) ok = TeleportExecutionManager.TryTeleport(go, targetPos);
                } catch {}
                Plugin.L?.LogInfo($"[TS][Teleport] 传送 {(ok?"成功":"失败")} entrant={(inVehicle?"vehicle":"player")} target={targetPos.x:F1},{targetPos.y:F1} 选中={targetPadCaptured.name}");
                if (!ok) ShowBubble("传送失败");
                else
                {
                    // 传后清空选择（发送方控制台）
                    try { TeleportConsoleSelection.ClearByKey(consoleKey); Plugin.L.LogInfo($"[TS][Teleport] 已清空选择 console={consoleKey}"); } catch {}
                }
            });
            Plugin.L?.LogInfo($"[TS][Teleport] 开始倒计时 pad={pad.name} entrant={(inVehicle?"vehicle":"player")} target={selectedPad.name}");
        }
        catch (Exception e) { Plugin.L?.LogWarning($"[TS][Teleport] TryStart异常: {e.Message}"); }
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
