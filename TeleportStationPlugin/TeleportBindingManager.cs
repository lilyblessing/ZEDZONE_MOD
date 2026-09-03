using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using BepInEx;
using HarmonyLib;

namespace TeleportStationPlugin;

/// <summary>
/// P4 绑定管理：控制台 900101 ↔ 圆盘 900102 自动就近绑定（20m），激活判定（已绑定+通电），超距提示，存档持久化。
/// 内存表：Dict<long, long> consolePtr -> padPtr（以 TerrainObject 实例的 GetInstanceID / Pointer 为键，H&D 下 OnEnable 注册表互补）。
/// 持久化：优先 TerrainObjectData.SetProperty(int,string)，失败回退独立 JSON（BepInEx/config/TeleportBinding.json）。
/// 语义（v0.9.29 候选全量 + v0.9.30 20m 回退）：
///   已有绑定 = 想要配对的对方设备已被别的设备占用，本次配对失败；
///   已绑对反复放置：距离内→绑定成功（重申），超距→超过距离；不再一律已有绑定。
/// </summary>
public static class TeleportBindingManager
{
    private const int ConsoleId = 900101;
    private const int PadId = 900102;
    private const float BindRange = 20f;
    private const float BindRangeSqr = 20f * 20f;
    private static readonly Dictionary<long, long> _consoleToPad = new(); // console instanceId -> pad instanceId
    private static readonly Dictionary<long, long> _padToConsole = new(); // pad -> console
    private static readonly Dictionary<long, int> _instanceIdToObjId = new(); // instanceId -> attr id (for debug)
    // v0.9.61 清理宽限：连续 _staleGraceTick 次不见才删（远处未加载≠已销毁；纯运行时策略数，非游戏常数）
    private static readonly Dictionary<long, int> _staleMiss = new();
    private const int StaleGraceTicks = 3;
    private static string CoordPath => Path.Combine(Paths.ConfigPath, "TeleportBindingCoords.json");
    private static float _lastSave = -999f;
    private static float _lastHint = -999f;
    private static bool _hooksPatched = false;
    private static object _lastPlayerForHint = null;
    private static string SavePath => Path.Combine(Paths.ConfigPath, "TeleportBinding.json");

    // 延迟钩：搬运放下主钩需在 Il2Cpp 程序集加载后（GameController 存在时）再 patch，否则 TypeByName 为 null
    public static void EnsureP4Hooks()
    {
        if (_hooksPatched) return;
        try
        {
            var gc = GameController.instance;
            if (gc == null) return;
            _hooksPatched = true;
            var h2 = new Harmony("com.zedzone.teleportstation.p4b");
            var onPlace = AccessTools.Method(AccessTools.TypeByName("HumanCharacterController"), "OnPlaceTerrainObject");
            if (onPlace != null) h2.Patch(onPlace, postfix: new HarmonyMethod(typeof(TeleportBindingManager).GetMethod(nameof(OnPlaceLifted), System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)));
            var place = AccessTools.Method(typeof(TerrainObject), "PlaceTerrainObject");
            if (place != null) h2.Patch(place, postfix: new HarmonyMethod(typeof(TeleportBindingManager).GetMethod(nameof(OnPlaced), System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)));
            var placeNoCheck = AccessTools.Method(typeof(TerrainObject), "PlaceTerrainObjectWithoutCheck");
            if (placeNoCheck != null) h2.Patch(placeNoCheck, postfix: new HarmonyMethod(typeof(TeleportBindingManager).GetMethod(nameof(OnPlacedNoParam), System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)));
            Plugin.L.LogInfo("[TS] P4 搬运钩延迟 patch 完成（OnPlaceTerrainObject/PlaceTerrainObject/WithoutCheck）");
        }
        catch (Exception ex) { Plugin.L.LogWarning($"[TS] P4 延迟钩异常: {ex.Message.Split('\n')[0]}"); }
    }

    // 供 ChargerPadFix/外部查询：是否已绑定
    public static bool IsBound(long consoleInstanceId) => _consoleToPad.ContainsKey(consoleInstanceId);
    public static bool IsPadBound(long padInstanceId) => _padToConsole.ContainsKey(padInstanceId);
    public static long GetBoundPad(long consoleInstanceId) => _consoleToPad.TryGetValue(consoleInstanceId, out var v) ? v : 0;
    public static long GetBoundConsole(long padInstanceId) => _padToConsole.TryGetValue(padInstanceId, out var v) ? v : 0;

    // ===== v0.9.63 UID 身份层（实例ID仅运行时关联，UID 跨读档稳定） =====
    public static string ConsoleUid(TerrainObject c) => TeleportStationUid.UidFor(c);
    public static string PadUid(TerrainObject p) => TeleportStationUid.UidFor(p);

