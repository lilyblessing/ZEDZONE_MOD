using System;
using System.Collections.Generic;
using UnityEngine;

namespace TeleportStationPlugin;

/// <summary>
/// v0.9.63 传送站稳定身份 UID：以建筑坐标对派生，建筑不动则跨读档不变。
/// 格式 "TS-x,y"（x,y 为 transform.position 四舍五入整数格；console 与其配对 pad 各自独立 UID）。
/// 全链（候选/选中/绑定日志/命名/气泡/面板）以 UID 为身份键；GO 名/实例ID 只做运行时关联，
/// 永不出现在面向玩家的文本与持久化身份中。未命名站显示 UID。
/// 坐标经 transform.position 编译期直访，零反射。
/// </summary>
public static class TeleportStationUid
{
    public const string Prefix = "TS-";

    // P1-6 显示热点缓存：key=TerrainObject 实例键（GetInstanceID），value=UID/坐标串。
    // DisplayForPad（标记刷新每 pad）与 DisplayForConsole 命中直接返回，免重复派生字符串；
    // DisplayForUid 经 ReverseLookupKey 反查命中后走键直取，跳过 FindPadByUid 全盘扫描（先用 CachedUidFor 校验 UID 一致）。
    // 正确性复核①已接线：BindingManager 解绑/陈旧清理/绑定成功/心跳死键清理路径均调 InvalidateUidCache；实例键复用窗口已关。
    private static readonly Dictionary<long, string> _uidCache = new Dictionary<long, string>();
    private static readonly Dictionary<long, string> _coordCache = new Dictionary<long, string>();

    /// <summary>清空 UID/坐标缓存（供后续放置/绑定/Cleanup 接线预留，本轮暂无调用方）。</summary>
    public static void InvalidateUidCache()
    {
        try { _uidCache.Clear(); _coordCache.Clear(); } catch { }
    }

    /// <summary>清空本类全部缓存（同 InvalidateUidCache，预留统一入口）。</summary>
    public static void InvalidateAll()
    {
        try { _uidCache.Clear(); _coordCache.Clear(); } catch { }
    }

    private static string CachedUidFor(TerrainObject t)
    {
        try
        {
            if (t == null) return "";
            long key = GetInstanceKey(t);
            string cached;
            if (_uidCache.TryGetValue(key, out cached)) return cached ?? "";
            string uid = UidFor(t);
            try
            {
                _uidCache[key] = uid;
                string c = CoordFromUid(uid);
                if (!string.IsNullOrEmpty(c)) _coordCache[key] = c;
            }
            catch { }
            return uid;
        }
        catch { return ""; }
    }

    private static string CachedCoordFor(TerrainObject t, long key)
    {
        try
        {
            string cached;
            if (_coordCache.TryGetValue(key, out cached)) return cached ?? "";
            string c = "";
            try { c = TeleportBindingManager.CoordKey(t); } catch { }
            if (string.IsNullOrEmpty(c)) c = CoordFromUid(CachedUidFor(t));
            try { if (!string.IsNullOrEmpty(c)) _coordCache[key] = c; } catch { }
            return c ?? "";
        }
        catch { return ""; }
    }

    private static void CachePair(long key, string uid, string coord)
    {
        try
        {
            if (key == 0) return;
            if (!string.IsNullOrEmpty(uid)) _uidCache[key] = uid;
            if (!string.IsNullOrEmpty(coord)) _coordCache[key] = coord;
        }
        catch { }
    }

    private static long ReverseLookupKey(string uid)
    {
        try
        {
            if (string.IsNullOrEmpty(uid)) return 0;
            foreach (var kv in _uidCache)
            {
                if (kv.Value == uid) return kv.Key;
            }
            return 0;
        }
        catch { return 0; }
    }

    /// <summary>由活体对象派生 UID；失败返回 ""（不编造）。</summary>
    public static string UidFor(TerrainObject t)
    {
        try
        {
            if (t == null || t.transform == null) return "";
            var p = t.transform.position;
            return $"{Prefix}{Mathf.RoundToInt(p.x)},{Mathf.RoundToInt(p.y)}";
        }
        catch { return ""; }
    }

    /// <summary>UID → 坐标键 "x,y"；非法返回 ""。</summary>
    public static string CoordFromUid(string uid)
    {
        try
        {
            if (string.IsNullOrEmpty(uid) || !uid.StartsWith(Prefix, StringComparison.Ordinal)) return "";
            string coord = uid.Substring(Prefix.Length).Trim();
            if (coord.Length == 0 || coord.IndexOf(',') < 0) return "";
            return coord;
        }
        catch { return ""; }
    }

    public static bool IsUid(string s)
    {
        try
        {
            // 等价于 CoordFromUid(s) 非空（前缀 + 去空白后非空 + 含逗号），全程无 Substring/Trim 分配。
            if (string.IsNullOrEmpty(s) || s.Length <= Prefix.Length) return false;
            if (!s.StartsWith(Prefix, StringComparison.Ordinal)) return false;
            if (s.IndexOf(',', Prefix.Length) < Prefix.Length) return false;
            for (int i = Prefix.Length; i < s.Length; i++)
            {
                if (!char.IsWhiteSpace(s[i])) return true;
            }
            return false;
        }
        catch { return false; }
    }

