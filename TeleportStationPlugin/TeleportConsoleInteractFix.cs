using System;
using HarmonyLib;
using UnityEngine;

namespace TeleportStationPlugin;

/// <summary>
/// P6.3 控制台劫持修复：拦截 900101 复用 108 通讯终端的原生交互，改为传送三项菜单（按F）。
/// 根因：BuildPrefabClone(g108, ConsoleDef) 保留 TerrainObject_Furniture_Commu 类型，其 OnPlayerEnterRange@0x180997AD0 Slot19 会注册 InteractManager.InteractDelegate(雇佣/上传),
/// 原 P6.2 因误判为 Computer 而挂 ComputerPanel 导致 900101 仍弹“雇佣幸存者/上传我的角色”。
/// 修复：Harmony Prefix 拦截 Furniture_Commu.OnPlayerEnterRange/OnPlayerExitRange，对 id==900101 直接 return false 屏蔽原生注册，
/// 靠近提示与 F 打开改由 TeleportConsoleMenuUI 轮询接管（与原生 M 地图解耦）。
/// </summary>
public static class TeleportConsoleInteractFix
{
    private static bool _patched = false;

    public static void EnsurePatch(Harmony h)
    {
        if (_patched) return;
        _patched = true;
        try
        {
            var commuType = AccessTools.TypeByName("TerrainObject_Furniture_Commu");
            if (commuType != null)
            {
                var enter = AccessTools.Method(commuType, "OnPlayerEnterRange");
                if (enter != null)
                {
                    var pre = new HarmonyMethod(typeof(TeleportConsoleInteractFix).GetMethod(nameof(CommuEnterPrefix), System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static));
                    h.Patch(enter, prefix: pre);
                    Plugin.L.LogInfo("[TS][Fix] 已挂钩 TerrainObject_Furniture_Commu.OnPlayerEnterRange (900101 屏蔽雇佣)");
                }
                else Plugin.L.LogWarning("[TS][Fix] 未找到 Commu.OnPlayerEnterRange");
                var exit = AccessTools.Method(commuType, "OnPlayerExitRange");
                if (exit == null) exit = AccessTools.Method(commuType, "OnPlayerExitRange", new Type[0]);
                if (exit == null) exit = AccessTools.Method(commuType, "OnPlayerExitRange", new Type[] { typeof(object) });
                // 兜底：按 slot 19/22 找基类
                if (exit == null) exit = AccessTools.Method(typeof(TerrainObject), "OnPlayerExitRange");
                if (exit != null)
                {
                    try
                    {
                        var pre2 = new HarmonyMethod(typeof(TeleportConsoleInteractFix).GetMethod(nameof(CommuExitPrefix), System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static));
                        h.Patch(exit, prefix: pre2);
                        Plugin.L.LogInfo("[TS][Fix] 已挂钩 OnPlayerExitRange (清理)");
                    } catch {}
                }
            }
            else Plugin.L.LogWarning("[TS][Fix] 未找到 TerrainObject_Furniture_Commu 类型");
        }
        catch (Exception e) { Plugin.L.LogWarning($"[TS][Fix] 挂钩异常: {e.Message.Split('\n')[0]}"); }
    }

    // OnPlayerEnterRange(object obj) — P6.3 仅对 900101 屏蔽原生
    public static bool CommuEnterPrefix(object __instance, object __0)
    {
        try
        {
            var t = __instance as TerrainObject;
            if (t != null && t.attr != null && t.attr.id == 900101)
            {
                // 不注册原生 Interact，改由 TeleportConsoleMenuUI 轮询提示/按F
                // 可选：立即尝试更新最近提示（MenuUI 会在 0.2s 内自刷新，此处仅打日志）
                //Plugin.L.LogInfo($"[TS][Fix] 屏蔽 900101 原生进入 {t.GetInstanceID()}");
                return false;
            }
        } catch {}
        return true;
    }

    public static bool CommuExitPrefix(object __instance)
    {
        try
        {
            var t = __instance as TerrainObject;
            if (t != null && t.attr != null && t.attr.id == 900101)
            {
                // 900101 的退出无需原生清理，MenuUI 会自动隐藏提示
                return false;
            }
        } catch {}
        return true;
    }

    // 兼容旧签名（带 object 参数）的重载，供 Harmony 位置绑定
    public static bool CommuLeavePrefix(object __instance, object __0) { return CommuExitPrefix(__instance); }
    public static bool ComputerEnterPrefix(object __instance, object __0) { return true; }
    public static bool ComputerOpenPrefix(object __instance, object m_computerData, object m_computer) { return true; }
    public static bool InteractOpenPrefix(object __instance, GameObject go, string str, object del) { return true; }
}