    // 按 UID 找活体（attr 匹配 + UID 比对；无活体返回 null，不编造）。
    public static TerrainObject FindPadByUid(string uid)
    {
        if (!TeleportStationUid.IsUid(uid)) return null;
        try
        {
            foreach (var t in FindAllTerrainObjectsById(PadId))
            {
                if (t == null) continue;
                if (TeleportStationUid.UidFor(t) == uid) return t;
            }
        }
        catch { }
        return null;
    }

    public static TerrainObject FindConsoleByKey(long key) => FindByKey(key) as TerrainObject;

    public static TerrainObject FindConsoleByUid(string uid)
    {
        if (!TeleportStationUid.IsUid(uid)) return null;
        try
        {
            foreach (var t in FindAllTerrainObjectsById(ConsoleId))
            {
                if (t == null) continue;
                if (TeleportStationUid.UidFor(t) == uid) return t;
            }
        }
        catch { }
        return null;
    }

    // 发送方配对盘 UID（运行时关联；对端未加载返回 ""）。
    public static string GetBoundPadUid(long consoleInstanceId)
    {
        try
        {
            long pk = GetBoundPad(consoleInstanceId);
            if (pk == 0) return "";
            var pad = FindByKey(pk) as TerrainObject;
            return pad != null ? TeleportStationUid.UidFor(pad) : "";
        }
        catch { return ""; }
    }

    // 激活判定（P4 精简→P6 完整）：已绑定 && 圆盘通电（pad ProductionData 有电） && 距离≤50m 附近有控制台；控制台本身无需供电
    public static bool IsActive(TerrainObject console)
    {
        if (console == null) return false;
        try
        {
            long cid = GetInstanceKey(console);
            if (!_consoleToPad.TryGetValue(cid, out var padKey)) return false;
            var pad = FindByKey(padKey) as TerrainObject;
            if (pad == null) return false;
            var dp = pad.transform.position - console.transform.position;
            if (dp.x * dp.x + dp.y * dp.y > BindRangeSqr) return false;
            var pd = GetProductionData(pad);
            if (pd != null)
            {
                try
                {
                    var sufficient = Convert.ToSingle(Reflect.Get(pd, "powerInputSufficientFloat"));
                    if (sufficient > 0.01f) return true;
                    var list = Reflect.Get(pd, "connectedElectricGeneratorList") as Il2CppSystem.Collections.Generic.List<ProductionData>;
                    if (list != null && list.Count > 0) return true;
                    return false;
                }
                catch { }
            }
            return true;
        }
        catch { return false; }
    }

    public static void OnPlaced(object __0)
    {
        try
        {
            TerrainObject placed = __0 as TerrainObject;
            if (placed == null) return;
            if (placed.attr == null) return;
            int id = placed.attr.id;
            if (id == ConsoleId) CheckAndBindForConsole(placed);
            else if (id == PadId) CheckAndBindForPad(placed);
        }
        catch { }
    }
    public static void OnPlacedNoParam(TerrainObject __instance)
    {
        try { if (__instance != null) OnPlaced(__instance); } catch { }
    }

    // HumanCharacterController.OnPlaceTerrainObject 的 postfix 入口（搬运放下主钩，非虚，RVA 0x48A6F0）
    public static void OnPlaceLifted(object __instance)
    {
        try
        {
            if (__instance != null) _lastPlayerForHint = __instance;
            TerrainObject t = null;
            if (__instance != null)
            {
                // dump.cs: public TerrainObject liftingTerrainObject; // 0x900（HumanCharacterController）
                try { t = Reflect.Get(__instance, "liftingTerrainObject") as TerrainObject; } catch { }
                if (t == null) try { t = Reflect.Get(__instance, "liftedObject") as TerrainObject; } catch { }
                if (t == null) try { t = Reflect.Get(__instance, "currentLiftedObject") as TerrainObject; } catch { }
                if (t == null) try { t = Reflect.Get(__instance, "m_liftedObject") as TerrainObject; } catch { }
                if (t == null) try { t = Reflect.Get(__instance, "placedObject") as TerrainObject; } catch { }
                if (t == null) try { t = Reflect.Get(__instance, "m_placedObject") as TerrainObject; } catch { }
            }
            if (t != null) OnPlaced(t);
            else Plugin.L.LogInfo($"[TS][Bind] OnPlaceLifted 无参版触发 __instance={__instance?.GetType().Name} t=null (liftingTerrainObject 未取到)");
        }
        catch (Exception ex) { Plugin.L.LogWarning($"[TS][Bind] OnPlaceLifted 无参异常: {ex.Message.Split('\n')[0]}"); }
    }