    /// <summary>坐标键 → UID。</summary>
    public static string UidFromCoord(string coord)
    {
        try
        {
            if (string.IsNullOrEmpty(coord) || coord.IndexOf(',') < 0) return "";
            return Prefix + coord.Trim();
        }
        catch { return ""; }
    }

    /// <summary>面向玩家的显示名：玩家命名优先，无名用 UID。永不返回 GO 名。</summary>
    /// v0.9.64 显示自愈（根因①）：SetName 写 console 坐标键（+绑定时才双写 pad 坐标键），
    /// 改名时未绑定 → pad 坐标键缺失 → DisplayForUid(padUid) 回退成 UID。
    /// 现直查未中时走活体回退（pad→对端console命名 / console自身命名），命中即写透 pad/console
    /// 坐标键，下次直查即中；无活体才回 UID（调用方可再用存量名兜底）。
    public static string DisplayForUid(string uid)
    {
        try
        {
            string coord = CoordFromUid(uid);
            if (!string.IsNullOrEmpty(coord))
            {
                string named = TeleportStationNameManager.GetNameByCoord(coord);
                if (!string.IsNullOrWhiteSpace(named)) return named;
                try
                {
                    string live = null;
                    TerrainObject pad = null;
                    try
                    {
                        // P1-6：缓存命中走键直取（FindConsoleByKey 底层为通用 FindByKey），跳过 FindPadByUid 全盘扫描；
                        // 命中后必须用 CachedUidFor 校验 UID 一致（防实例键复用），失败则回落旧级联。
                        long hitKey = ReverseLookupKey(uid);
                        if (hitKey != 0)
                        {
                            var cachedObj = TeleportBindingManager.FindConsoleByKey(hitKey);
                            if (cachedObj != null && CachedUidFor(cachedObj) == uid)
                                pad = cachedObj;
                        }
                    }
                    catch { }
                    if (pad == null)
                        pad = TeleportBindingManager.FindPadByUid(uid);
                    if (pad != null)
                    {
                        long pk = GetInstanceKey(pad);
                        CachePair(pk, uid, coord);
                        long ck = TeleportBindingManager.GetBoundConsole(pk);
                        if (ck != 0)
                        {
                            var console = TeleportBindingManager.FindConsoleByKey(ck);
                            string n;
                            if (console != null && TeleportStationNameManager.TryGetCustomName(console, out n) && !string.IsNullOrWhiteSpace(n))
                                live = n;
                        }
                    }
                    if (string.IsNullOrEmpty(live))
                    {
                        var console2 = TeleportBindingManager.FindConsoleByUid(uid);
                        if (console2 != null)
                        {
                            try { CachePair(GetInstanceKey(console2), uid, coord); } catch { }
                            string n2;
                            if (TeleportStationNameManager.TryGetCustomName(console2, out n2) && !string.IsNullOrWhiteSpace(n2))
                                live = n2;
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(live))
                    {
                        try { TeleportStationNameManager.SetCoordName(coord, live); } catch {}
                        return live;
                    }
                } catch {}
            }
            return string.IsNullOrEmpty(uid) ? "未知站点" : uid;
        }
        catch { return string.IsNullOrEmpty(uid) ? "未知站点" : uid; }
    }

    /// <summary>圆盘显示名：对端控制台命名 → pad 坐标命名 → 自身 UID。永不返回 GO 名。</summary>
    public static string DisplayForPad(TerrainObject pad)
    {
        try
        {
            if (pad == null) return "未知站点";
            long pk = GetInstanceKey(pad);
            try
            {
                long ck = TeleportBindingManager.GetBoundConsole(pk);
                if (ck != 0)
                {
                    var console = TeleportBindingManager.FindConsoleByKey(ck);
                    if (console != null)
                    {
                        string n;
                        if (TeleportStationNameManager.TryGetCustomName(console, out n) && !string.IsNullOrWhiteSpace(n))
                            return n;
                    }
                }
            }
            catch { }
            try
            {
                string pck = CachedCoordFor(pad, pk);
                if (!string.IsNullOrEmpty(pck))
                {
                    string n2 = TeleportStationNameManager.GetNameByCoord(pck);
                    if (!string.IsNullOrWhiteSpace(n2)) return n2;
                }
            }
            catch { }
            string uid = CachedUidFor(pad);
            return string.IsNullOrEmpty(uid) ? "未知站点" : uid;
        }
        catch { return "未知站点"; }
    }

    /// <summary>控制台显示名：命名优先，无名用 UID。永不返回 GO 名。</summary>
    public static string DisplayForConsole(TerrainObject console)
    {
        try
        {
            if (console == null) return "未知站点";
            string n;
            if (TeleportStationNameManager.TryGetCustomName(console, out n) && !string.IsNullOrWhiteSpace(n))
                return n;
            string uid = CachedUidFor(console);
            return string.IsNullOrEmpty(uid) ? "未知站点" : uid;
        }
        catch { return "未知站点"; }
    }

    private static long GetInstanceKey(TerrainObject t)
    {
        try { return (long)t.GetInstanceID(); }
        catch { try { return (long)t.Pointer; } catch { return t.GetHashCode(); } }
    }
}
