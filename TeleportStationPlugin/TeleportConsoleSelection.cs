using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using BepInEx;

namespace TeleportStationPlugin;

/// <summary>
/// P6 控制台选目的地：selectedPad 按控制台实例键存储，持久化双轨（properties[1] + JSON），跨档隔离。
/// 已上线 = 已绑 + IsPowered(AND)；发送方可传 = 已绑 + 供电(AND) + 电池≥10000；接收方可选 = 已上线即可。
/// </summary>
public static class TeleportConsoleSelection
{
    private const int ConsoleId = 900101;
    private const int PadId = 900102;
    private static readonly Dictionary<long, long> _sel = new(); // consoleKey -> padKey
    private static float _lastSave = -999f;
    private static string SelPath => Path.Combine(Paths.ConfigPath, "TeleportSelection.json");

    // ===== 供电 AND 判定（与 PadTrigger 一致：consuming && sufficient>0.01 && list.Count>0） =====
    public static bool IsPowered(TerrainObject pad)
    {
        if (pad == null || pad.attr == null) return false;
        try
        {
            var pd = GetProductionData(pad);
            if (pd == null) return false;
            bool consuming = pad.attr.electricConsuming;
            if (!consuming) return false;
            float sufficient = Convert.ToSingle(Reflect.Get(pd, "powerInputSufficientFloat"));
            var list = Reflect.Get(pd, "connectedElectricGeneratorList") as Il2CppSystem.Collections.Generic.List<ProductionData>;
            int cnt = list != null ? list.Count : -1;
            return sufficient > 0.01f && list != null && cnt > 0;
        }
        catch { return false; }
    }

    public static bool IsOnline(TerrainObject pad)
    {
        if (pad == null) return false;
        long pk = GetInstanceKey(pad);
        if (!TeleportBindingManager.IsPadBound(pk)) return false;
        return IsPowered(pad);
    }

    public static bool IsOnlineByKey(long padKey)
    {
        var pad = FindByKey(padKey) as TerrainObject;
        return IsOnline(pad);
    }

    public static bool IsSenderReady(TerrainObject pad)
    {
        if (pad == null) return false;
        long pk = GetInstanceKey(pad);
        if (!TeleportBindingManager.IsPadBound(pk)) return false;
        if (!IsPowered(pad)) return false;
        if (!TeleportBatteryManager.HasEnoughCharge(pad)) return false;
        return true;
    }

    // ===== 选点读写 =====
    public static void SetSelected(TerrainObject console, TerrainObject targetPad)
    {
        if (console == null || targetPad == null) return;
        long ck = GetInstanceKey(console);
        long pk = GetInstanceKey(targetPad);
        _sel[ck] = pk;
        // 持久化：properties[1] = padKey string
        try
        {
            var cData = Reflect.Get(console, "objectData") ?? Reflect.Get(console, "terrainObjectData");
            if (cData != null)
            {
                var m = cData.GetType().GetMethod("SetProperty", new Type[] { typeof(int), typeof(string) });
                if (m != null) m.Invoke(cData, new object[] { 1, pk.ToString() });
            }
        } catch {}
        Save();
        Plugin.L.LogInfo($"[TS][Sel] 选中 console={ck} -> pad={pk} {targetPad.name}");
    }

    public static long GetSelectedKey(long consoleKey)
    {
        if (_sel.TryGetValue(consoleKey, out var v)) return v;
        // 懒加载：从 objectData properties[1] 读
        try
        {
            var console = FindByKey(consoleKey) as TerrainObject;
            if (console != null)
            {
                var cData = Reflect.Get(console, "objectData") ?? Reflect.Get(console, "terrainObjectData");
                if (cData != null)
                {
                    var gm = cData.GetType().GetMethod("GetProperty", new Type[] { typeof(int) });
                    if (gm != null)
                    {
                        var val = gm.Invoke(cData, new object[] { 1 }) as string;
                        if (!string.IsNullOrEmpty(val) && long.TryParse(val, out var pk))
                        {
                            _sel[consoleKey] = pk;
                            return pk;
                        }
                    }
                }
            }
        } catch {}
        return 0;
    }