    private static void CheckAndBindForConsole(TerrainObject console)
    {
        try
        {
            long cid = GetInstanceKey(console);
            // 已绑定：重申逻辑（距离内→成功，超距→超距），不再一律已有绑定
            if (_consoleToPad.TryGetValue(cid, out var boundPid))
            {
                var boundPad = FindByKey(boundPid) as TerrainObject;
                if (boundPad != null)
                {
                    var d = boundPad.transform.position - console.transform.position;
                    float d2 = d.x * d.x + d.y * d.y;
                    if (d2 <= BindRangeSqr)
                    {
                        ShowHint("绑定成功", isError: false);
                        Plugin.L.LogInfo($"[TS][Bind] 绑定成功（重申） console={console.name}({cid}) -> pad={boundPad.name}({boundPid}) dist={Mathf.Sqrt(d2):F1}m");
                        return;
                    }
                    else
                    {
                        ShowHint("超过距离", isError: true);
                        Plugin.L.LogInfo($"[TS][Bind] 超过距离（已绑对拉远） console={console.name} padDist={Mathf.Sqrt(d2):F1}m >50m");
                        return;
                    }
                }
                else
                {
                    // 对端已销毁，清理陈旧映射，视作未绑定继续尝试新绑定
                    Plugin.L.LogInfo($"[TS][Bind] 清理陈旧绑定 console={cid} pad={boundPid} 对端丢失");
                    _consoleToPad.Remove(cid);
                    _padToConsole.Remove(boundPid);
                }
            }
            TryAutoBindNearest(console);
        }
        catch { }
    }

    private static void CheckAndBindForPad(TerrainObject pad)
    {
        try
        {
            long pid = GetInstanceKey(pad);
            if (_padToConsole.TryGetValue(pid, out var boundCid))
            {
                var boundConsole = FindByKey(boundCid) as TerrainObject;
                if (boundConsole != null)
                {
                    var d = boundConsole.transform.position - pad.transform.position;
                    float d2 = d.x * d.x + d.y * d.y;
                    if (d2 <= BindRangeSqr)
                    {
                        ShowHint("绑定成功", isError: false);
                        Plugin.L.LogInfo($"[TS][Bind] 绑定成功（重申） pad={pad.name}({pid}) -> console={boundConsole.name}({boundCid}) dist={Mathf.Sqrt(d2):F1}m");
                        return;
                    }
                    else
                    {
                        ShowHint("超过距离", isError: true);
                        Plugin.L.LogInfo($"[TS][Bind] 超过距离（已绑对拉远） pad={pad.name} consoleDist={Mathf.Sqrt(d2):F1}m >50m");
                        return;
                    }
                }
                else
                {
                    Plugin.L.LogInfo($"[TS][Bind] 清理陈旧绑定 pad={pid} console={boundCid} 对端丢失");
                    _padToConsole.Remove(pid);
                    _consoleToPad.Remove(boundCid);
                }
            }
            // 未绑定 pad：寻找最近未绑定控制台尝试绑定（全量扫描，含非 Production 控制台）
            var candidates = FindAllTerrainObjectsById(ConsoleId);
            TerrainObject nearestUnbound = null; float bestUnbound= float.MaxValue;
            bool hasBoundWithinRange = false;
            var pPos = pad.transform.position;
            foreach (var c in candidates)
            {
                var d = c.transform.position - pPos;
                float d2 = d.x*d.x + d.y*d.y;
                if (d2>BindRangeSqr) continue;
                long cid = GetInstanceKey(c);
                if (_consoleToPad.ContainsKey(cid)) { hasBoundWithinRange = true; continue; }
                if (d2<bestUnbound) { bestUnbound=d2; nearestUnbound=c; }
            }
            if (nearestUnbound != null) { TryAutoBindNearest(nearestUnbound); return; }
            if (hasBoundWithinRange) { ShowHint("已有绑定", isError:true); Plugin.L.LogInfo($"[TS][Bind] 已有绑定 pad={pad.name} 50m内有已绑定控制台"); return; }
            ShowHint("超过距离", isError:true);
            Plugin.L.LogInfo($"[TS][Bind] 超过距离 pad={pad.name} 50m内无控制台");
        }
        catch { }
    }

