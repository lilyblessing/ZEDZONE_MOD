using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;

namespace PortableFridgePlugin;

/// <summary>
/// 供电 + 保鲜核心：
/// - 延迟注册物品（等 ItemManager 就绪，带重试）
/// - Harmony Postfix 挂 TimeController.AddTime：游戏时间每推进 delta 即处理小冰箱
/// - 处理：找玩家主背包中的小冰箱 → 其容器(inventoryData)内找电瓶(85)
///   有电 → 扣电 + 容器内食物 properties[0] 前移（暂停腐烂）
/// </summary>
public class FridgeMonitor : MonoBehaviour
{
    private float _registerTimer = 8f;
    private bool _registered;
    private int _registerTries;

    // 时间累计（用于标定 AddTime/ChangeTimeTo 单位：睡一天后看候选天数）
    private static float _totalTimeAdvanced;
    private static long _tickCount;
    private static float _lastKnownTime = float.NaN;
    private static float _accDays;   // 冰箱运转日志节流累计
    private static float _noPowerAcc; // 无电日志节流累计

    // 单位已确认：AddTime/ChangeTimeTo 参数单位 = 游戏天（1f = 1 游戏天；0.0006 ≈ 1 游戏分钟）
    // 保鲜写入开启；扣电待电池槽方案（见 BatteryConsuming 探查）
    private const bool ApplyPreservation = true;
    // 日志节流
    private static float _lastLogTime;

    private void Update()
    {
        if (!_registered)
        {
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

        // 每 10 秒打印一次标定日志（AddTime 累计值）
        if (Time.time - _lastLogTime > 10f)
        {
            _lastLogTime = Time.time;
            Plugin.L.LogInfo($"[PFridge] 标定: AddTime累计={_totalTimeAdvanced:F1} (调用{_tickCount}次) 注册={PortableFridgeItem.Registered}(id={PortableFridgeItem.ItemId})");
        }
    }

    // ---------- Harmony Postfix：游戏时间推进 ----------

    internal static void Postfix_AddTime(float __0)
    {
        _totalTimeAdvanced += __0;
        _tickCount++;
        if (float.IsNaN(_lastKnownTime)) _lastKnownTime = __0;
        else _lastKnownTime += __0;
        try { OnTimeAdvanced(__0); }
        catch (Exception e) { Plugin.L.LogError($"[PFridge] OnTimeAdvanced 异常: {e}"); }
    }

    internal static void Postfix_ChangeTimeTo(float __0)
    {
        float t = __0;
        _tickCount++;
        if (!float.IsNaN(_lastKnownTime) && t > _lastKnownTime)
        {
            float delta = t - _lastKnownTime;
            try { OnTimeAdvanced(delta); }
            catch (Exception e) { Plugin.L.LogError($"[PFridge] OnTimeAdvanced(ChangeTimeTo) 异常: {e}"); }
        }
        _lastKnownTime = t;
    }

    private static void OnTimeAdvanced(float delta)
    {
        if (!PortableFridgeItem.Registered || PortableFridgeItem.ItemId < 0) return;

        // 标定日志（每 60 次打印一次，避免刷屏）
        if (_tickCount % 60 == 0)
        {
            Plugin.L.LogInfo($"[PFridge] 标定: delta={delta:F4} 累计={_totalTimeAdvanced:F1} " +
                             $"候选天数(1440制)={delta / 1440f:F6} (86400制)={delta / 86400f:F6}");
        }

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
            ProcessFridge(fridgeItem, delta);
        }
    }

    private static void ProcessFridge(ItemData fridgeItem, float delta)
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

        float days = delta;   // delta 单位 = 游戏天

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

        // 扣电：240 WH/游戏天（wattage=10 换算，1200WH 电瓶 5 天耗尽）
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
            Plugin.L.LogInfo($"[PFridge] 冰箱运转: +{days:F4}天 保鲜{foodCount}份食物 电瓶剩{remaining:F0}WH");
        }
    }

    private static bool IsFood(ItemData item)
    {
        try
        {
            var attr = ItemManager.instance.GetItemAttrById(item.itemId);
            return attr != null && attr.itemType == ItemType.Food;
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
