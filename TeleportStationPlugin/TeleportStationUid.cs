using System;
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
        try { return !string.IsNullOrEmpty(s) && !string.IsNullOrEmpty(CoordFromUid(s)); }
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
    public static string DisplayForUid(string uid)
    {
        try
        {
            string coord = CoordFromUid(uid);
            if (!string.IsNullOrEmpty(coord))
            {
                string named = TeleportStationNameManager.GetNameByCoord(coord);
                if (!string.IsNullOrWhiteSpace(named)) return named;
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
                string pck = TeleportBindingManager.CoordKey(pad);
                if (!string.IsNullOrEmpty(pck))
                {
                    string n2 = TeleportStationNameManager.GetNameByCoord(pck);
                    if (!string.IsNullOrWhiteSpace(n2)) return n2;
                }
            }
            catch { }
            string uid = UidFor(pad);
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
            string uid = UidFor(console);
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