    // 自动就近绑定：对控制台 console 绑定 50m 内最近未绑定圆盘
    // 语义：已绑→重申（成功/超距），未绑→ nearestUnbound 存在→成功，无→已绑盘在范围内→已有绑定，否则超距
    public static bool TryAutoBindNearest(TerrainObject console)
    {
        if (console == null) return false;
        try
        {
            long cid = GetInstanceKey(console);
            if (console.attr == null || console.attr.id != ConsoleId) return false;
            // 已绑重申分支
            if (_consoleToPad.TryGetValue(cid, out var existingPid))
            {
                var existingPad = FindByKey(existingPid) as TerrainObject;
                if (existingPad != null)
                {
                    var d = existingPad.transform.position - console.transform.position;
                    float d2 = d.x * d.x + d.y * d.y;
                    if (d2 <= BindRangeSqr)
                    {
                        ShowHint("绑定成功", isError: false);
                        Plugin.L.LogInfo($"[TS][Bind] 绑定成功（重申 Try） console={console.name}({cid}) -> pad={existingPad.name}({existingPid}) dist={Mathf.Sqrt(d2):F1}m");
                        return true;
                    }
                    else
                    {
                        ShowHint("超过距离", isError: true);
                        Plugin.L.LogInfo($"[TS][Bind] 超过距离（重申 Try 已绑拉远） console={console.name} dist={Mathf.Sqrt(d2):F1}m");
                        return false;
                    }
                }
                else
                {
                    _consoleToPad.Remove(cid);
                    _padToConsole.Remove(existingPid);
                }
            }
            var cPos = console.transform.position;
            TerrainObject nearestUnbound = null;
            float bestUnboundD2 = float.MaxValue;
            long bestUnboundKey = 0;
            bool hasBoundWithinRange = false;
            var candidates = FindAllTerrainObjectsById(PadId);

            foreach (var pad in candidates)
            {
                var d = pad.transform.position - cPos;
                float d2 = d.x * d.x + d.y * d.y;
                if (d2 > BindRangeSqr) continue;
                long pid = GetInstanceKey(pad);
                if (_padToConsole.ContainsKey(pid)) { hasBoundWithinRange = true; continue; }
                if (d2 < bestUnboundD2) { bestUnboundD2 = d2; nearestUnbound = pad; bestUnboundKey = pid; }
            }

            if (nearestUnbound != null)
            {
                _consoleToPad[cid] = bestUnboundKey;
                _padToConsole[bestUnboundKey] = cid;
                _instanceIdToObjId[cid] = ConsoleId;
                _instanceIdToObjId[bestUnboundKey] = PadId;
                SaveForInstance(console, nearestUnbound);
                Save();
                ShowHint("绑定成功", isError: false);
                Plugin.L.LogInfo($"[TS][Bind] 绑定成功 console={console.name}({cid}) -> pad={nearestUnbound.name}({bestUnboundKey}) dist={Mathf.Sqrt(bestUnboundD2):F1}m");
                return true;
            }
            if (hasBoundWithinRange)
            {
                ShowHint("已有绑定", isError: true);
                Plugin.L.LogInfo($"[TS][Bind] 已有绑定 console={console.name} 50m内有已绑定圆盘");
                return false;
            }
            ShowHint("超过距离", isError: true);
            Plugin.L.LogInfo($"[TS][Bind] 超过距离 console={console.name} pos={cPos.x:F1},{cPos.y:F1} 50m内无圆盘");
            return false;
        }
        catch (Exception e) { Plugin.L.LogWarning($"[TS][Bind] 异常: {e.Message.Split('\n')[0]}"); return false; }
    }

    // P6.2 静默版：供自动轮询使用，仅成功时 ShowHint，不刷失败提示
    public static bool TryAutoBindNearestQuiet(TerrainObject console)
    {
        if (console == null) return false;
        try
        {
            long cid = GetInstanceKey(console);
            if (console.attr == null || console.attr.id != ConsoleId) return false;
            if (_consoleToPad.ContainsKey(cid)) return true; // 已绑视为成功，不重申刷屏
            var cPos = console.transform.position;
            TerrainObject nearestUnbound = null;
            float bestUnboundD2 = float.MaxValue;
            long bestUnboundKey = 0;
            var candidates = FindAllTerrainObjectsById(PadId);
            foreach (var pad in candidates)
            {
                var d = pad.transform.position - cPos;
                float d2 = d.x * d.x + d.y * d.y;
                if (d2 > BindRangeSqr) continue;
                long pid = GetInstanceKey(pad);
                if (_padToConsole.ContainsKey(pid)) continue;
                if (d2 < bestUnboundD2) { bestUnboundD2 = d2; nearestUnbound = pad; bestUnboundKey = pid; }
            }
            if (nearestUnbound != null)
            {
                _consoleToPad[cid] = bestUnboundKey;
                _padToConsole[bestUnboundKey] = cid;
                _instanceIdToObjId[cid] = ConsoleId;
                _instanceIdToObjId[bestUnboundKey] = PadId;
                SaveForInstance(console, nearestUnbound);
                Save();
                ShowHint("绑定成功", isError: false);
                Plugin.L.LogInfo($"[TS][Bind][Auto] 绑定成功 console={console.name}({cid}) -> pad={nearestUnbound.name}({bestUnboundKey}) dist={Mathf.Sqrt(bestUnboundD2):F1}m");
                return true;
            }
            return false;
        } catch { return false; }
    }

