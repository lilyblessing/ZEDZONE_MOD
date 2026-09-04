using System;
using System.Collections.Generic;
using UnityEngine;

namespace TeleportStationPlugin;

/// <summary>
/// P6.1 性能修复：FindAll 去重扫描统一缓存（0.5s TTL），消除每帧 4× Resources.FindObjectsOfTypeAll 的 60Hz 轮询风暴。
/// 合并 4 源：_knownClones + ActiveObjects_Production + Resources.FindObjectsOfTypeAll + ActiveObjects_Production
/// </summary>
public static class TeleportObjectCache
{
    private const float TTL = 0.5f;
    // P2-6：统一 FindByKey 入口覆盖的两表（与 TeleportBindingManager.ConsoleId/PadId 同值，缓存侧私有常量）
    private const int ConsoleAttrId = 900101;
    private const int PadAttrId = 900102;
    private static readonly Dictionary<int, List<TerrainObject>> _cache = new();
    private static readonly Dictionary<int, float> _cacheTime = new();

    public static List<TerrainObject> FindAllById(int attrId)
    {
        float now = 0f;
        try { now = Time.unscaledTime; } catch { now = UnityEngine.Time.realtimeSinceStartup; }
        if (_cache.TryGetValue(attrId, out var cached) && _cacheTime.TryGetValue(attrId, out var t) && cached != null)
        {
            if (now - t < TTL) return new List<TerrainObject>(cached);
        }
        var computed = Compute(attrId);
        _cache[attrId] = new List<TerrainObject>(computed);
        _cacheTime[attrId] = now;
        return computed;
    }

    /// <summary>
    /// P2-6 统一入口：按键找活体，供外部调用；复用 0.5s TTL 缓存（控制台 + 圆盘两表），命中零扫描。
    /// 未命中返回 null（调用方按原语义处理）；全程 try/catch，行为保底。
    /// </summary>
    public static TerrainObject FindByKey(long key)
    {
        try
        {
            if (key == 0) return null;
            var consoles = FindAllById(ConsoleAttrId);
            if (consoles != null)
            {
                for (int i = 0; i < consoles.Count; i++)
                {
                    var t = consoles[i];
                    if (t == null) continue;
                    try { if (GetInstanceKey(t) == key) return t; } catch {}
                }
            }
            var pads = FindAllById(PadAttrId);
            if (pads != null)
            {
                for (int i = 0; i < pads.Count; i++)
                {
                    var t = pads[i];
                    if (t == null) continue;
                    try { if (GetInstanceKey(t) == key) return t; } catch {}
                }
            }
        }
        catch { }
        return null;
    }

    public static void Invalidate(int attrId)
    {
        _cache.Remove(attrId);
        _cacheTime.Remove(attrId);
    }

    public static void InvalidateAll()
    {
        _cache.Clear();
        _cacheTime.Clear();
    }

    private static List<TerrainObject> Compute(int attrId)
    {
        var result = new List<TerrainObject>();
        var seen = new HashSet<long>();
        // 1. _knownClones（含 H&D 隐藏对象）
        try
        {
            var f = typeof(ChargerPadFix).GetField("_knownClones", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var list = f?.GetValue(null) as List<object>;
            if (list != null)
            {
                foreach (var o in list)
                {
                    var c = o as Component;
                    if (c == null) continue;
                    var t = FindTerrainObject(c.transform) as TerrainObject;
                    if (t != null && t.attr != null && t.attr.id == attrId)
                    {
                        long k = GetInstanceKey(t);
                        if (seen.Add(k)) result.Add(t);
                    }
                }
            }
        } catch {}
        // 2. ActiveObjects_Production
        try
        {
            var list2 = TerrainObject_Production.ActiveObjects_Production;
            if (list2 != null)
            {
                for (int i = 0; i < list2.Count; i++)
                {
                    var g = list2[i];
                    if (g == null) continue;
                    var t = FindTerrainObject(g.transform) as TerrainObject;
                    if (t == null) try { t = Reflect.Get(g, "terrainObject") as TerrainObject; } catch {}
                    if (t != null && t.attr != null && t.attr.id == attrId)
                    {
                        long k = GetInstanceKey(t);
                        if (seen.Add(k)) result.Add(t);
                    }
                }
            }
        } catch {}
        // 3. Resources 全量（兜底，覆盖 900101 非 Production 控制台）
        try
        {
            var all = UnityEngine.Resources.FindObjectsOfTypeAll<TerrainObject>();
            if (all != null)
            {
                foreach (var t in all)
                {
                    if (t == null || t.attr == null || t.attr.id != attrId) continue;
                    long k = GetInstanceKey(t);
                    if (seen.Add(k)) result.Add(t);
                }
            }
        } catch {}
        // 4. ActiveObjects_Production
        try
        {
            var list3 = TerrainObject_Production.ActiveObjects_Production;
            if (list3 != null)
            {
                for (int i = 0; i < list3.Count; i++)
                {
                    var g = list3[i];
                    if (g == null) continue;
                    var t = FindTerrainObject(g.transform) as TerrainObject;
                    if (t != null && t.attr != null && t.attr.id == attrId)
                    {
                        long k = GetInstanceKey(t);
                        if (seen.Add(k)) result.Add(t);
                    }
                }
            }
        } catch {}
        return result;
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
}
