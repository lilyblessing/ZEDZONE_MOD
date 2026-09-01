using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using BepInEx;
using HarmonyLib;

namespace TeleportStationPlugin;

/// <summary>
/// P4 绑定管理：控制台 900101 ↔ 圆盘 900102 自动就近绑定（50m），激活判定（已绑定+通电），超距提示，存档持久化。
/// 内存表：Dict<long, long> consolePtr -> padPtr（以 TerrainObject 实例的 GetInstanceID / Pointer 为键，H&D 下 OnEnable 注册表互补）。
/// 持久化：优先 TerrainObjectData.SetProperty(int,string)，失败回退独立 JSON（BepInEx/config/TeleportBinding.json）。
/// 语义（v0.9.28 修正）：
///   已有绑定 = 想要配对的对方设备已被别的设备占用，本次配对失败；
///   已绑对反复放置：距离内→绑定成功（重申），超距→超过距离；不再一律已有绑定。
/// </summary>
public static class TeleportBindingManager
{
    private const int ConsoleId = 900101;
    private const int PadId = 900102;
    private const float BindRange = 50f;
    private const float BindRangeSqr = 50f * 50f;
    private static readonly Dictionary<long, long> _consoleToPad = new(); // console instanceId -> pad instanceId
    private static readonly Dictionary<long, long> _padToConsole = new(); // pad -> console
    private static readonly Dictionary<long, int> _instanceIdToObjId = new(); // instanceId -> attr id (for debug)
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
            ShowHint("已解绑", isError: false);
            Plugin.L.LogInfo($"[TS][Bind] 解绑 console={cid} pad={pid}");
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
        var result = new List<TerrainObject>();
        var seen = new HashSet<long>();
        try
        {
            // 1. _knownClones（含 H&D）
            var f = typeof(ChargerPadFix).GetField("_knownClones", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var list = f?.GetValue(null) as System.Collections.Generic.List<object>;
            if (list != null) foreach (var o in list) { var c = o as Component; if (c==null) continue; var t = FindTerrainObject(c.transform) as TerrainObject; if (t!=null && t.attr!=null && t.attr.id==attrId) { long k=GetInstanceKey(t); if(seen.Add(k)) result.Add(t); } }
        } catch {}
        try
        {
            // 2. ActiveObjects_Production（消费端/充电台等）
            var list2 = TerrainObject_Production.ActiveObjects_Production;
            if (list2 != null) for (int i=0;i<list2.Count;i++) { var g=list2[i]; if(g==null) continue; var t=FindTerrainObject(g.transform) as TerrainObject; if(t!=null && t.attr!=null && t.attr.id==attrId) { long k=GetInstanceKey(t); if(seen.Add(k)) result.Add(t); } }
        } catch {}
        try
        {
            // 3. 全量扫描（覆盖 900101 控制台等非 Production 类型）
            var all = UnityEngine.Resources.FindObjectsOfTypeAll<TerrainObject>();
            if (all != null) foreach (var t in all) if (t!=null && t.attr!=null && t.attr.id==attrId) { long k=GetInstanceKey(t); if(seen.Add(k)) result.Add(t); }
        } catch {}
        try
        {
            // 4. ActiveObjects_StirlingGenerator 也可能包含克隆
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
        }
        catch (Exception e) { Plugin.L.LogWarning($"[TS][Bind] 保存异常: {e.Message.Split('\n')[0]}"); }
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
            // 死键清理：仅保留当前场景活体（全量扫描，含非 Production 控制台）
            try
            {
                var alive = new HashSet<long>();
                foreach(var t in FindAllTerrainObjectsById(ConsoleId)) alive.Add(GetInstanceKey(t));
                foreach(var t in FindAllTerrainObjectsById(PadId)) alive.Add(GetInstanceKey(t));
                if (alive.Count>0)
                {
                    var dead = new List<long>();
                    foreach(var kv in _consoleToPad) if(!alive.Contains(kv.Key) || !alive.Contains(kv.Value)) dead.Add(kv.Key);
                    foreach(var k in dead){ if(_consoleToPad.TryGetValue(k,out var v)){ _padToConsole.Remove(v); } _consoleToPad.Remove(k); }
                    if(dead.Count>0) Plugin.L.LogInfo($"[TS][Bind] 清理死键 {dead.Count} 对，余 {_consoleToPad.Count} 对");
                }
            } catch {}
        }
        catch { }
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
            foreach(var kv in _consoleToPad) if(!alive.Contains(kv.Key) || !alive.Contains(kv.Value)) dead.Add(kv.Key);
            foreach(var k in dead){ if(_consoleToPad.TryGetValue(k,out var v)) _padToConsole.Remove(v); _consoleToPad.Remove(k); }
            if(dead.Count>0) Plugin.L.LogInfo($"[TS][Bind] Tick 清理死键 {dead.Count} 对");
        } catch {}
    }
}

/// <summary>
/// P4 轮询控制器：玩家靠近控制台按 E 自动就近绑定（50m），H 键解绑；每帧零分配，失败静默。
/// </summary>
public class TeleportBindingController : MonoBehaviour
{
    private float _nextCheck = -1f;
    void Update()
    {
        try
        {
            TeleportBindingManager.EnsureP4Hooks();
            TeleportBindingManager.CleanupStale();
            if (Time.unscaledTime < _nextCheck) return;
            _nextCheck = Time.unscaledTime + 0.2f;
            if (!Input.GetKeyDown(KeyCode.E) && !Input.GetKeyDown(KeyCode.H)) return;
            var player = GetPlayerTransform();
            if (player == null) return;
            var console = FindNearestConsole(player.position, 3f);
            if (console == null) return;
            if (Input.GetKeyDown(KeyCode.E)) TeleportBindingManager.TryAutoBindNearest(console);
            else if (Input.GetKeyDown(KeyCode.H)) TeleportBindingManager.TryUnbind(console);
        }
        catch { }
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
            var list = TerrainObject_Production.ActiveObjects_Production;
            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    var g = list[i];
                    if (g == null) continue;
                    var t = Reflect.Get(g, "terrainObject") as TerrainObject;
                    if (t == null) t = FindTerrainObject(g.transform) as TerrainObject;
                    if (t == null || t.attr == null || t.attr.id != 900101) continue;
                    var d = t.transform.position - pos;
                    float d2 = d.x * d.x + d.y * d.y;
                    if (d2 < best) { best = d2; bestObj = t; }
                }
            }
            try
            {
                var f = typeof(ChargerPadFix).GetField("_knownClones", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                var lst = f?.GetValue(null) as System.Collections.Generic.List<object>;
                if (lst != null)
                {
                    foreach (var o in lst)
                    {
                        var comp = o as Component;
                        if (comp == null) continue;
                        var t = FindTerrainObject(comp.transform) as TerrainObject;
                        if (t == null || t.attr == null || t.attr.id != 900101) continue;
                        var d = t.transform.position - pos;
                        float d2 = d.x * d.x + d.y * d.y;
                        if (d2 < best) { best = d2; bestObj = t; }
                    }
                }
            }
            catch { }
            try
            {
                var all = UnityEngine.Resources.FindObjectsOfTypeAll<TerrainObject>();
                if (all != null) foreach (var t in all) if (t!=null && t.attr!=null && t.attr.id==900101) { var d = t.transform.position - pos; float d2 = d.x*d.x+d.y*d.y; if (d2 < best) { best=d2; bestObj=t; } }
            } catch {}
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