    public static bool TryUnbind(TerrainObject console)
    {
        if (console == null) return false;
        try
        {
            long cid = GetInstanceKey(console);
            if (!_consoleToPad.TryGetValue(cid, out var pid)) { ShowHint("未绑定", isError: true); return false; }
            var pad = FindByKey(pid) as TerrainObject;
            _consoleToPad.Remove(cid);
            _padToConsole.Remove(pid);
            try
            {
                var cData = Reflect.Get(console, "objectData") ?? Reflect.Get(console, "terrainObjectData");
                var pData = pad != null ? (Reflect.Get(pad, "objectData") ?? Reflect.Get(pad, "terrainObjectData")) : null;
                if (cData != null) { var m = cData.GetType().GetMethod("SetProperty", new Type[] { typeof(int), typeof(string) }); if (m != null) m.Invoke(cData, new object[] { 0, "" }); }
                if (pData != null) { var m2 = pData.GetType().GetMethod("SetProperty", new Type[] { typeof(int), typeof(string) }); if (m2 != null) m2.Invoke(pData, new object[] { 0, "" }); }
            }
            catch { }
            Save();
            try { TeleportConsoleSelection.ClearByKey(cid); } catch {}
            ShowHint("已解绑", isError: false);
            Plugin.L.LogInfo($"[TS][Bind] 解绑 console={cid} pad={pid} 并清空选择");
            return true;
        }
        catch { return false; }
    }

