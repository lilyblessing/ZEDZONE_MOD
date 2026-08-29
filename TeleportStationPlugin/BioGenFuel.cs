using System;
using UnityEngine;

namespace TeleportStationPlugin;

/// <summary>
/// v0.8.0 P2：生物能电站（900103）—— 观察版（不修改游戏数据）。
/// 目标定位：斯特林燃料机制（烧什么物品/消耗速率/消耗代码点），为「只烧腐肉205+过期食品 + 速率×0.5」奠基。
/// ItemFeatureType 无 Food 类（已核实：Liquid/Combustible/BBQItem/...）→ 白名单不能靠 feature 粗筛；
/// 改用「消耗点判定」：准入 = 烧什么——把斯特林默认「可燃物判定」改为「白名单判定」（下轮实现）。
/// 观察手段：OnGeneratorStart/Stop postfix（public non-virtual 稳定）+ 10s 采样 fuelInventoryData。
/// 判别：TerrainObject 组件 attr id == 900103（引用/ID 双保险）。
/// </summary>
public static class BioGenFuel
{
    private static float _lastSample;
    private static bool _warnedNoAttrField;
    private static readonly System.Collections.Generic.HashSet<long> _markedContainers = new();
    private static float _lastRejectLog;

    /// <summary>hook get_fuelInventoryData postfix：标记 900103 燃料容器（标题「生物燃料」+ 一次性记录）——白名单识别基准。</summary>
    public static void GetFuelInventoryPostfix(TerrainObject_Production_StirlingGenerator __instance, ref InventoryData __result)
    {
        try
        {
            if (__result == null || !IsBioGen(__instance)) return;
            long ptr = 0;
            try { ptr = (long)__result.Pointer; } catch { ptr = __result.GetHashCode(); }
            if (_markedContainers.Add(ptr))
            {
                try { Reflect.Set(__result, "inventoryTitleName", GameLocale.T("生物燃料仓", "Bio Fuel Hopper")); } catch { }
                // v0.8.4：清空 itemFeatureLimit（接管准入——UI 层不再拦任何物品，唯一白名单 = AddItem hook）
                try
                {
                    var empty = new Il2CppSystem.Collections.Generic.List<ItemFeatureType>();
                    Reflect.Set(__result, "itemFeatureLimit", empty);
                    Plugin.L.LogInfo($"[TS] BioGen 燃料仓已标记并接管准入（itemFeatureLimit 清空，白名单=AddItem hook）；size=({__result.inventorySizeX}x{__result.inventorySizeY})");
                }
                catch (Exception e1)
                {
                    Plugin.L.LogWarning($"[TS] itemFeatureLimit 清空异常: {e1.Message.Split('\n')[0]}");
                    Plugin.L.LogInfo($"[TS] BioGen 燃料仓已标记（仅标记，准入接管失败）；size=({__result.inventorySizeX}x{__result.inventorySizeY})");
                }
            }
        }
        catch { }
    }

    /// <summary>v0.8.5：准入判定点 = InventoryData.PassesFeatureLimit(ItemAttr)（private non-virtual，UI 拖放与 TryAddItem 公共入口）。
    /// 生物燃料仓：白名单 = 腐肉 205 / 食品类（含过期食品，attr 级判定）；其余拒绝。</summary>
    public static bool PassesFeatureLimitPrefix(InventoryData __instance, ItemAttr attr, ref bool __result)
    {
        try
        {
            if (__instance == null || attr == null) return true;
            if (!IsBioGenContainer(__instance)) return true; // 非生物燃料仓走原判定
            int id = -1;
            try { id = Convert.ToInt32(Reflect.Get(attr, "itemId")); } catch { }
            bool isFood = false;
            try
            {
                var itype = Reflect.Get(attr, "itemType");
                isFood = itype != null && itype.ToString().Contains("Food");
            }
            catch { }
            if (id == 205 || isFood)
            {
                __result = true; // 白名单通过（腐肉 / 食品类）
                return false;
            }
            __result = false;
            if (Time.unscaledTime - _lastRejectLog > 3f)
            {
                _lastRejectLog = Time.unscaledTime;
                Plugin.L.LogInfo($"[TS] BioGen 拒绝燃料: id={id}");
            }
            return false;
        }
        catch { return true; }
    }

    /// <summary>hook InventoryData 放入入口（AddItem 私有漏斗 + Try* 三入口）prefix（v0.8.4 用 __0 位置绑定——参数名不匹配曾致 patch 失败）。
    /// 实时容器归属判定；生物燃料仓 → 仅允许腐肉 205 / 过期食品。</summary>
    public static bool WhitelistPrefix(InventoryData __instance, ItemData __0)
    {
        try
        {
            if (__instance == null) return true;
            if (!IsBioGenContainer(__instance)) return true; // 非生物燃料仓放行（原版行为）
            ItemData item = __0;
            bool ok = item != null && IsAllowedFuel(item);
            if (ok) return true;
            if (Time.unscaledTime - _lastRejectLog > 3f)
            {
                _lastRejectLog = Time.unscaledTime;
                Plugin.L.LogInfo($"[TS] BioGen 拒绝燃料: {(item == null ? "null" : (Reflect.Get(item, "itemName") + " id=" + FuelItemId(item)))}");
            }
            return false; // 拒绝放入（物品将回到原处）
        }
        catch { return true; }
    }

