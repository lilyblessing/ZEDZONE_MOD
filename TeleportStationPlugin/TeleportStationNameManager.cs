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
    // v0.9.61 坐标稳定键：key="x,y"（建筑不动坐标不变，跨读档稳定；实例ID只做运行时关联）。
    // SetName 双写（实例键+坐标键+对端pad实例键/坐标键）；GetName 按 实例键→存档property[2]→坐标键 顺序回退。
    private static readonly Dictionary<string, string> _namesByCoord = new();
    private static float _lastSave = -999f;
    // v0.9.69 脏位：本身份下 SetName 才置位；Load 期被 SuppressDirty 抑制。
    private static bool _dirty = false;
    internal static void MarkDirty()
    {
        try { if (!TeleportSaveIdentity.SuppressDirty) _dirty = true; } catch {}
    }
    private static string NamePath => TeleportSaveIdentity.SavePath("TeleportStationNames.json");

    // ---- 对外：获取/设置 ----
    // 坐标稳定键（编译期直访 transform.position，无反射；四舍五入到整数格）。
    public static string CoordKey(TerrainObject t)
    {
        try
        {
            var p = t.transform.position;
            return $"{Mathf.RoundToInt(p.x)},{Mathf.RoundToInt(p.y)}";
        }
        catch { return ""; }
    }

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
            // v0.9.61 坐标键回退（跨读档：实例ID已变但坐标未变时命中）
            try
            {
                string ck = CoordKey(console);
                if (!string.IsNullOrEmpty(ck) && _namesByCoord.TryGetValue(ck, out var cn) && !string.IsNullOrEmpty(cn))
                {
                    _names[k] = cn;
                    return cn;
                }
            } catch {}
            try { if (!string.IsNullOrEmpty(console.name)) return console.name; } catch {}
            return "传送站" + Math.Abs(k % 10000).ToString("D4");
        } catch { try { return console != null && !string.IsNullOrEmpty(console.name) ? console.name : ""; } catch { return ""; } }
    }

    // v0.9.61 供地图标记用：pad→对端活体console取命名（改名写在console键下，查必须走console）。
    // 对端不活时回退 pad 自身实例键/坐标键（SetName 双写保证）。
    public static string GetNameForPadObject(TerrainObject pad)
    {
        if (pad == null) return "";
        try
        {
            long pk = GetInstanceKey(pad);
            try
            {
                long ck = TeleportBindingManager.GetBoundConsole(pk);
                if (ck != 0)
                {
                    var console = FindByKey(ck) as TerrainObject;
                    if (console != null)
                    {
                        string n = GetName(console);
                        if (!string.IsNullOrWhiteSpace(n)) return n;
                    }
                }
            } catch {}
            if (_names.TryGetValue(pk, out var pn) && !string.IsNullOrEmpty(pn)) return pn;
            try
            {
                string ck2 = CoordKey(pad);
                if (!string.IsNullOrEmpty(ck2) && _namesByCoord.TryGetValue(ck2, out var cn2) && !string.IsNullOrEmpty(cn2)) return cn2;
            } catch {}
        } catch {}
        return "";
    }

    // v0.9.63 玩家命名查询（无 GO 名回退）：仅查实例键→存档 property[2]→坐标键。
    // 未命名返回 false，调用方显示 UID。面向玩家文本禁止再用 console.name。
    public static bool TryGetCustomName(TerrainObject console, out string name)
    {
        name = "";
        if (console == null) return false;
        try
        {
            long k = GetInstanceKey(console);
            string c;
            if (_names.TryGetValue(k, out c) && !string.IsNullOrEmpty(c)) { name = c; return true; }
            try
            {
                var d = Reflect.Get(console, "objectData") ?? Reflect.Get(console, "terrainObjectData");
                if (d != null)
                {
                    var gm = d.GetType().GetMethod("GetProperty", new Type[] { typeof(int) });
                    if (gm != null)
                    {
                        var v = gm.Invoke(d, new object[] { 2 }) as string;
                        if (!string.IsNullOrEmpty(v)) { _names[k] = v; name = v; return true; }
                    }
                }
            }
            catch { }
            try
            {
                string ck = CoordKey(console);
                string cn;
                if (!string.IsNullOrEmpty(ck) && _namesByCoord.TryGetValue(ck, out cn) && !string.IsNullOrEmpty(cn))
                {
                    _names[k] = cn;
                    name = cn;
                    return true;
                }
            }
            catch { }
        }
        catch { }
        return false;
    }

    // v0.9.63 按坐标键取玩家命名（UID 显示解析用）；无名返回 ""。
    public static string GetNameByCoord(string coord)
    {
        if (string.IsNullOrEmpty(coord)) return "";
        try
        {
            string n;
            if (_namesByCoord.TryGetValue(coord, out n) && !string.IsNullOrEmpty(n)) return n;
        }
        catch { }
        return "";
    }

    // v0.9.64 坐标键治愈写（DisplayForUid 活体回退写透 / 绑定成功对齐用；空名=删键）。
    public static void SetCoordName(string coord, string name)
    {
        if (string.IsNullOrEmpty(coord)) return;
        try
        {
            if (string.IsNullOrEmpty(name)) _namesByCoord.Remove(coord);
            else _namesByCoord[coord] = name;
        } catch {}
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
            try { MarkDirty(); } catch {}
            // v0.9.61 双写坐标键（跨读档稳定）
            try
            {
                string ck = CoordKey(console);
                if (!string.IsNullOrEmpty(ck))
                {
                    if (string.IsNullOrEmpty(newName)) _namesByCoord.Remove(ck);
                    else _namesByCoord[ck] = newName;
                }
            } catch {}
            // v0.9.61 双写对端 pad（地图按 pad 查名；改名时双方活体，双写必中）
            try
            {
                long padKey = TeleportBindingManager.GetBoundPad(k);
                if (padKey != 0)
                {
                    var pad = FindByKey(padKey) as TerrainObject;
                    if (pad != null)
                    {
                        if (string.IsNullOrEmpty(newName)) _names.Remove(padKey);
                        else _names[padKey] = newName;
                        string pck = CoordKey(pad);
                        if (!string.IsNullOrEmpty(pck))
                        {
                            if (string.IsNullOrEmpty(newName)) _namesByCoord.Remove(pck);
                            else _namesByCoord[pck] = newName;
                        }
                    }
                }
            } catch {}
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
    // v0.9.61 信封格式 {"v":1,"byId":{...},"byCoord":{...}}；旧扁平 {"id":"name"} 按 byId 读入（兼容）。
    private static void Save()
    {
        try
        {
            float now = 0f;
            try { now = Time.unscaledTime; } catch { now = Time.realtimeSinceStartup; }
            if (now - _lastSave < 1f) return;
            _lastSave = now;
            SaveNow();
        } catch (Exception ex) { Plugin.L.LogWarning($"[TS][Name] Save {ex.Message.Split('\n')[0]}"); }
    }

    // v0.9.68 切换落盘：绕过节流强制写当前 namespace（调用方保证 key 仍是旧 key）。
    // v0.9.69 脏位守卫：非脏返回 0；写后清脏位。
    public static int FlushForIdentity()
    {
        try
        {
            if ((_names.Count == 0 && _namesByCoord.Count == 0) || !_dirty) return 0;
            _lastSave = -999f;
            SaveNow();
            _dirty = false;
            return _names.Count + _namesByCoord.Count;
        }
        catch { return 0; }
    }

    private static void SaveNow()
    {
        try
        {
            var sb = new System.Text.StringBuilder("{\"v\":1,\"byId\":{");
            bool first = true;
            foreach (var kv in _names)
            {
                if (!first) sb.Append(",");
                sb.Append($"\"{kv.Key}\":\"{Escape(kv.Value)}\"");
                first = false;
            }
            sb.Append("},\"byCoord\":{");
            first = true;
            foreach (var kv in _namesByCoord)
            {
                if (!first) sb.Append(",");
                sb.Append($"\"{Escape(kv.Key)}\":\"{Escape(kv.Value)}\"");
                first = false;
            }
            sb.Append("}}");
            File.WriteAllText(NamePath, sb.ToString());
            Plugin.L.LogInfo($"[TS][Name] 保存 {NamePath} byId={_names.Count} byCoord={_namesByCoord.Count}");
        } catch (Exception ex) { Plugin.L.LogWarning($"[TS][Name] Save {ex.Message.Split('\n')[0]}"); }
    }

    public static void Load()
    {
        try
        {
            // v0.9.67 存档隔离：读 namespaced，缺失则 legacy 只读一次兜底。
            string loadPath = TeleportSaveIdentity.LoadPath("TeleportStationNames.json");
            if (!File.Exists(loadPath))
            {
                // 首次运行无文件，等待活体覆盖
            }
            else
            {
                var txt = File.ReadAllText(loadPath);
                if (!string.IsNullOrWhiteSpace(txt))
                {
                    if (txt.Contains("\"byId\"") || txt.Contains("\"byCoord\""))
                    {
                        string byId = ExtractSection(txt, "\"byId\"");
                        string byCoord = ExtractSection(txt, "\"byCoord\"");
                        int n1 = 0, n2 = 0;
                        if (byId != null) foreach (var kv in ParseJson(byId)) { _names[kv.Key] = kv.Value; n1++; }
                        if (byCoord != null) foreach (var kv in ParseStrMap(byCoord)) { _namesByCoord[kv.Key] = kv.Value; n2++; }
                        Plugin.L.LogInfo($"[TS][Name] 载入JSON byId={n1} byCoord={n2}");
                    }
                    else
                    {
                        var parsed = ParseJson(txt);
                        foreach (var kv in parsed) _names[kv.Key] = kv.Value;
                        Plugin.L.LogInfo($"[TS][Name] 载入JSON(旧扁平) {_names.Count}条(文件{parsed.Count})");
                    }
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
                // v0.9.64 坐标键回填（根因①治愈）：存档名只进了实例键表时，同步一份到
                // console 当前坐标键，保证 DisplayForUid(uid→coord) 跨读档命中。
                try
                {
                    if (!string.IsNullOrEmpty(pv))
                    {
                        string cck2 = CoordKey(c);
                        if (!string.IsNullOrEmpty(cck2))
                        {
                            string old;
                            if (!_namesByCoord.TryGetValue(cck2, out old) || old != pv) _namesByCoord[cck2] = pv;
                        }
                    }
                } catch {}
                // pv 为空时不删 JSON 残留（避免存档清空误删，外层 CleanupStale 负责）
            }
            if (over > 0) Plugin.L.LogInfo($"[TS][Name] properties[2]覆盖{over}条");
        } catch {}
    }

    // v0.9.67 存档隔离：Flush 内存旧表（返回旧条目数供切换日志）。
    public static int ResetForIdentity()
    {
        try
        {
            int n = _names.Count + _namesByCoord.Count;
            _names.Clear();
            _namesByCoord.Clear();
            _dirty = false;
            return n;
        }
        catch { return 0; }
    }

    public static int CountEntries()
    {
        try { return _names.Count + _namesByCoord.Count; } catch { return 0; }
    }

    public static void CleanupStale()
    {
        try
        {
            // v0.9.61：只清理实例键表；坐标表是跨读档锚点，永不清（建筑拆除后由改名/覆盖自然更新）。
            var alive = new HashSet<long>();
            foreach (var t in FindAllTerrainObjectsById(ConsoleId)) alive.Add(GetInstanceKey(t));
            foreach (var t in FindAllTerrainObjectsById(PadId)) alive.Add(GetInstanceKey(t));
            if (alive.Count == 0) return;
            var dead = new List<long>();
            foreach (var kv in _names)
                if (!alive.Contains(kv.Key)) dead.Add(kv.Key);
            foreach (var k in dead) _names.Remove(k);
            if (dead.Count > 0) Plugin.L.LogInfo($"[TS][Name] 清理死键{dead.Count} 余{_names.Count} (byCoord保留{_namesByCoord.Count})");
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
    // P2 收尾：只读委托统一入口（查 900101+900102 两张 0.5s TTL 缓存表，命中零扫描；未中返 null，语义同旧直扫）。
    private static TerrainObject FindByKey(long key)
    {
        try { return TeleportObjectCache.FindByKey(key); } catch { return null; }
    }

    // 取信封中 "key":{...} 的 {...} 子串（含括号），找不到返回 null。
    private static string ExtractSection(string json, string quotedKey)
    {
        try
        {
            int ki = json.IndexOf(quotedKey, StringComparison.Ordinal);
            if (ki < 0) return null;
            int bi = json.IndexOf('{', ki + quotedKey.Length);
            if (bi < 0) return null;
            int depth = 0;
            bool inStr = false;
            for (int i = bi; i < json.Length; i++)
            {
                char ch = json[i];
                if (inStr) { if (ch == '\\') i++; else if (ch == '"') inStr = false; continue; }
                if (ch == '"') inStr = true;
                else if (ch == '{') depth++;
                else if (ch == '}') { depth--; if (depth == 0) return json.Substring(bi, i - bi + 1); }
            }
        } catch {}
        return null;
    }
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

    // 字符串键版（byCoord 段复用同一对 `"k":"v"` 形状，键为 "x,y" 坐标串）。
    private static Dictionary<string, string> ParseStrMap(string json)
    {
        var d = new Dictionary<string, string>();
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
            d[key] = val;
        }
        return d;
    }
}