    public static TerrainObject GetSelectedPad(TerrainObject console)
    {
        if (console == null) return null;
        long ck = GetInstanceKey(console);
        long pk = GetSelectedKey(ck);
        if (pk == 0) return null;
        return FindByKey(pk) as TerrainObject;
    }

    public static bool HasSelection(TerrainObject console)
    {
        if (console == null) return false;
        long ck = GetInstanceKey(console);
        long pk = GetSelectedKey(ck);
        return pk != 0;
    }

    public static void Clear(TerrainObject console)
    {
        if (console == null) return;
        long ck = GetInstanceKey(console);
        ClearByKey(ck);
    }

    public static void ClearByKey(long consoleKey)
    {
        if (_sel.ContainsKey(consoleKey)) _sel.Remove(consoleKey);
        try
        {
            var console = FindByKey(consoleKey) as TerrainObject;
            if (console != null)
            {
                var cData = Reflect.Get(console, "objectData") ?? Reflect.Get(console, "terrainObjectData");
                if (cData != null)
                {
                    var m = cData.GetType().GetMethod("SetProperty", new Type[] { typeof(int), typeof(string) });
                    if (m != null) m.Invoke(cData, new object[] { 1, "" });
                }
            }
        } catch {}
        Save();
        Plugin.L.LogInfo($"[TS][Sel] 清空 console={consoleKey}");
    }

    public static void ClearAllForPad(long padKey)
    {
        // 若某 pad 被销毁，清理所有指向它的选中
        var dead = new List<long>();
        foreach (var kv in _sel) if (kv.Value == padKey) dead.Add(kv.Key);
        foreach (var k in dead) ClearByKey(k);
    }

    // ===== 持久化 JSON（与 Binding 同款，带死键清理） =====
    private static void Save()
    {
        try
        {
            float now = Time.unscaledTime;
            if (now - _lastSave < 1f) return;
            _lastSave = now;
            var data = new Dictionary<string, long>();
            foreach (var kv in _sel) data[kv.Key.ToString()] = kv.Value;
            var json = Serialize(data);
            File.WriteAllText(SelPath, json);
        } catch {}
    }

    private static string Serialize(Dictionary<string, long> d)
    {
        var sb = new System.Text.StringBuilder("{");
        bool first = true;
        foreach (var kv in d) { if (!first) sb.Append(","); sb.Append($"\"{kv.Key}\":{kv.Value}"); first = false; }
        sb.Append("}"); return sb.ToString();
    }

    public static void Load()
    {
        try
        {
            if (!File.Exists(SelPath)) return;
            var txt = File.ReadAllText(SelPath).Trim().Trim('{', '}');
            if (string.IsNullOrWhiteSpace(txt)) return;
            var parts = txt.Split(',');
            foreach (var p in parts)
            {
                var kv = p.Split(':');
                if (kv.Length != 2) continue;
                var k = kv[0].Trim().Trim('"');
                if (long.TryParse(k, out var ck) && long.TryParse(kv[1].Trim(), out var pk))
                    _sel[ck] = pk;
            }
            Plugin.L.LogInfo($"[TS][Sel] 载入 JSON {_sel.Count} 对");
            CleanupStale();
        } catch {}
        // 再从 properties[1] 补（以 property 为准，覆盖 JSON 的陈旧值）
        try
        {
            var consoles = FindAllTerrainObjectsById(ConsoleId);
            foreach (var c in consoles)
            {
                long ck = GetInstanceKey(c);
                var cData = Reflect.Get(c, "objectData") ?? Reflect.Get(c, "terrainObjectData");
                if (cData == null) continue;
                var gm = cData.GetType().GetMethod("GetProperty", new Type[] { typeof(int) });
                if (gm == null) continue;
                var val = gm.Invoke(cData, new object[] { 1 }) as string;
                if (!string.IsNullOrEmpty(val) && long.TryParse(val, out var pk) && pk != 0)
                    _sel[ck] = pk;
                else if (string.IsNullOrEmpty(val) && _sel.ContainsKey(ck))
                {
                    // property 已清空但 JSON 还有，说明已传后清空，以 property 为准，删 JSON 键
                    _sel.Remove(ck);
                }
            }
        } catch {}
    }