    /// <summary>v0.8.2：实时容器归属——遍历斯特林活动实例，找到持有该容器的 900103（不依赖标记）。</summary>
    private static bool IsBioGenContainer(InventoryData fd)
    {
        try
        {
            var list = TerrainObject_Production_StirlingGenerator.ActiveObjects_StirlingGenerator;
            if (list == null) return false;
            for (int i = 0; i < list.Count; i++)
            {
                var g = list[i];
                if (g == null || !IsBioGen(g)) continue;
                try
                {
                    var fuel = g.fuelInventoryData;
                    if (fuel != null && ReferenceEquals(fuel, fd)) return true;
                }
                catch { }
            }
            return false;
        }
        catch { return false; }
    }

    private static int FuelItemId(ItemData it)
    {
        try
        {
            var attr = Reflect.Get(it, "itemAttr");
            return Convert.ToInt32(Reflect.Get(attr, "itemId"));
        }
        catch { return -1; }
    }

    /// <summary>白名单：腐肉 205 或 过期食品（游戏时间 − 生产时间 ≥ 保质期）。</summary>
    private static bool IsAllowedFuel(ItemData it)
    {
        try
        {
            int id = FuelItemId(it);
            if (id == 205) return true; // 腐肉
            // 过期食品：Food 类且有过期标记
            try
            {
                var attr = Reflect.Get(it, "itemAttr");
                var itype = Reflect.Get(attr, "itemType");
                if (itype == null || !itype.ToString().Contains("Food")) return false;
                var expired = Reflect.Get(it, "IsFoodExpired");
                if (expired == null) return false;
                if (expired is bool b) return b;
                var m = it.GetType().GetMethod("IsFoodExpired");
                if (m != null) return (bool)m.Invoke(it, null);
            }
            catch { }
            return false;
        }
        catch { return false; }
    }

    public static void OnGeneratorStartPostfix(TerrainObject_Production_StirlingGenerator __instance)
    {
        try
        {
            if (!IsBioGen(__instance)) return;
            Plugin.L.LogInfo("[TS] BioGen 启动（开始观察）");
        }
        catch (Exception e) { Plugin.L.LogWarning($"[TS] BioGen OnStart 异常: {e.Message.Split('\n')[0]}"); }
    }

    public static void OnGeneratorStopPostfix(TerrainObject_Production_StirlingGenerator __instance)
    {
        try
        {
            if (!IsBioGen(__instance)) return;
            Plugin.L.LogInfo("[TS] BioGen 停机");
        }
        catch { }
    }

    /// <summary>由 RegistrationProbe.Update 调用：每 10s 采样 900103 燃料库存/限制（消耗观察）。</summary>
    public static void Tick()
    {
        try
        {
            var list = TerrainObject_Production_StirlingGenerator.ActiveObjects_StirlingGenerator;
            if (list == null) return;
            float now = Time.unscaledTime;
            bool sampleDue = now - _lastSample > 10f;
            for (int i = 0; i < list.Count; i++)
            {
                var g = list[i];
                if (g == null) continue;
                if (!IsBioGen(g)) continue;
                if (sampleDue)
                {
                    _lastSample = now;
                    try
                    {
                        var fd = g.fuelInventoryData;
                        string info = fd == null ? "fuelInventoryData=null"
                            : $"size=({fd.inventorySizeX}x{fd.inventorySizeY}) limit={Reflect.Get(fd, "itemFeatureLimit")} title={Reflect.Get(fd, "inventoryTitleName")} items={Reflect.Get(fd, "itemList")}";
                        Plugin.L.LogInfo($"[TS] BioGen 观察: {info}");
                    }
                    catch (Exception e) { Plugin.L.LogWarning($"[TS] BioGen 采样异常: {e.Message.Split('\n')[0]}"); }
                }
            }
        }
        catch { }
    }

    private static bool IsBioGen(TerrainObject_Production_StirlingGenerator g)
    {
        try
        {
            var to = FindTerrainObject(g.transform);
            if (to == null) return false;
            object attr = null;
            try { attr = Reflect.Get(to, "attr"); } catch { }
            if (attr == null)
            {
                // 探测：attr 字段名未知——首轮 dump 组件字段线索（诊断）
                if (!_warnedNoAttrField)
                {
                    _warnedNoAttrField = true;
                    foreach (var f in to.GetType().GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                    {
                        if (f.Name.StartsWith("Native") || f.Name is "isWrapped" or "pooledPtr") continue;
                        Plugin.L.LogInfo($"[TS] BioGen 诊断: {to.GetType().Name}.{f.Name} = {TryRead(f.GetValue(to))}");
                    }
                }
                return false;
            }
            // ID/引用双保险
            if (RegistrationStore.Attrs.TryGetValue(900103, out var our) && ReferenceEquals(attr, our)) return true;
            return AttrId(attr) == 900103;
        }
        catch { return false; }
    }

    private static string TryRead(object v)
    {
        if (v == null) return "null";
        try { return v.GetType().Name; } catch { return "?"; }
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