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
[BepInPlugin("com.zedzone.bigfridge", "BigFridge", "1.2.2")]
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
/// 轮询收集冰箱库存（读档后冰箱才实例化；游戏读档会重置容器尺寸，故每次启动
/// 都必须重新收集）。收集完成后降频为 60 秒低频守护（覆盖游戏内切换存档、
/// 新放置冰箱等后续出现 8x16 容器的场景）。频率自适应：前 8 轮 5 秒，
/// 之后 20 秒；库存>0 且连续 3 轮完全稳定才进入守护；日志仅变化时输出。
/// </summary>
public class FieldFixer : MonoBehaviour
{
    private float _timer = 5f;
    private int _round;
    private int _lastFridgeCount = -1;
    private int _lastInvCount = -1;
    private int _stableRounds;
    private bool _idle; // 低频守护模式（收集完成后）
    private readonly HashSet<long> _seenFridgePtrs = new();
    private readonly HashSet<long> _seenInvPtrs = new();

    private void Update()
    {
        _timer -= Time.deltaTime;
        if (_timer > 0f) return;
        // 频率：守护模式 60 秒；否则前 8 轮 5 秒（快速覆盖读档），之后 20 秒
        _timer = _idle ? 60f : (_round < 8 ? 5f : 20f);
        _round++;

        try
        {
            var all = Resources.FindObjectsOfTypeAll<TerrainObject_Production_Fridge>();
            int fridgeCount = all != null ? all.Length : 0;

            // 集合每轮重建（读档后对象引用需刷新）
            Plugin.FridgeInventories.Clear();
            int invCount = 0;
            if (all != null)
            {
                for (int i = 0; i < all.Length; i++)
                {
                    var fridge = all[i];
                    if (fridge == null || fridge.objectData == null) continue;

                    // 冰箱自身尺寸：仅首次见到时写入（去重）
                    if (_seenFridgePtrs.Add(fridge.Pointer.ToInt64()))
                    {
                        try { fridge.inventorySize = Plugin.NewSize; } catch { }
                    }

                    foreach (var inv in new[] { fridge.objectData.inventoryData, fridge.objectData.inventoryData2, fridge.objectData.inventoryData3 })
                    {
                        if (inv == null) continue;
                        Plugin.FridgeInventories.Add(inv);
                        // 库存尺寸：仅首次见到时写入（去重）
                        if (_seenInvPtrs.Add(inv.Pointer.ToInt64()))
                        {
                            try { inv.inventorySize = Plugin.NewSize; } catch { }
                        }
                        invCount++;
                    }
                }
            }

            // 稳定性：冰箱数与库存数都稳定才累计（先判断变化再更新基准）
            bool changed = fridgeCount != _lastFridgeCount || invCount != _lastInvCount;
            if (!changed)
                _stableRounds++;
            else
                _stableRounds = 0;

            // 收集完成 → 进入低频守护（不彻底停止，覆盖游戏内换档/新冰箱）
            if (!_idle && invCount > 0 && _stableRounds >= 3)
            {
                _idle = true;
                _lastFridgeCount = fridgeCount;
                _lastInvCount = invCount;
                Plugin.L.LogInfo($"[BigFridge] 收集完成(第{_round}轮): 冰箱={fridgeCount} 库存={invCount} 集合={Plugin.FridgeInventories.Count}，进入低频守护(60s)");
                return;
            }

            // 日志降噪：仅数量变化时输出
            if (changed)
            {
                string tag = _idle ? "[BigFridge·守护]" : $"[BigFridge] 第{_round}轮";
                Plugin.L.LogInfo($"{tag}: 冰箱={fridgeCount} 库存={invCount} 集合={Plugin.FridgeInventories.Count}");
            }
            _lastFridgeCount = fridgeCount;
            _lastInvCount = invCount;
        }
        catch (Exception e)
        {
            Plugin.L.LogError($"[BigFridge] 字段兜底失败: {e}");
        }
    }
}
