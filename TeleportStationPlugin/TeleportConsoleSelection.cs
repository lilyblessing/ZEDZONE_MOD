using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using BepInEx;

namespace TeleportStationPlugin;

/// <summary>
/// P6 控制台选目的地：v0.9.63 起以稳定 UID（consoleUid→padUid，见 TeleportStationUid）为身份键，
/// 持久化双轨（properties[1]=padUid 字符串 + JSON 信封 {"v":1,"byUid":{}}），跨读档稳定。
/// 实例ID仅做运行时关联，永不做持久身份。旧数字键 JSON/property 读入时尽力换算，换算失败即丢弃并日志。
/// 已上线 = 活体 IsPowered 实时判；无活体（未加载/读档后）→ 持久在线态（TeleportMapStations.json online）。
/// 发送方可传 = 已绑 + 供电(AND) + 电池≥10000；接收方无门控（v0.9.64 用户定案），在线态仅显示。
/// </summary>
public static class TeleportConsoleSelection
{
    private const int ConsoleId = 900101;
    private const int PadId = 900102;
    private static readonly Dictionary<string, string> _selByUid = new(); // consoleUid -> padUid（唯一真相源）
    private static float _lastSave = -999f;
    private static string SelPath => TeleportSaveIdentity.SavePath("TeleportSelection.json");

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

    // ===== v0.9.63 在线判两路：活体 IsPowered 实时判；无活体读持久在线态 =====
    // v0.9.64：仅作“最后已知在线态”显示查询，不再做任何发送/选点拒绝门控。
    // 持久表由 TeleportMapManager 活体观测时写入（TeleportMapStations.json {"x,y":{...,"online":0/1}}）。
    public static bool IsOnlineUid(string padUid)
    {
        if (!TeleportStationUid.IsUid(padUid)) return false;
        try
        {
            var live = TeleportBindingManager.FindPadByUid(padUid);
            if (live != null)
            {
                if (IsOnline(live)) return true;
                // 活体存在但判离线（加载竞态/未绑定）：持久曾在线则仍按在线放行
                return QueryPersistedOnline(TeleportStationUid.CoordFromUid(padUid));
            }
            return QueryPersistedOnline(TeleportStationUid.CoordFromUid(padUid));
        }
        catch { return false; }
    }

    public static bool QueryPersistedOnline(string coord)
    {
        if (string.IsNullOrEmpty(coord)) return false;
        try { return TeleportMapManager.QueryPersistedOnline(coord); }
        catch { return false; }
    }

