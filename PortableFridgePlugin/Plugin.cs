using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace PortableFridgePlugin;

/// <summary>
/// 便携小冰箱：内置容器（Backpack 10×8）+ 电瓶(85)供电 + 保鲜。
/// 保鲜原理：食物过期 = 当前游戏时间 − ItemData.properties[0] ≥ perishTime；
/// 有电时持续把容器内食物 properties[0] 前移（等效暂停腐烂）。
/// 供电：容器内电瓶按 1200WH / 5天 = 240WH/天 消耗。
/// </summary>
[BepInPlugin("com.zedzone.portablefridge", "PortableFridge", "0.1.0")]
public class Plugin : BasePlugin
{
    internal static Plugin Instance;
    internal static ManualLogSource L;

    public override void Load()
    {
        Instance = this;
        L = Log;

        AddComponent<FridgeMonitor>();

        try
        {
            PortableFridgeItem.Initialize(System.IO.Path.GetDirectoryName(typeof(Plugin).Assembly.Location));
        }
        catch (Exception e)
        {
            Log.LogError($"[PFridge] 初始化目录失败: {e}");
        }

        try
        {
            var harmony = new Harmony("com.zedzone.portablefridge");
            PatchTime(harmony);
        }
        catch (Exception e)
        {
            Log.LogError($"[PFridge] Harmony 初始化失败: {e}");
        }

        Log.LogInfo("[PFridge] 便携小冰箱插件已加载 (v0.1.0)");
    }

    private void PatchTime(Harmony harmony)
    {
        // TimeController.AddTime(float) —— 所有游戏时间推进（含睡觉）都经过它
        var addTime = typeof(TimeController).GetMethod("AddTime",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            null, new[] { typeof(float) }, null);
        if (addTime == null)
        {
            Log.LogError("[PFridge] TimeController.AddTime(float) 反射失败");
            return;
        }
        harmony.Patch(addTime, postfix: new HarmonyMethod(
            typeof(FridgeMonitor).GetMethod(nameof(FridgeMonitor.Postfix_AddTime),
                BindingFlags.NonPublic | BindingFlags.Static)));
        Log.LogInfo("[PFridge] 已挂钩 TimeController.AddTime");

        // ChangeTimeTo(float) —— 睡觉/时间跳跃（绝对设置）也 hook，差值 = 推进量
        var changeTime = typeof(TimeController).GetMethod("ChangeTimeTo",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            null, new[] { typeof(float) }, null);
        if (changeTime == null)
        {
            Log.LogError("[PFridge] TimeController.ChangeTimeTo(float) 反射失败");
            return;
        }
        harmony.Patch(changeTime, postfix: new HarmonyMethod(
            typeof(FridgeMonitor).GetMethod(nameof(FridgeMonitor.Postfix_ChangeTimeTo),
                BindingFlags.NonPublic | BindingFlags.Static)));
        Log.LogInfo("[PFridge] 已挂钩 TimeController.ChangeTimeTo");
    }
}
