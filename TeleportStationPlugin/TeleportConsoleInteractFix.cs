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
        try
        {
            var commuType = AccessTools.TypeByName("TerrainObject_Furniture_Commu");
            if (commuType != null)
            {
                var m = AccessTools.Method(commuType, "OnPlayerEnterRange");
                if (m != null)
                {
                    h.Patch(m, prefix: new HarmonyMethod(typeof(TeleportConsoleInteractFix).GetMethod(nameof(CommuEnterPrefix), System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)));
                    Plugin.L.LogInfo("[TS][Fix] 已挂钩 TerrainObject_Furniture_Commu.OnPlayerEnterRange (900101 劫持)");
                }
                else Plugin.L.LogWarning("[TS][Fix] 未找到 Commu.OnPlayerEnterRange");
                var leave = AccessTools.Method(commuType, "OnPlayerLeaveRange");
                if (leave != null)
                {
                    h.Patch(leave, prefix: new HarmonyMethod(typeof(TeleportConsoleInteractFix).GetMethod(nameof(CommuLeavePrefix), System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)));
                }
            }
            else Plugin.L.LogWarning("[TS][Fix] 未找到 TerrainObject_Furniture_Commu 类型");

            var compType = AccessTools.TypeByName("TerrainObject_Computer");
            if (compType != null)
            {
                var m2 = AccessTools.Method(compType, "OnPlayerEnterRange");
                if (m2 != null)
                {
                    h.Patch(m2, prefix: new HarmonyMethod(typeof(TeleportConsoleInteractFix).GetMethod(nameof(ComputerEnterPrefix), System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)));
                    Plugin.L.LogInfo("[TS][Fix] 已挂钩 TerrainObject_Computer.OnPlayerEnterRange (900101 双保险)");
                }
            }

            var panelType = AccessTools.TypeByName("ComputerPanel");
            if (panelType != null)
            {
                var open = AccessTools.Method(panelType, "OpenPanel");
                if (open != null)
                {
                    h.Patch(open, prefix: new HarmonyMethod(typeof(TeleportConsoleInteractFix).GetMethod(nameof(ComputerOpenPrefix), System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)));
                    Plugin.L.LogInfo("[TS][Fix] 已挂钩 ComputerPanel.OpenPanel (900101 二次保险)");
                }
                else Plugin.L.LogWarning("[TS][Fix] 未找到 ComputerPanel.OpenPanel");
            }
            else Plugin.L.LogWarning("[TS][Fix] 未找到 ComputerPanel 类型");

            // InteractManager.OpenPanel 也拦截（通用交互入口）——按 go 上的 TerrainObject 判定
            var imType = AccessTools.TypeByName("InteractManager");
            if (imType != null)
            {
                var open2 = AccessTools.Method(imType, "OpenPanel");
                if (open2 != null)
                {
                    // OpenPanel 有多重载，取含 GameObject, string, InteractDelegate 的
                    // 若取不到第一重载，尝试遍历
                    if (open2.GetParameters().Length >= 3)
                    {
                        h.Patch(open2, prefix: new HarmonyMethod(typeof(TeleportConsoleInteractFix).GetMethod(nameof(InteractOpenPrefix), System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)));
                        Plugin.L.LogInfo("[TS][Fix] 已挂钩 InteractManager.OpenPanel (900101 通用拦截)");
                    }
                }
            }
        }
        catch (Exception e)
        {
            Plugin.L.LogWarning($"[TS][Fix] 劫持挂钩异常: {e.Message.Split('\n')[0]}");
        }
    }

    public static bool CommuEnterPrefix(object __instance, object __0)
    {
        try
        {
            var t = __instance as TerrainObject;
            if (t != null && t.attr != null && t.attr.id == 900101)
            {
                // 屏蔽原版雇佣交互注册，不弹气泡
                // 注意：不自动弹我们的面板，让 TeleportBindingController 的 E 轮询负责（避免靠近即弹）
                return false;
            }
        } catch {}
        return true;
    }

    public static bool CommuLeavePrefix(object __instance, object __0)
    {
        try
        {
            var t = __instance as TerrainObject;
            if (t != null && t.attr != null && t.attr.id == 900101) return false;
        } catch {}
        return true;
    }

    public static bool ComputerEnterPrefix(object __instance, object __0)
    {
        try
        {
            var t = __instance as TerrainObject;
            if (t != null && t.attr != null && t.attr.id == 900101) return false;
        } catch {}
        return true;
    }

    // ComputerPanel.OpenPanel(ComputerData, TerrainObject_Computer)
    public static bool ComputerOpenPrefix(object __instance, object m_computerData, object m_computer)
    {
        try
        {
            var t = m_computer as TerrainObject;
            if (t != null && t.attr != null && t.attr.id == 900101)
            {
                Plugin.L.LogInfo($"[TS][Fix] 拦截 ComputerPanel.OpenPanel 900101 -> 改弹选点面板");
                try
                {
                    // 必须在主线程弹 UI，确保 Instance 已注册
                    var ui = TeleportConsoleUI.Instance ?? TeleportConsoleUI.EnsureExists();
                    ui.ShowForConsole(t);
                } catch (Exception ex) { Plugin.L.LogWarning($"[TS][Fix] 弹选点面板异常: {ex.Message}"); }
                return false;
            }
        } catch {}
        return true;
    }

    // InteractManager.OpenPanel(GameObject, string, InteractDelegate)
    public static bool InteractOpenPrefix(object __instance, GameObject go, string str, object del)
    {
        try
        {
            if (go != null)
            {
                // 从 go 向上找 TerrainObject
                Transform tr = go.transform;
                int d = 0;
                while (tr != null && d++ < 8)
                {
                    foreach (var c in tr.GetComponents<Component>())
                    {
                        if (c == null) continue;
                        if (c is TerrainObject t && t.attr != null && t.attr.id == 900101)
                        {
                            Plugin.L.LogInfo($"[TS][Fix] 拦截 InteractManager.OpenPanel 900101 go={go.name} str={str}");
                            return false;
                        }
                    }
                    tr = tr.parent;
                }
            }
        } catch {}
        return true;
    }
}