    // 持久坐标（x,y 整格；无记录返回 false，不编造）。
    public static bool TryGetPersistedPos(string padUid, out int x, out int y)
    {
        x = 0; y = 0;
        try
        {
            string coord = TeleportStationUid.CoordFromUid(padUid);
            if (string.IsNullOrEmpty(coord)) return false;
            string nm; bool on;
            if (TeleportMapManager.QueryPersistedStation(coord, out x, out y, out nm, out on)) return true;
            // 持久文件无记录：UID 本身即坐标，回退解析（在线态另行判定，不在此放行）
            var parts = coord.Split(',');
            if (parts.Length == 2 && int.TryParse(parts[0].Trim(), out x) && int.TryParse(parts[1].Trim(), out y)) return true;
        }
        catch { }
        return false;
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

    // ===== 选点读写（UID 身份键） =====
    public static void SetSelected(TerrainObject console, TerrainObject targetPad)
    {
        if (console == null || targetPad == null) return;
        string cuid = TeleportStationUid.UidFor(console);
        string puid = TeleportStationUid.UidFor(targetPad);
        if (string.IsNullOrEmpty(cuid) || string.IsNullOrEmpty(puid)) return;
        SetSelectedByUid(cuid, puid);
        try
        {
            var cData = Reflect.Get(console, "objectData") ?? Reflect.Get(console, "terrainObjectData");
            if (cData != null)
            {
                var m = cData.GetType().GetMethod("SetProperty", new Type[] { typeof(int), typeof(string) });
                if (m != null) m.Invoke(cData, new object[] { 1, puid });
            }
        }
        catch { }
    }

    // 无活体选点（存量/远站）：console 活体 + 目标 UID。persisted-online=true 方可后续传送。
    public static void SetSelectedByUid(string consoleUid, string padUid)
    {
        if (!TeleportStationUid.IsUid(consoleUid) || !TeleportStationUid.IsUid(padUid)) return;
        _selByUid[consoleUid] = padUid;
        try
        {
            var console = TeleportBindingManager.FindConsoleByUid(consoleUid);
            if (console != null)
            {
                var cData = Reflect.Get(console, "objectData") ?? Reflect.Get(console, "terrainObjectData");
                if (cData != null)
                {
                    var m = cData.GetType().GetMethod("SetProperty", new Type[] { typeof(int), typeof(string) });
                    if (m != null) m.Invoke(cData, new object[] { 1, padUid });
                }
            }
        }
        catch { }
        Save();
        string disp = TeleportStationUid.DisplayForUid(padUid);
        Plugin.L.LogInfo($"[TS][Sel] 选中 {consoleUid} -> {padUid}({disp})");
    }

    public static string GetSelectedUid(string consoleUid)
    {
        if (string.IsNullOrEmpty(consoleUid)) return "";
        try
        {
            string puid;
            if (_selByUid.TryGetValue(consoleUid, out puid) && !string.IsNullOrEmpty(puid)) return puid;
            // 懒加载：从活体 console 的 properties[1] 读（UID 字符串；旧数字键尽力换算）
            var console = TeleportBindingManager.FindConsoleByUid(consoleUid);
            if (console != null)
            {
                var cData = Reflect.Get(console, "objectData") ?? Reflect.Get(console, "terrainObjectData");
                if (cData != null)
                {
                    var gm = cData.GetType().GetMethod("GetProperty", new Type[] { typeof(int) });
                    if (gm != null)
                    {
                        var val = gm.Invoke(cData, new object[] { 1 }) as string;
                        if (TeleportStationUid.IsUid(val)) { _selByUid[consoleUid] = val; return val; }
                        long legacyPk;
                        if (!string.IsNullOrEmpty(val) && long.TryParse(val, out legacyPk))
                        {
                            var pad = FindByKey(legacyPk) as TerrainObject;
                            if (pad != null)
                            {
                                string pu = TeleportStationUid.UidFor(pad);
                                if (!string.IsNullOrEmpty(pu)) { _selByUid[consoleUid] = pu; return pu; }
                            }
                        }
                    }
                }
            }
        }
        catch { }
        return "";
    }

    // 活体目标解析（无活体返回 null；调用方走 UID/坐标路径，不视为失效）。
    public static TerrainObject ResolveLivePad(string padUid) => TeleportBindingManager.FindPadByUid(padUid);

    // 兼容 API：实例键查询（运行时关联）。目标无活体时返回 0。
    public static long GetSelectedKey(long consoleKey)
    {
        try
        {
            var console = FindByKey(consoleKey) as TerrainObject;
            if (console == null) return 0;
            string cuid = TeleportStationUid.UidFor(console);
            string puid = GetSelectedUid(cuid);
            if (string.IsNullOrEmpty(puid)) return 0;
            var pad = TeleportBindingManager.FindPadByUid(puid);
            return pad != null ? GetInstanceKey(pad) : 0;
        }
        catch { return 0; }
    }

    public static TerrainObject GetSelectedPad(TerrainObject console)
    {
        if (console == null) return null;
        string puid = GetSelectedUid(TeleportStationUid.UidFor(console));
        if (string.IsNullOrEmpty(puid)) return null;
        return TeleportBindingManager.FindPadByUid(puid);
    }

    public static bool HasSelection(TerrainObject console)
    {
        if (console == null) return false;
        return !string.IsNullOrEmpty(GetSelectedUid(TeleportStationUid.UidFor(console)));
    }

    public static void Clear(TerrainObject console)
    {
        if (console == null) return;
        long ck = GetInstanceKey(console);
        ClearByKey(ck);
    }

    public static void ClearByKey(long consoleKey)
    {
        try
        {
            var console = FindByKey(consoleKey) as TerrainObject;
            if (console != null)
            {
                string cuid = TeleportStationUid.UidFor(console);
                if (!string.IsNullOrEmpty(cuid) && _selByUid.ContainsKey(cuid)) _selByUid.Remove(cuid);
                var cData = Reflect.Get(console, "objectData") ?? Reflect.Get(console, "terrainObjectData");
                if (cData != null)
                {
                    var m = cData.GetType().GetMethod("SetProperty", new Type[] { typeof(int), typeof(string) });
                    if (m != null) m.Invoke(cData, new object[] { 1, "" });
                }
            }
        }
        catch { }
        Save();
        Plugin.L.LogInfo($"[TS][Sel] 清空 consoleKey={consoleKey}");
    }

    public static void ClearByUid(string consoleUid)
    {
        if (string.IsNullOrEmpty(consoleUid)) return;
        if (_selByUid.ContainsKey(consoleUid)) _selByUid.Remove(consoleUid);
        try
        {
            var console = TeleportBindingManager.FindConsoleByUid(consoleUid);
            if (console != null)
            {
                var cData = Reflect.Get(console, "objectData") ?? Reflect.Get(console, "terrainObjectData");
                if (cData != null)
                {
                    var m = cData.GetType().GetMethod("SetProperty", new Type[] { typeof(int), typeof(string) });
                    if (m != null) m.Invoke(cData, new object[] { 1, "" });
                }
            }
        }
        catch { }
        Save();
        Plugin.L.LogInfo($"[TS][Sel] 清空 {consoleUid}");
    }

    public static void ClearAllForPad(long padKey)
    {
        // 若某 pad 被销毁，清理所有指向它的选中（实例键→UID 换算，换算失败跳过）
        try
        {
            var pad = FindByKey(padKey) as TerrainObject;
            string puid = pad != null ? TeleportStationUid.UidFor(pad) : "";
            if (string.IsNullOrEmpty(puid)) return;
            var dead = new List<string>();
            foreach (var kv in _selByUid) if (kv.Value == puid) dead.Add(kv.Key);
            foreach (var k in dead) ClearByUid(k);
        }
        catch { }
    }

    // ===== 持久化 JSON 信封 {"v":1,"byUid":{cuid:puid}}；旧数字键格式兼容读入 =====
    private static void Save()
    {
        try
        {
            float now = Time.unscaledTime;
            if (now - _lastSave < 1f) return;
            _lastSave = now;
            SaveNow();
        }
        catch { }
    }

    // v0.9.68 切换落盘：绕过节流强制写当前 namespace（调用方保证 key 仍是旧 key）。
    public static int FlushForIdentity()
    {
        try
        {
            if (_selByUid.Count == 0) return 0;
            _lastSave = -999f;
            SaveNow();
            return _selByUid.Count;
        }
        catch { return 0; }
    }

    private static void SaveNow()
    {
        try
        {
            var sb = new System.Text.StringBuilder("{\"v\":1,\"byUid\":{");
            bool first = true;
            foreach (var kv in _selByUid)
            {
                if (string.IsNullOrEmpty(kv.Key) || string.IsNullOrEmpty(kv.Value)) continue;
                if (!first) sb.Append(",");
                sb.Append($"\"{kv.Key}\":\"{kv.Value}\"");
                first = false;
            }
            sb.Append("}}");
            File.WriteAllText(SelPath, sb.ToString());
        }
        catch { }
    }

    public static void Load()
    {
        try
        {
            // v0.9.67 存档隔离：读 namespaced，缺失则 legacy 只读一次兜底。
            string loadPath = TeleportSaveIdentity.LoadPath("TeleportSelection.json");
            if (!File.Exists(loadPath)) return;
            var txt = File.ReadAllText(loadPath).Trim();
            if (string.IsNullOrWhiteSpace(txt)) return;
            int adopted = 0, dropped = 0;
            if (txt.Contains("\"byUid\""))
            {
                // 新信封 {"v":1,"byUid":{"cuid":"puid"}}
                foreach (var kv in ParseStrMap(ExtractSection(txt, "\"byUid\"")))
                {
                    if (TeleportStationUid.IsUid(kv.Key) && TeleportStationUid.IsUid(kv.Value))
                    { _selByUid[kv.Key] = kv.Value; adopted++; }
                    else dropped++;
                }
            }
            else
            {
                // 旧数字键 {"ck":pk}：实例ID跨读档必变，仅当两端活体均可换算为 UID 才收留
                var inner = txt.Trim().Trim('{', '}');
                if (!string.IsNullOrWhiteSpace(inner))
                {
                    foreach (var p in inner.Split(','))
                    {
                        var kv = p.Split(':');
                        if (kv.Length != 2) continue;
                        var k = kv[0].Trim().Trim('"');
                        long ck, pk;
                        if (!long.TryParse(k, out ck) || !long.TryParse(kv[1].Trim(), out pk)) { dropped++; continue; }
                        var c = FindByKey(ck) as TerrainObject;
                        var pd = FindByKey(pk) as TerrainObject;
                        if (c == null || pd == null) { dropped++; continue; }
                        string cuid = TeleportStationUid.UidFor(c), puid = TeleportStationUid.UidFor(pd);
                        if (string.IsNullOrEmpty(cuid) || string.IsNullOrEmpty(puid)) { dropped++; continue; }
                        _selByUid[cuid] = puid;
                        adopted++;
                    }
                }
            }
            Plugin.L.LogInfo($"[TS][Sel] 载入 JSON 采用 {adopted} 对 丢弃 {dropped} 对（旧实例键跨读档失效）");
        }
        catch { }
        // 再从活体 properties[1] 补（以 property 为准，覆盖 JSON 的陈旧值）
        try
        {
            var consoles = FindAllTerrainObjectsById(ConsoleId);
            foreach (var c in consoles)
            {
                string cuid = TeleportStationUid.UidFor(c);
                if (string.IsNullOrEmpty(cuid)) continue;
                var cData = Reflect.Get(c, "objectData") ?? Reflect.Get(c, "terrainObjectData");
                if (cData == null) continue;
                var gm = cData.GetType().GetMethod("GetProperty", new Type[] { typeof(int) });
                if (gm == null) continue;
                var val = gm.Invoke(cData, new object[] { 1 }) as string;
                if (TeleportStationUid.IsUid(val))
                    _selByUid[cuid] = val;
                else if (!string.IsNullOrEmpty(val))
                {
                    long pk;
                    if (long.TryParse(val, out pk))
                    {
                        // 旧数字 property：同档内活体可换算则收留，否则丢弃（跨读档实例ID必变）
                        var pd = FindByKey(pk) as TerrainObject;
                        if (pd != null)
                        {
                            string pu = TeleportStationUid.UidFor(pd);
                            if (!string.IsNullOrEmpty(pu)) _selByUid[cuid] = pu;
                        }
                    }
                }
                else if (string.IsNullOrEmpty(val) && _selByUid.ContainsKey(cuid))
                {
                    // property 已清空但 JSON 还有，说明已传后清空，以 property 为准
                    _selByUid.Remove(cuid);
                }
            }
        }
        catch { }
    }

    // v0.9.67 存档隔离：Flush 内存旧表（返回旧条目数供切换日志）。
    public static int ResetForIdentity()
    {
        try
        {
            int n = _selByUid.Count;
            _selByUid.Clear();
            return n;
        }
        catch { return 0; }
    }

    public static int CountEntries()
    {
        try { return _selByUid.Count; } catch { return 0; }
    }

    public static void CleanupStale()
    {
        // v0.9.63：UID 条目不按活体清理（远站未加载≠已销毁，活体缺失是常态）；
        // 仅清非法键（格式损坏），活体解析失败的合法 UID 保留。
        try
        {
            var dead = new List<string>();
            foreach (var kv in _selByUid)
                if (!TeleportStationUid.IsUid(kv.Key) || !TeleportStationUid.IsUid(kv.Value)) dead.Add(kv.Key);
            foreach (var k in dead) _selByUid.Remove(k);
            if (dead.Count > 0) Plugin.L.LogInfo($"[TS][Sel] 清理非法键 {dead.Count} 对 余 {_selByUid.Count}");
        }
        catch { }
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
        }
        catch { }
        return null;
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
                if (n == '\\') { sb.Append('\\'); i++; }
                else if (n == '"') { sb.Append('"'); i++; }
                else sb.Append(c);
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    // 字符串键版 {"k":"v"} 解析（UID 无转义字符，极简实现）。
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
        try { return TeleportObjectCache.FindAllById(attrId); } catch {}
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
