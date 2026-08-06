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
[BepInPlugin("com.zedzone.bigfridge", "BigFridge", "1.1.0")]
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
            Log.LogInfo("[BigFridge] 已挂钩 TerrainObject_Production_Fridge.get_inventorySize");
        }
        else Log.LogError("[BigFridge] Fridge.get_inventorySize 反射失败");

        // 2) InventoryData 尺寸 getter（旧冰箱/读档恢复路径）
        var invGetter = typeof(InventoryData).GetMethod("get_inventorySize",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (invGetter != null)
        {
            harmony.Patch(invGetter, prefix: new HarmonyMethod(
                typeof(Plugin).GetMethod(nameof(Prefix_InventoryDataGetSize), BindingFlags.NonPublic | BindingFlags.Static)));
            Log.LogInfo("[BigFridge] 已挂钩 InventoryData.get_inventorySize");
        }
        else Log.LogError("[BigFridge] InventoryData.get_inventorySize 反射失败");

        // 3) 轮询收集冰箱库存 + 字段兜底
        AddComponent<FieldFixer>();

        Log.LogInfo("[BigFridge] 大容量冰箱插件已加载 (v1.1.0)");
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

/// <summary>
/// 轮询收集冰箱库存（读档后冰箱才实例化，需多轮重试），并直接写入字段兜底。
/// </summary>
public class FieldFixer : MonoBehaviour
{
    private float _timer = 6f;
    private int _round;

    private void Update()
    {
        if (_round >= 24) return; // 最多轮询 24 轮（约 2 分钟，覆盖读档延迟）
        _timer -= Time.deltaTime;
        if (_timer > 0f) return;
        _timer = 5f;
        _round++;

        try
        {
            Plugin.FridgeInventories.Clear();
            var all = Resources.FindObjectsOfTypeAll<TerrainObject_Production_Fridge>();
            int fridgeCount = all != null ? all.Length : 0;
            int invCount = 0;
            if (all != null)
            {
                for (int i = 0; i < all.Length; i++)
                {
                    var fridge = all[i];
                    if (fridge == null || fridge.objectData == null) continue;

                    // 设置冰箱自身尺寸（走属性 setter）
                    try { fridge.inventorySize = Plugin.NewSize; } catch { }

                    // 收集并设置其 InventoryData
                    foreach (var inv in new[] { fridge.objectData.inventoryData, fridge.objectData.inventoryData2, fridge.objectData.inventoryData3 })
                    {
                        if (inv == null) continue;
                        Plugin.FridgeInventories.Add(inv);
                        try { inv.inventorySize = Plugin.NewSize; } catch { }
                        invCount++;
                    }
                }
            }
            Plugin.L.LogInfo($"[BigFridge] 第{_round}轮: 冰箱={fridgeCount} 库存={invCount} 集合={Plugin.FridgeInventories.Count}");
        }
        catch (Exception e)
        {
            Plugin.L.LogError($"[BigFridge] 字段兜底失败: {e}");
        }
    }
}
