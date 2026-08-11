using System;
using System.Collections.Generic;
using UnityEngine;

namespace PortableFridgePlugin;

/// <summary>
/// 供电 + 保鲜核心（v0.3.0 性能优化版）：
/// - 延迟注册物品（等 ItemManager 就绪，带重试）
/// - Harmony Postfix 挂 TimeController.AddTime / ChangeTimeTo：游戏时间推进 → 只累计
/// - 累计达阈值（0.1 游戏天 ≈ 2.4 游戏小时）才批量处理一次小冰箱：
///   读电池槽电量 → 有电则容器内食物 properties[0] 前移（暂停腐烂）+ 扣电
/// 优化：时间推进合并（睡眠时每秒数十次调用 → 每 0.1 天处理一次），native 交互降 2-3 个数量级。
/// </summary>
public class FridgeMonitor : MonoBehaviour
{
    private float _registerTimer = 8f;
    private bool _registered;
    private int _registerTries;

    // 时间推进累计（游戏天）；ChangeTimeTo 差值计算
    private static float _pendingTime;
    private static float _lastKnownTime = float.NaN;

    // 处理阈值：0.1 游戏天（约 2.4 游戏小时）批量处理一次
    private const float ProcessThreshold = 0.1f;

    // 日志节流累计
    private static float _accDays;
    private static float _noPowerAcc;

    // 保鲜写入开关
    private const bool ApplyPreservation = true;

    // isFood 判定缓存（物品类型定义加载后不变，按 itemId 缓存）
    private static readonly Dictionary<int, bool> IsFoodCache = new Dictionary<int, bool>();

    private void Update()
    {
        if (_registered) return;
        _registerTimer -= Time.deltaTime;
        if (_registerTimer > 0f) return;
        if (PortableFridgeItem.Register())
        {
            _registered = true;
        }
        else if (++_registerTries >= 10)
        {
            _registered = true; // 放弃重试，避免每帧日志
        }
        else
        {
            _registerTimer = 5f;
        }
    }

    // ---------- Harmony Postfix：游戏时间推进（只累计，不处理）----------

    internal static void Postfix_AddTime(float __0)
    {
        _pendingTime += __0;
        TryFlush();
    }

    internal static void Postfix_ChangeTimeTo(float __0)
    {
        float t = __0;
        if (!float.IsNaN(_lastKnownTime) && t > _lastKnownTime)
        {
            _pendingTime += t - _lastKnownTime;
            TryFlush();
        }
        _lastKnownTime = t;
    }

    /// <summary>累计达阈值时批量处理一次；未注册/无累计则跳过。</summary>
    private static void TryFlush()
    {
        if (!PortableFridgeItem.Registered || PortableFridgeItem.ItemId < 0) return;
        if (_pendingTime < ProcessThreshold) return;

        float batch = _pendingTime;
        _pendingTime = 0f;
        try { ProcessAllFridges(batch); }
        catch (Exception e) { Plugin.L.LogError($"[PFridge] 批量处理异常: {e}"); }
    }

    private static void ProcessAllFridges(float days)
    {
        var gc = GameController.instance;
        var cd = gc?.playerCharacter?.characterData;
        var inv = cd?.inventoryData;
        if (inv == null) return;

        var list = inv.itemList;
        if (list == null) return;

        for (int i = 0; i < list.Count; i++)
        {
            var fridgeItem = list[i];
            if (fridgeItem == null || fridgeItem.itemId != PortableFridgeItem.ItemId) continue;
            ProcessFridge(fridgeItem, days);
        }
    }

    private static void ProcessFridge(ItemData fridgeItem, float days)
    {
        var container = fridgeItem.inventoryData;   // 小冰箱的内部库存
        if (container == null) return;

        // 电池槽 BatterySlot0 格式："电池itemId|电量WH"
        float remaining = 0f;
        int batteryId = 0;
        try
        {
            string slot = fridgeItem.GetProperty("BatterySlot0");
            if (!string.IsNullOrEmpty(slot) && slot.Contains("|"))
            {
                var parts = slot.Split('|');
                if (parts.Length >= 2)
                {
                    batteryId = int.Parse(parts[0]);
                    remaining = float.Parse(parts[1]);
                }
            }
        }
        catch (Exception e) { Plugin.L.LogWarning($"[PFridge] 读取电池槽失败: {e.Message}"); }

        if (batteryId <= 0 || remaining <= 0f)
        {
            // 无电：不保鲜（节流日志）
            _noPowerAcc += days;
            if (_noPowerAcc >= 0.5f) { _noPowerAcc = 0f; Plugin.L.LogInfo("[PFridge] 电池仓无电，冰箱停止保鲜"); }
            return;
        }

        // ---- 有电：保鲜 + 手动扣电（游戏不驱动背包物品，扣电由插件负责）----
        int foodCount = 0;
        var items = container.itemList;
        if (items != null)
        {
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                if (it == null) continue;
                if (!IsFood(it)) continue;
                foodCount++;
                if (ApplyPreservation) AdvanceFreshness(it, days);
            }
        }

        // 扣电：≈240 WH/游戏天（wattage=10 标定换算，1200WH 电瓶 5 天耗尽）
        float cost = PortableFridgeItem.WattagePerDayFromWattage * days;
        remaining -= cost;
        if (remaining < 0f) remaining = 0f;
        try { fridgeItem.SetProperty("BatterySlot0", $"{batteryId}|{remaining:F6}"); }
        catch (Exception e) { Plugin.L.LogWarning($"[PFridge] 写电池槽失败: {e.Message}"); }

        // 日志节流：累计推进≥0.25 游戏天（约 6 小时）打一行
        _accDays += days;
        if (_accDays >= 0.25f)
        {
            _accDays = 0f;
            Plugin.L.LogInfo($"[PFridge] 冰箱运转: +{days:F3}天 保鲜{foodCount}份食物 电瓶剩{remaining:F0}WH");
        }
    }

    private static bool IsFood(ItemData item)
    {
        if (item == null) return false;
        int id = item.itemId;
        if (IsFoodCache.TryGetValue(id, out bool cached)) return cached;
        try
        {
            var attr = ItemManager.instance.GetItemAttrById(id);
            bool isFood = attr != null && attr.itemType == ItemType.Food;
            IsFoodCache[id] = isFood;
            return isFood;
        }
        catch { return false; }
    }

    /// <summary>把食物 properties[0] 时间戳前移 days 天（等效暂停腐烂）。</summary>
    private static bool AdvanceFreshness(ItemData item, float days)
    {
        try
        {
            var props = item.properties;
            if (props == null || props.Length == 0) return false;
            props[0] = props[0] + days;
            return true;
        }
        catch (Exception e)
        {
            Plugin.L.LogWarning($"[PFridge] 食物保鲜失败: {e.Message}");
            return false;
        }
    }
}
