using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;

namespace FridgeModPlugin;

/// <summary>
/// 大容量冰箱 MOD：将冰箱（TerrainObject_Production_Fridge）内部存储
/// 从原版 (8x16) 格改为 (22x34) 格。
///
/// 冰箱库存存于 TerrainObjectData.inventoryData/2/3（旧冰箱读档后尺寸恢复 8x16，
/// 且 UI 读的是 InventoryData 而非 fridge 的尺寸）。三层方案：
/// 1. 轮询收集所有冰箱的 InventoryData → 集合
/// 2. patch `InventoryData.get_inventorySize`（非 virtual）：集合内返回 22x34
/// 3. 字段兜底：集合内 InventoryData.inventorySize 直接写入 22x34
/// 另保留 patch `TerrainObject_Production_Fridge.get_inventorySize`（新冰箱源头）。
/// </summary>
[BepInPlugin("com.zedzone.bigfridge", "BigFridge", "0.2.3")]
public class Plugin : BasePlugin
{
    internal static Plugin Instance;
    internal static ManualLogSource L;

    /// <summary>新存储尺寸：22x34 格。</summary>
    internal static readonly Vector2Int NewSize = new Vector2Int(22, 34);

    /// <summary>属于冰箱的 InventoryData 实例集合（引用相等）。</summary>
    internal static readonly HashSet<InventoryData> FridgeInventories = new();

    public override void Load()
    {
        Instance = this;
        L = Log;

        var harmony = new Harmony("com.zedzone.bigfridge");

        // 1) 冰箱自身的 inventorySize getter（新冰箱源头）
        var fridgeGetter = typeof(TerrainObject_Production_Fridge).GetMethod("get_inventorySize",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (fridgeGetter != null)
        {
            harmony.Patch(fridgeGetter, prefix: new HarmonyMethod(
                typeof(Plugin).GetMethod(nameof(Prefix_FridgeGetInventorySize), BindingFlags.NonPublic | BindingFlags.Static)));
            Log.LogInfo("已挂钩 TerrainObject_Production_Fridge.get_inventorySize");
        }
        else Log.LogError("Fridge.get_inventorySize 反射失败");

        // 2) InventoryData 尺寸 getter（旧冰箱/读档恢复路径）
        var invGetter = typeof(InventoryData).GetMethod("get_inventorySize",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (invGetter != null)
        {
            harmony.Patch(invGetter, prefix: new HarmonyMethod(
                typeof(Plugin).GetMethod(nameof(Prefix_InventoryDataGetSize), BindingFlags.NonPublic | BindingFlags.Static)));
            Log.LogInfo("已挂钩 InventoryData.get_inventorySize");
        }
        else Log.LogError("InventoryData.get_inventorySize 反射失败");

        // 3) 轮询收集冰箱库存 + 字段兜底
        AddComponent<FieldFixer>();

        Log.LogInfo("大容量冰箱插件已加载 (v0.2.3)");
    }

    /// <summary>Fridge.get_inventorySize 前缀：新尺寸。</summary>
    private static bool Prefix_FridgeGetInventorySize(ref Vector2Int __result)
    {
        __result = NewSize;
        return false;
    }

    /// <summary>InventoryData.get_inventorySize 前缀：属于冰箱的库存返回新尺寸。</summary>
    private static bool Prefix_InventoryDataGetSize(InventoryData __instance, ref Vector2Int __result)
    {
        if (__instance != null && FridgeInventories.Contains(__instance))
        {
            __result = NewSize;
            return false;
        }
        return true;
    }
}
