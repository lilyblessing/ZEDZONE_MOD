using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using BepInEx;
using ZedZoneShared;

namespace TeleportStationPlugin;

/// <summary>
/// 传送站控制台 900101 独立命名管理（用于地图标签显示）。
/// 持久化双轨（与 TeleportBindingManager / TeleportConsoleSelection 同构）：
///   主轨 TerrainObjectData.properties[2]（SetProperty/GetProperty via 反射，随存档走，跨档隔离）；
///   备轨 BepInEx/config/TeleportStationNames.json（独立 JSON，存档丢失/重建时回退补齐，写入磁盘优先）。
/// 内存表 Dictionary&lt;long,string&gt; _names 以 GetInstanceKey(TerrainObject)=GetInstanceID/Pointer/HashCode 为键。
/// 加载策略：JSON 先入表，再用活体 consoles 的 properties[2] 非空值覆盖（存档为准）；保存 1s 节流+Escape。
/// 查找经 TeleportObjectCache(0.5s TTL) 优先，fallback ChargerPadFix._knownClones + Resources.FindObjectsOfTypeAll。
/// 不含 H&amp;D 特殊逻辑（仅复用 _knownClones 只读）。
/// </summary>
public static class TeleportStationNameManager
{
    private const int ConsoleId = 900101;
    private const int PadId = 900102;
    private static readonly Dictionary<long, string> _names = new();
    private static float _lastSave = -999f;
    private static string NamePath => Path.Combine(Paths.ConfigPath, "TeleportStationNames.json");

    // ---- 对外：获取/设置 ----
    public static string GetName(TerrainObject console)
    {
        if (console == null) return "";
        try
        {
            long k = GetInstanceKey(console);
            if (_names.TryGetValue(k, out var c) && !string.IsNullOrEmpty(c)) return c;
            // 懒加载 properties[2]
            try
            {
                var d = Reflect.Get(console, "objectData") ?? Reflect.Get(console, "terrainObjectData");
                if (d != null)
                {
                    var gm = d.GetType().GetMethod("GetProperty", new Type[] { typeof(int) });
                    if (gm != null)
                    {
                        var v = gm.Invoke(d, new object[] { 2 }) as string;
                        if (!string.IsNullOrEmpty(v)) { _names[k] = v; return v; }
                    }
                }
            } catch {}
            try { if (!string.IsNullOrEmpty(console.name)) return console.name; } catch {}
            return "传送站" + Math.Abs(k % 10000).ToString("D4");
        } catch { try { return console != null && !string.IsNullOrEmpty(console.name) ? console.name : ""; } catch { return ""; } }
    }

    public static void SetName(TerrainObject console, string newName)
    {
        if (console == null) return;
        if (newName == null) newName = "";
        newName = newName.Trim();
        try
        {
            long k = GetInstanceKey(console);
            if (string.IsNullOrEmpty(newName)) _names.Remove(k);
            else _names[k] = newName;
            // 写存档 property[2]（反射 GetMethod("SetProperty")）
            try
            {
                var d = Reflect.Get(console, "objectData") ?? Reflect.Get(console, "terrainObjectData");
                if (d != null)
                {
                    var m = d.GetType().GetMethod("SetProperty", new Type[] { typeof(int), typeof(string) });
                    if (m != null) m.Invoke(d, new object[] { 2, newName });
                }
            } catch (Exception ex) { Plugin.L.LogWarning($"[TS][Name] SetProperty(2) {ex.Message.Split('\n')[0]}"); }
            Save();
            Plugin.L.LogInfo($"[TS][Name] Set {k}=\"{newName}\"");
        } catch (Exception ex) { Plugin.L.LogWarning($"[TS][Name] SetName {ex.Message.Split('\n')[0]}"); }
    }

    // ---- 持久化：Save/Load ----
    private static void Save()
    {
        try
        {
            float now = 0f;
            try { now = Time.unscaledTime; } catch { now = Time.realtimeSinceStartup; }
            if (now - _lastSave < 1f) return;
            _lastSave = now;
            var sb = new System.Text.StringBuilder("{");
            bool first = true;
            foreach (var kv in _names)
            {
                if (!first) sb.Append(",");
                sb.Append($"\"{kv.Key}\":\"{Escape(kv.Value)}\"");
                first = false;
            }
            sb.Append("}");
            File.WriteAllText(NamePath, sb.ToString());
            Plugin.L.LogInfo($"[TS][Name] 保存 {NamePath} {_names.Count}条");
        } catch (Exception ex) { Plugin.L.LogWarning($"[TS][Name] Save {ex.Message.Split('\n')[0]}"); }
    }

    public static void Load()
    {
        try
        {
            if (!File.Exists(NamePath))
            {
                // 首次运行无文件，等待活体覆盖
            }
            else
            {
                var txt = File.ReadAllText(NamePath);
                if (!string.IsNullOrWhiteSpace(txt))
                {
                    var parsed = ParseJson(txt);
                    foreach (var kv in parsed) _names[kv.Key] = kv.Value;
                    Plugin.L.LogInfo($"[TS][Name] 载入JSON {_names.Count}条(文件{parsed.Count})");
                }
            }
        } catch (Exception ex) { Plugin.L.LogWarning($"[TS][Name] Load {ex.Message.Split('\n')[0]}"); return; }
        // 活体 properties[2] 覆盖（存档权威，处理跨档/覆盖场景）
        try
        {
            var cs = FindAllTerrainObjectsById(ConsoleId);
            int over = 0;
            foreach (var c in cs)
            {
                if (c == null) continue;
                long ck = GetInstanceKey(c);
                string pv = null;
                try
                {
                    var d = Reflect.Get(c, "objectData") ?? Reflect.Get(c, "terrainObjectData");
                    if (d != null)
                    {
                        var gm = d.GetType().GetMethod("GetProperty", new Type[] { typeof(int) });
                        if (gm != null) pv = gm.Invoke(d, new object[] { 2 }) as string;
                    }
                } catch {}
                if (!string.IsNullOrEmpty(pv) && (!_names.TryGetValue(ck, out var cur) || cur != pv))
                {
                    _names[ck] = pv;
                    over++;
                }
                // pv 为空时不删 JSON 残留（避免存档清空误删，外层 CleanupStale 负责）
            }
            if (over > 0) Plugin.L.LogInfo($"[TS][Name] properties[2]覆盖{over}条");
        } catch {}
    }

