using System;
using System.Collections.Generic;
using UnityEngine;

namespace TeleportStationPlugin;

/// <summary>
/// v0.9.2 P3：建筑盘（900102）电池仓 4×4 + 充电（接斯特林=原版速率、接生物能=×4）。
/// 机制定案（2026-08-31 反编译 ProductionManager.UpdateBatteryCharger 0x180930240）：
///   原版充电公式 totalWh = powerInputSufficientFloat[0xC8] × attr.electricWattage[0xA4] × addedTime(游戏天) × 24.0
///   → 平分给仓内可充电池 → ItemFeature_Battery.ChargeBattery(itemData, whPerBattery)（内部容量封顶+落盘）。
/// 本实现：
///   - 容器 = 盘的 fuelInventoryData（Stirling 克隆，F 打开燃料仓 UI 天然可用）→ 改 4×4 + 标题「电池仓」+ 清 itemFeatureLimit（电池可入）
///   - 时间增量 = TimeController.AddTime postfix 累积（PortableFridge 已验证模式）
///   - 供电 = productionData.powerInputSufficientFloat > 0；倍率 = connectedElectricGeneratorList 含 900103（生物能）→×4 else ×1
///   - 电池识别 = 试调 GetBatteryRemainingPower（非电池内部校验返回 false/异常，安全跳过）
/// </summary>
public static class BatteryChargeFix
{
    private const int PadId = 900102;
    private const int BioGenId = 900103;
    private static readonly System.Collections.Generic.HashSet<long> _initKeys = new(); // 实例初始化去重
    private static float _lastScan = -1f;
    private static float _lastLog;
    private static float _pendingGameDays; // TimeController hook 累积的未结算游戏天
    private static float _lastAbsTime = float.NaN; // ChangeTimeTo 跟踪（睡觉=绝对跳变）

    /// <summary>TimeController.AddTime postfix 入口：累计游戏天增量（1f = 1 游戏天）。__0 位置绑定（命名参数曾致 IL Compile Error）。</summary>
    public static void OnGameTimeAdded(float __0)
    {
        try { if (__0 > 0f) _pendingGameDays += __0; } catch { }
    }

    /// <summary>TimeController.ChangeTimeTo postfix 入口：睡觉等绝对跳变——差值入池（PortableFridge 同款协同）。__0 位置绑定。</summary>
    public static void OnGameTimeChangedTo(float __0)
    {
        try
        {
            if (!float.IsNaN(_lastAbsTime) && __0 > _lastAbsTime)
                _pendingGameDays += __0 - _lastAbsTime;
            _lastAbsTime = __0;
        }
        catch { }
    }

    /// <summary>由 RegistrationProbe.Update 每帧调用（内部 0.5s 节流）。</summary>
    public static void Tick()
    {
        try
        {
            float now = Time.unscaledTime;
            if (now - _lastScan < 0.5f) return;
            _lastScan = now;
            var list = TerrainObject_Production_StirlingGenerator.ActiveObjects_StirlingGenerator;
            if (list == null) return;
            float gameDays = _pendingGameDays;
            if (gameDays > 0f) _pendingGameDays = 0f; // 结算清零（单盘场景；多盘并存各充一轮，可接受）
            for (int i = 0; i < list.Count; i++)
            {
                var g = list[i];
                if (g == null || !IsPadInstance(g)) continue;
                try
                {
                    EnsureContainer(g);
                    if (gameDays > 0f) Charge(g, gameDays);
                }
                catch (Exception e) { Plugin.L.LogWarning($"[TS] 电池仓 Tick 异常: {e.Message.Split('\n')[0]}"); }
            }
        }
        catch { }
    }

    /// <summary>一次性初始化（每实例）：4×4 容器 + 标题 + itemFeatureLimit 清空（电池可入）。</summary>
    private static void EnsureContainer(TerrainObject_Production_StirlingGenerator g)
    {
        long key = 0;
        try { key = (long)g.Pointer; } catch { key = g.GetHashCode(); }
        if (_initKeys.Contains(key)) return;
        _initKeys.Add(key);
        var fd = g.fuelInventoryData;
        if (fd == null) return;
        try { Reflect.Set(fd, "inventoryTitleName", GameLocale.T("电池仓", "Battery Cell")); } catch { }
        try
        {
            var empty = new Il2CppSystem.Collections.Generic.List<ItemFeatureType>();
            Reflect.Set(fd, "itemFeatureLimit", empty); // 清准入（电池无 Combustible 原版会被拒）
        }
        catch { }
        try { Reflect.Set(g, "fuelInventorySize", new Vector2Int(4, 4)); } catch { } // 源头尺寸
        try { Reflect.Set(fd, "inventorySize", new Vector2Int(4, 4)); } catch { }    // 容器尺寸（UI 读它）
        Plugin.L.LogInfo($"[TS] 电池仓初始化: 4×4 size=({fd.inventorySizeX}x{fd.inventorySizeY})");
    }