    private static System.Type _cachedBubbleType = null;
    private static System.Reflection.MethodInfo _cachedBubbleMethod = null;
    private static void ShowHint(string msg, bool isError)
    {
        try
        {
            float now = Time.unscaledTime;
            if (now - _lastHint < 1f) return;
            _lastHint = now;
            bool shown = false;
            try
            {
                if (_cachedBubbleType == null)
                {
                    _cachedBubbleType = AccessTools.TypeByName("BasicCharacterController");
                    if (_cachedBubbleType == null) _cachedBubbleType = AccessTools.TypeByName("HumanCharacterController");
                    if (_cachedBubbleType != null) _cachedBubbleMethod = _cachedBubbleType.GetMethod("ShowDialogueBubble", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                }
                var t = _cachedBubbleType;
                var m = _cachedBubbleMethod;
                object player = _lastPlayerForHint;
                try
                {
                    if (player == null)
                    {
                        var gc = GameController.instance;
                        if (gc != null)
                        {
                            player = Reflect.Get(gc, "player");
                            if (player == null) player = Reflect.Get(gc, "localPlayer");
                            if (player == null) player = Reflect.Get(gc, "controlledCharacter");
                            if (player == null) player = Reflect.Get(gc, "mainCharacter");
                        }
                    }
                }
                catch { }
                if (player == null)
                {
                    try
                    {
                        var go = GameObject.FindWithTag("Player");
                        if (go != null && t != null)
                        {
                            foreach (var c in go.GetComponents<Component>()) if (c != null && c.GetType().Name == t.Name) { player = c; break; }
                            if (player == null) foreach (var c in go.GetComponentsInChildren<Component>(true)) if (c != null && c.GetType().Name == t.Name) { player = c; break; }
                        }
                    }
                    catch { }
                }
                var bubbleMethod = _cachedBubbleMethod;
                if (player != null && bubbleMethod != null)
                {
                    bubbleMethod.Invoke(player, new object[] { msg, 4f });
                    shown = true;
                    Plugin.L.LogInfo($"[TS][Hint][Bubble] {msg}");
                }
                else if (player == null) Plugin.L.LogWarning($"[TS][Hint] Bubble 未找到玩家 t={t?.Name} m={bubbleMethod?.Name}");
                else if (bubbleMethod == null) Plugin.L.LogWarning($"[TS][Hint] Bubble 方法为空 t={t?.Name}");
            }
            catch (Exception ex) { Plugin.L.LogWarning($"[TS][Hint] Bubble 失败: {ex.Message.Split('\n')[0]}"); }
            try
            {
                if (!shown)
                {
                    var t = AccessTools.TypeByName("SystemNotificationPanel");
                    var inst = t?.GetProperty("instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null);
                    var m = t?.GetMethod("ShowNotification", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    if (inst != null && m != null) { m.Invoke(inst, new object[] { msg, true, null, null }); shown = true; }
                }
            }
            catch { }
            if (!shown) Plugin.L.LogInfo($"[TS][Hint] {(isError? "[超距] ":"")}{msg}");
        }
        catch { }
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

    private static object GetProductionData(TerrainObject t)
    {
        try
        {
            var od = Reflect.Get(t, "objectData");
            if (od == null) return null;
            return Reflect.Get(od, "productionData");
        }
        catch { return null; }
    }

    private static TerrainObject FindByKey(long key)
    {
        try
        {
            var f = typeof(ChargerPadFix).GetField("_knownClones", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var list = f?.GetValue(null) as System.Collections.Generic.List<object>;
            if (list != null)
            {
                foreach (var o in list)
                {
                    var comp = o as Component;
                    if (comp == null) continue;
                    var t = FindTerrainObject(comp.transform) as TerrainObject;
                    if (t != null && GetInstanceKey(t) == key) return t;
                }
            }
            var list2 = TerrainObject_Production.ActiveObjects_Production;
            if (list2 != null)
            {
                for (int i = 0; i < list2.Count; i++)
                {
                    var g = list2[i];
                    if (g == null) continue;
                    var t = FindTerrainObject(g.transform) as TerrainObject;
                    if (t != null && GetInstanceKey(t) == key) return t;
                }
            }
            // 兜底：全量扫描（含非 Production 的控制台）
            try
            {
                var all = UnityEngine.Resources.FindObjectsOfTypeAll<TerrainObject>();
                if (all != null) foreach (var t in all) if (t != null && GetInstanceKey(t) == key) return t;
            } catch {}
        }
        catch { }
        return null;
    }

    private static List<TerrainObject> FindAllTerrainObjectsById(int attrId)
    {
        // P6.1 性能：统一走 0.5s 缓存（TeleportObjectCache），原每帧 4× Resources 导致 30 帧
        try { return TeleportObjectCache.FindAllById(attrId); } catch {}
        var result = new List<TerrainObject>();
        var seen = new HashSet<long>();
        try
        {
            var f = typeof(ChargerPadFix).GetField("_knownClones", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var list = f?.GetValue(null) as System.Collections.Generic.List<object>;
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
        try
        {
            var list3 = TerrainObject_Production_StirlingGenerator.ActiveObjects_StirlingGenerator;
            if (list3 != null) for (int i=0;i<list3.Count;i++) { var g=list3[i]; if(g==null) continue; var t=FindTerrainObject(g.transform) as TerrainObject; if(t!=null && t.attr!=null && t.attr.id==attrId) { long k=GetInstanceKey(t); if(seen.Add(k)) result.Add(t); } }
        } catch {}
        return result;
    }

    private static void SaveForInstance(TerrainObject console, TerrainObject pad)
    {
        try
        {
            var cData = Reflect.Get(console, "objectData") ?? Reflect.Get(console, "terrainObjectData");
            var pData = Reflect.Get(pad, "objectData") ?? Reflect.Get(pad, "terrainObjectData");
            string cKey = GetInstanceKey(console).ToString();
            string pKey = GetInstanceKey(pad).ToString();
            if (cData != null)
            {
                try { var m = cData.GetType().GetMethod("SetProperty", new Type[] { typeof(int), typeof(string) }); if (m != null) m.Invoke(cData, new object[] { 0, pKey }); } catch { }
            }
            if (pData != null)
            {
                try { var m2 = pData.GetType().GetMethod("SetProperty", new Type[] { typeof(int), typeof(string) }); if (m2 != null) m2.Invoke(pData, new object[] { 0, cKey }); } catch { }
            }
            Plugin.L.LogInfo($"[TS][Bind] 保存（properties[0]） console={cKey} pad={pKey}");
        }
        catch (Exception e) { Plugin.L.LogWarning($"[TS][Bind] 保存异常: {e.Message.Split('\n')[0]}"); }
    }

    private static void Save()
    {
        try
        {
            float now = Time.unscaledTime;
            if (now - _lastSave < 1f) return;
            _lastSave = now;
            var data = new Dictionary<string, long>();
            foreach (var kv in _consoleToPad) data[kv.Key.ToString()] = kv.Value;
            var json = SimpleJson.Serialize(data);
            File.WriteAllText(SavePath, json);
            Plugin.L.LogInfo($"[TS][Bind] 保存 JSON {SavePath} {_consoleToPad.Count} 对");
            try { SaveCoords(); } catch {}
        }
        catch (Exception e) { Plugin.L.LogWarning($"[TS][Bind] 保存异常: {e.Message.Split('\n')[0]}"); }
    }

    // v0.9.61 坐标对持久化：实例ID跨读档必变，坐标不变。存 "ccx,ccy>pcx,pcy" 列表；Load 时按坐标回链活体。
    // 坐标经 transform.position 编译期直访，无反射。
    public static string CoordKey(TerrainObject t)
    {
        try { var p = t.transform.position; return $"{Mathf.RoundToInt(p.x)},{Mathf.RoundToInt(p.y)}"; }
        catch { return ""; }
    }

    private static void SaveCoords()
    {
        try
        {
            var sb = new System.Text.StringBuilder("{\"v\":1,\"pairs\":[");
            bool first = true;
            int n = 0;
            foreach (var kv in _consoleToPad)
            {
                var c = FindByKey(kv.Key) as TerrainObject;
                var p = FindByKey(kv.Value) as TerrainObject;
                if (c == null || p == null) continue;
                string cc = CoordKey(c), pc = CoordKey(p);
                if (string.IsNullOrEmpty(cc) || string.IsNullOrEmpty(pc)) continue;
                if (!first) sb.Append(",");
                sb.Append($"\"{cc}>{pc}\"");
                first = false;
                n++;
            }
            sb.Append("]}");
            File.WriteAllText(CoordPath, sb.ToString());
            if (n > 0) Plugin.L.LogInfo($"[TS][Bind] 保存坐标对 {CoordPath} {n} 对");
        } catch (Exception e) { Plugin.L.LogWarning($"[TS][Bind] 保存坐标对异常: {e.Message.Split('\n')[0]}"); }
    }

    // 按坐标找活体（attr 匹配 + 坐标匹配），用于跨读档回链。
    private static TerrainObject FindByCoord(int attrId, string coord)
    {
        if (string.IsNullOrEmpty(coord)) return null;
        try
        {
            foreach (var t in FindAllTerrainObjectsById(attrId))
            {
                if (t == null) continue;
                if (CoordKey(t) == coord) return t;
            }
        } catch {}
        return null;
    }

    private static class SimpleJson
    {
        public static string Serialize(Dictionary<string, long> d)
        {
            var sb = new System.Text.StringBuilder("{");
            bool first = true;
            foreach (var kv in d) { if (!first) sb.Append(","); sb.Append($"\"{kv.Key}\":{kv.Value}"); first = false; }
            sb.Append("}"); return sb.ToString();
        }
    }

    public static void Load()
    {
        try
        {
            if (!File.Exists(SavePath)) return;
            var txt = File.ReadAllText(SavePath);
            txt = txt.Trim().Trim('{', '}');
            if (string.IsNullOrWhiteSpace(txt)) return;
            var parts = txt.Split(',');
            foreach (var p in parts)
            {
                var kv = p.Split(':');
                if (kv.Length != 2) continue;
                var k = kv[0].Trim().Trim('"');
                if (long.TryParse(k, out var ck) && long.TryParse(kv[1].Trim(), out var pk))
                {
                    _consoleToPad[ck] = pk;
                    _padToConsole[pk] = ck;
                }
            }
            Plugin.L.LogInfo($"[TS][Bind] 载入 JSON {_consoleToPad.Count} 对");
            // v0.9.61 坐标对回链（跨读档：实例ID已变，用坐标找回活体对，重建内存映射）
            try
            {
                if (File.Exists(CoordPath))
                {
                    var ctxt = File.ReadAllText(CoordPath);
                    int linked = 0;
                    foreach (var pair in ParsePairs(ctxt))
                    {
                        var sep = pair.IndexOf('>');
                        if (sep < 0) continue;
                        string cc = pair.Substring(0, sep), pc = pair.Substring(sep + 1);
                        var c = FindByCoord(ConsoleId, cc);
                        var p = FindByCoord(PadId, pc);
                        if (c == null || p == null) continue;
                        long ck = GetInstanceKey(c), pk = GetInstanceKey(p);
                        if (_consoleToPad.TryGetValue(ck, out var oldPk) && oldPk == pk) continue;
                        // 若两端已被别的映射占用，不抢占（沿用“已有绑定”语义）
                        if (_consoleToPad.ContainsKey(ck) || _padToConsole.ContainsKey(pk)) continue;
                        _consoleToPad[ck] = pk;
                        _padToConsole[pk] = ck;
                        _instanceIdToObjId[ck] = ConsoleId;
                        _instanceIdToObjId[pk] = PadId;
                        linked++;
                    }
                    if (linked > 0) Plugin.L.LogInfo($"[TS][Bind] 坐标回链 {linked} 对，内存现 {_consoleToPad.Count} 对");
                }
            } catch (Exception e2) { Plugin.L.LogWarning($"[TS][Bind] 坐标回链异常: {e2.Message.Split('\n')[0]}"); }
            // v0.9.61 Load 期不再做死键清理：启动时活体表不全（远处/未加载）且实例ID跨档必变，
            // 此处清理即“载入2条→清理死键2→余0”（日志铁证），清理由运行时 CleanupStale（带宽限）负责。
        }
        catch { }
    }

    // 解析 {"v":1,"pairs":["1,2>3,4",...]} 中的 pair 串
    private static List<string> ParsePairs(string json)
    {
        var res = new List<string>();
        try
        {
            if (string.IsNullOrWhiteSpace(json)) return res;
            int bi = json.IndexOf('[');
            int ei = json.LastIndexOf(']');
            if (bi < 0 || ei < 0 || ei <= bi) return res;
            string inner = json.Substring(bi + 1, ei - bi - 1);
            foreach (var part in inner.Split(','))
            {
                string s = part.Trim().Trim('"').Trim();
                if (!string.IsNullOrEmpty(s) && s.Contains(">")) res.Add(s);
            }
        } catch {}
        return res;
    }

    public static void CleanupStale()
    {
        try
        {
            var alive = new HashSet<long>();
            foreach(var t in FindAllTerrainObjectsById(ConsoleId)) alive.Add(GetInstanceKey(t));
            foreach(var t in FindAllTerrainObjectsById(PadId)) alive.Add(GetInstanceKey(t));
            if(alive.Count==0) return;
            var dead=new List<long>();
            // v0.9.61 宽限：连续 StaleGraceTicks 次不见才删；见一次清零（远处未加载≠销毁）。
            foreach(var kv in _consoleToPad)
            {
                bool hit = alive.Contains(kv.Key) && alive.Contains(kv.Value);
                if (hit) { _staleMiss.Remove(kv.Key); continue; }
                int m = 0;
                _staleMiss.TryGetValue(kv.Key, out m);
                m++;
                if (m >= StaleGraceTicks) dead.Add(kv.Key);
                else _staleMiss[kv.Key] = m;
            }
            foreach(var k in dead){ if(_consoleToPad.TryGetValue(k,out var v)) _padToConsole.Remove(v); _consoleToPad.Remove(k); _staleMiss.Remove(k); }
            if(dead.Count>0) Plugin.L.LogInfo($"[TS][Bind] Tick 清理死键 {dead.Count} 对（宽限{StaleGraceTicks}轮）");
        } catch {}
    }
}

/// <summary>
/// P6.2 轮询控制器：20m 自动互绑（无 E 绑定），原版 E/Q 保留（E 走 ComputerPanel，Q 移动）。
/// 每秒对未绑控制台尝试就近绑定静默版，仅成功时提示。
/// </summary>
public class TeleportBindingController : MonoBehaviour
{
    private float _nextCleanup = -1f;
    private float _nextAutoBind = -1f;
    void Update()
    {
        try
        {
            TeleportBindingManager.EnsureP4Hooks();
            float now = Time.unscaledTime;
            if (now >= _nextCleanup)
            {
                _nextCleanup = now + 1f;
                try { TeleportBindingManager.CleanupStale(); } catch {}
                try { TeleportConsoleSelection.CleanupStale(); } catch {}
                try { TeleportStationNameManager.CleanupStale(); } catch {}
            }
            if (now >= _nextAutoBind)
            {
                _nextAutoBind = now + 1f; // 1Hz 自动互绑
                try { AutoBindTick(); } catch {}
            }
            // P6.1 的 E/H 绑定/选点逻辑已退役：E 由原生 ComputerPanel 接管，选点改走地图标记
            // 保留 H 关闭旧面板兼容（若 TeleportConsoleUI 仍打开）
            try
            {
                var ui = TeleportConsoleUI.Instance;
                if (ui != null && ui.IsOpen && Input.GetKeyDown(KeyCode.H)) { try { ui.Close(); } catch {} }
            } catch {}
        }
        catch { }
    }

    private void AutoBindTick()
    {
        try
        {
            var consoles = TeleportObjectCache.FindAllById(900101);
            if (consoles == null || consoles.Count == 0) return;
            foreach (var c in consoles)
            {
                if (c == null || c.transform == null || c.attr == null) continue;
                long ck = GetInstanceKey(c);
                if (TeleportBindingManager.IsBound(ck)) continue;
                // 静默尝试，仅成功打日志，不刷“超过距离/已有绑定”提示
                try { TeleportBindingManager.TryAutoBindNearestQuiet(c); } catch {}
            }
        } catch {}
    }

    private static long GetInstanceKey(TerrainObject t)
    {
        try { return (long)t.GetInstanceID(); } catch { try { return (long)t.Pointer; } catch { return t.GetHashCode(); } }
    }

    private Transform GetPlayerTransform()
    {
        try
        {
            var gc = GameController.instance;
            if (gc != null)
            {
                var p = Reflect.Get(gc, "player") as Component;
                if (p != null) return p.transform;
                var pl = Reflect.Get(gc, "localPlayer") as Component;
                if (pl != null) return pl.transform;
            }
            var go = GameObject.FindWithTag("Player");
            if (go != null) return go.transform;
        }
        catch { }
        return null;
    }

    private TerrainObject FindNearestConsole(Vector3 pos, float maxDist)
    {
        try
        {
            float best = maxDist * maxDist;
            TerrainObject bestObj = null;
            // P6.1 性能：走 0.5s 缓存，单次扫描而非三源分散扫描
            var candidates = TeleportObjectCache.FindAllById(900101);
            foreach (var t in candidates)
            {
                if (t == null || t.transform == null) continue;
                var d = t.transform.position - pos;
                float d2 = d.x * d.x + d.y * d.y;
                if (d2 < best) { best = d2; bestObj = t; }
            }
            return bestObj;
        }
        catch { return null; }
    }

    private Component FindTerrainObject(Transform tr)
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
}