    public static void CleanupStale()
    {
        try
        {
            var alive = new HashSet<long>();
            foreach (var t in FindAllTerrainObjectsById(ConsoleId)) alive.Add(GetInstanceKey(t));
            if (alive.Count == 0) return;
            var dead = new List<long>();
            foreach (var kv in _names)
                if (!alive.Contains(kv.Key)) dead.Add(kv.Key);
            foreach (var k in dead) _names.Remove(k);
            if (dead.Count > 0) Plugin.L.LogInfo($"[TS][Name] 清理死键{dead.Count} 余{_names.Count}");
        } catch {}
    }

    // ---- 查找：Cache 优先 + _knownClones + Resources ----
    private static List<TerrainObject> FindAllTerrainObjectsById(int attrId)
    {
        try { return TeleportObjectCache.FindAllById(attrId); } catch {}
        var res = new List<TerrainObject>();
        var seen = new HashSet<long>();
        // 来自 ChargerPadFix 的克隆/隐藏对象注册表（OnEnable 记录，含非 Production 存活对象）
        try
        {
            var f = typeof(ChargerPadFix).GetField("_knownClones", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var list = f?.GetValue(null) as List<object>;
            if (list != null)
                foreach (var o in list)
                {
                    var comp = o as Component;
                    if (comp == null) continue;
                    var t = FindTerrainObject(comp.transform) as TerrainObject;
                    if (t != null && t.attr != null && t.attr.id == attrId)
                    {
                        long kk = GetInstanceKey(t);
                        if (seen.Add(kk)) res.Add(t);
                    }
                }
        } catch {}
        try
        {
            var all = UnityEngine.Resources.FindObjectsOfTypeAll<TerrainObject>();
            if (all != null)
                foreach (var t in all)
                    if (t != null && t.attr != null && t.attr.id == attrId)
                    {
                        long kk = GetInstanceKey(t);
                        if (seen.Add(kk)) res.Add(t);
                    }
        } catch {}
        return res;
    }

    // ---- 工具 ----
    private static long GetInstanceKey(TerrainObject t)
    {
        try { return (long)t.GetInstanceID(); }
        catch { try { return (long)t.Pointer; } catch { return t.GetHashCode(); } }
    }

    private static Component FindTerrainObject(Transform tr)
    {
        int d = 0;
        while (tr != null && d++ < 16)
        {
            foreach (var c in tr.GetComponents<Component>())
            {
                if (c == null) continue;
                if (c.GetType().Name.Contains("TerrainObject")) return c;
            }
            tr = tr.parent;
        }
        return null;
    }

    private static string Escape(string s)
    {
        if (s == null) return "";
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
    }

    private static string Unescape(string s)
    {
        if (s == null) return "";
        var sb = new System.Text.StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\\' && i + 1 < s.Length)
            {
                char n = s[i + 1];
                switch (n)
                {
                    case '\\': sb.Append('\\'); i++; break;
                    case '"': sb.Append('"'); i++; break;
                    case 'n': sb.Append('\n'); i++; break;
                    case 'r': sb.Append('\r'); i++; break;
                    case 't': sb.Append('\t'); i++; break;
                    default: sb.Append(c); break;
                }
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    // 极简 JSON 解析器：处理 {"123":"name with \"转义\" and \\"} ，键为数字字符串，支持 \" \\ \n \r \t
    private static Dictionary<long, string> ParseJson(string json)
    {
        var d = new Dictionary<long, string>();
        if (string.IsNullOrWhiteSpace(json)) return d;
        json = json.Trim();
        if (json.Length < 2 || json[0] != '{' || json[json.Length - 1] != '}') return d;
        int i = 1, len = json.Length;
        while (i < len - 1)
        {
            while (i < len && (char.IsWhiteSpace(json[i]) || json[i] == ',')) i++;
            if (i >= len - 1 || json[i] == '}') break;
            if (json[i] != '"') { i++; continue; }
            int ks = i + 1, ke = -1;
            for (int j = ks; j < len; j++) { if (json[j] == '\\') { j++; continue; } if (json[j] == '"') { ke = j; break; } }
            if (ke < 0) break;
            string key = Unescape(json.Substring(ks, ke - ks));
            i = ke + 1;
            while (i < len && char.IsWhiteSpace(json[i])) i++;
            if (i >= len || json[i] != ':') break;
            i++;
            while (i < len && char.IsWhiteSpace(json[i])) i++;
            if (i >= len || json[i] != '"') break;
            int vs = i + 1, ve = -1;
            for (int j = vs; j < len; j++) { if (json[j] == '\\') { j++; continue; } if (json[j] == '"') { ve = j; break; } }
            if (ve < 0) break;
            string val = Unescape(json.Substring(vs, ve - vs));
            i = ve + 1;
            if (long.TryParse(key, out var ck)) d[ck] = val;
        }
        return d;
    }
}