    /// <summary>充电主逻辑（反编译公式实现）。返回本轮充电 Wh。</summary>
    private static float Charge(TerrainObject_Production_StirlingGenerator g, float gameDays)
    {
        // v0.9.3：虚拟供电（范围检测）——盘不是游戏原生消费者，powerInputSufficientFloat 恒 0；
        // 供电 = 50m 内运行中的发电机（900103 生物能 / 120 原版斯特林），源判×4
        float sufficient;
        float mult;
        if (!FindSupply(g, out sufficient, out mult)) return 0f;
        var inv = g.fuelInventoryData;
        if (inv == null) return 0f;

        // 收集仓内电池（试调 GetBatteryRemainingPower 识别，非电池安全跳过）
        var slots = new List<ItemData>();
        var list = inv.itemList;
        if (list == null) return 0f;
        for (int i = 0; i < list.Count; i++)
        {
            var it = list[i];
            if (it == null) continue;
            try { ItemFeature_Battery.GetBatteryRemainingPower(it); slots.Add(it); } catch { }
        }
        if (slots.Count == 0) return 0f;

        float totalWh = sufficient * 24f * mult * gameDays; // = effectiveWatt × 天 × 24 × 倍率
        float each = totalWh / slots.Count;
        if (each <= 0f) return 0f;
        for (int i = 0; i < slots.Count; i++)
        {
            try { ItemFeature_Battery.ChargeBattery(slots[i], each); }
            catch { }
        }
        if (Time.unscaledTime - _lastLog > 5f)
        {
            _lastLog = Time.unscaledTime;
            Plugin.L.LogInfo($"[TS] 电池仓充电: {slots.Count} 枚 +{totalWh:F1}Wh 源满足={sufficient:F2} 倍率×{mult}");
        }
        return totalWh;
    }

    /// <summary>虚拟供电检测（v0.9.3）：50m 内运行中的发电机 → (满足度1.0, 倍率)；生物能(900103)优先生效 ×4。
    /// 替代原 powerInputSufficientFloat/connectedElectricGeneratorList（克隆盘非原生消费者，恒无电）。</summary>
    private static bool FindSupply(TerrainObject_Production_StirlingGenerator pad, out float sufficient, out float mult)
    {
        sufficient = 0f;
        mult = 1f;
        try
        {
            var padPos = pad.transform.position;
            var list = TerrainObject_Production_StirlingGenerator.ActiveObjects_StirlingGenerator;
            if (list == null) return false;
            bool found = false;
            for (int i = 0; i < list.Count; i++)
            {
                var g = list[i];
                if (g == null || ReferenceEquals(g, pad)) continue;
                int id = GenId(g);
                if (id != BioGenId && id != 120) continue; // 生物能 900103 / 原版斯特林 120
                var dp = g.transform.position - padPos;
                if (dp.x * dp.x + dp.y * dp.y > 50f * 50f) continue; // 虚拟电线距离 50m
                if (!IsRunning(g)) continue;
                found = true;
                if (id == BioGenId) mult = 4f; // 生物能 ×4（若有多个源取优）
            }
            if (!found) return false;
            sufficient = 1f;
            return true;
        }
        catch { return false; }
    }

    private static int GenId(TerrainObject_Production_StirlingGenerator g)
    {
        try
        {
            var to = FindTerrainObject(g.transform);
            if (to == null) return -1;
            object attr = null;
            try { attr = Reflect.Get(to, "attr"); } catch { }
            if (attr == null) return -1;
            if (RegistrationStore.Attrs.TryGetValue(BioGenId, out var ours) && ReferenceEquals(attr, ours)) return BioGenId;
            return AttrId(attr);
        }
        catch { return -1; }
    }

    private static bool IsRunning(TerrainObject_Production_StirlingGenerator g)
    {
        try
        {
            var v = Reflect.Get(g, "isRunning");
            return v != null && (bool)v;
        }
        catch { return false; }
    }

    /// <summary>供电倍率（v0.9.3 起由 FindSupply 直接给出，本方法废弃）。</summary>
    // （已由 FindSupply 统一处理——供电/倍率同源判定，删除原 powerInputSufficientFloat 路径）

    private static bool IsPadInstance(TerrainObject_Production_StirlingGenerator g)
    {
        try
        {
            var to = FindTerrainObject(g.transform);
            if (to == null) return false;
            object attr = null;
            try { attr = Reflect.Get(to, "attr"); } catch { }
            if (attr == null) return false;
            if (RegistrationStore.Attrs.TryGetValue(PadId, out var our) && ReferenceEquals(attr, our)) return true;
            return AttrId(attr) == PadId;
        }
        catch { return false; }
    }

    private static int AttrId(object attr)
    {
        try { return Convert.ToInt32(Reflect.Get(attr, "id")); } catch { return -1; }
    }

    private static Component FindTerrainObject(Transform t)
    {
        int d = 0;
        while (t != null && d++ < 16)
        {
            foreach (var c in t.GetComponents<Component>())
            {
                if (c == null) continue;
                string n = c.GetType().Name;
                if (n.Contains("TerrainObject") || n.Contains("Stirling"))
                    return c;
            }
            t = t.parent;
        }
        return null;
    }
}