    public static void CleanupStale()
    {
        try
        {
            var alive = new HashSet<long>();
            foreach (var t in FindAllTerrainObjectsById(ConsoleId)) alive.Add(GetInstanceKey(t));
            foreach (var t in FindAllTerrainObjectsById(PadId)) alive.Add(GetInstanceKey(t));
            if (alive.Count == 0) return;
            var dead = new List<long>();
            foreach (var kv in _sel) if (!alive.Contains(kv.Key) || !alive.Contains(kv.Value)) dead.Add(kv.Key);
            foreach (var k in dead) _sel.Remove(k);
            if (dead.Count > 0) Plugin.L.LogInfo($"[TS][Sel] 清理死键 {dead.Count} 对 余 {_sel.Count}");
        } catch {}
    }

    // ===== 工具（复用 Binding 的查找，避免循环依赖） =====
    private static long GetInstanceKey(TerrainObject t)
    {
        try { return (long)t.GetInstanceID(); } catch { try { return (long)t.Pointer; } catch { return t.GetHashCode(); } }
    }

    private static object GetProductionData(TerrainObject pad)
    {
        try
        {
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

    private static Component FindTerrainObject(Transform tr)
    {
        int d = 0;
        while (tr != null && d++ < 16)
        {
            foreach (var c in tr.GetComponents<Component>()) if (c != null && c.GetType().Name.Contains("TerrainObject")) return c;
            tr = tr.parent;
        }
        return null;
    }

    private static TerrainObject FindByKey(long key)
    {
        try
        {
            var f = typeof(ChargerPadFix).GetField("_knownClones", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var list = f?.GetValue(null) as List<object>;
            if (list != null) foreach (var o in list) { var comp = o as Component; if (comp == null) continue; var t = FindTerrainObject(comp.transform) as TerrainObject; if (t != null && GetInstanceKey(t) == key) return t; }
            var prods = TerrainObject_Production.ActiveObjects_Production;
            if (prods != null) for (int i=0;i<prods.Count;i++) { var g=prods[i]; if(g==null) continue; var t = FindTerrainObject(g.transform) as TerrainObject; if(t!=null && GetInstanceKey(t)==key) return t; }
            var all = UnityEngine.Resources.FindObjectsOfTypeAll<TerrainObject>();
            if (all != null) foreach (var t in all) if (t!=null && GetInstanceKey(t)==key) return t;
        } catch {}
        return null;
    }

    private static List<TerrainObject> FindAllTerrainObjectsById(int attrId)
    {
        var result = new List<TerrainObject>();
        var seen = new HashSet<long>();
        try
        {
            var f = typeof(ChargerPadFix).GetField("_knownClones", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var list = f?.GetValue(null) as List<object>;
            if (list != null) foreach (var o in list) { var c=o as Component; if(c==null) continue; var t=FindTerrainObject(c.transform) as TerrainObject; if(t!=null && t.attr!=null && t.attr.id==attrId){ long k=GetInstanceKey(t); if(seen.Add(k)) result.Add(t); } }
        } catch {}
        try { var list2 = TerrainObject_Production.ActiveObjects_Production; if(list2!=null) for(int i=0;i<list2.Count;i++){ var g=list2[i]; if(g==null) continue; var t=FindTerrainObject(g.transform) as TerrainObject; if(t!=null && t.attr!=null && t.attr.id==attrId){ long k=GetInstanceKey(t); if(seen.Add(k)) result.Add(t); } } } catch {}
        try { var all = UnityEngine.Resources.FindObjectsOfTypeAll<TerrainObject>(); if(all!=null) foreach(var t in all) if(t!=null && t.attr!=null && t.attr.id==attrId){ long k=GetInstanceKey(t); if(seen.Add(k)) result.Add(t); } } catch {}
        return result;
    }
}
