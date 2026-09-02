using System;
using HarmonyLib;
using UnityEngine;

namespace TeleportStationPlugin;

/// <summary>
/// P6.1 控制台劫持修复：拦截 900101 复用 108 通讯终端的原生交互，改为传送选点面板。
/// 根因：BuildPrefabClone(g108, ConsoleDef) 保留 TerrainObject_Furniture_Commu 类型，其 OnPlayerEnterRange@0x180997AD0 Slot19 会注册 InteractManager.InteractDelegate(...雇佣...),
/// 玩家靠近即弹“招募/雇佣”气泡、按 E 进 ComputerPanel.OpenPanel@0x180588520。
/// 修复：Harmony Prefix 拦截两层——Commu.OnPlayerEnterRange (注册阶段) + ComputerPanel.OpenPanel (打开阶段)；900101 直接 return false 并弹 TeleportConsoleUI。
/// </summary>
public static class TeleportConsoleInteractFix
{
    private static bool _patched = false;

    public static void EnsurePatch(Harmony h)
    {
        if (_patched) return;
        _patched = true;
        Plugin.L.LogInfo("[TS][Fix] P6.2 原版E/Q保留模式：取消 Commu/ComputerPanel 劫持，改由 TeleportConsoleComputerFix 注入菜单");
        // P6.1 的 Commu.OnPlayerEnterRange / ComputerPanel.OpenPanel / InteractManager 拦截已全部退役
        // 保留空壳以兼容旧调用点，真实菜单由 TeleportConsoleComputerFix.EnsurePatch 负责
        // 允许原版靠近提示与 E 打开 ComputerPanel，Q 移动不受影响
    }

    public static bool CommuEnterPrefix(object __instance, object __0) { return true; }
    public static bool CommuLeavePrefix(object __instance, object __0) { return true; }
    public static bool ComputerEnterPrefix(object __instance, object __0) { return true; }
    public static bool ComputerOpenPrefix(object __instance, object m_computerData, object m_computer) { return true; }
    public static bool InteractOpenPrefix(object __instance, GameObject go, string str, object del) { return true; }
}
