using System;
using UnityEngine;

namespace TeleportStationPlugin;

/// <summary>
/// v0.8.9 P2：生物能电站（900103）燃料白名单——烧录链重做（Ghidra 反编译定案版）。
/// Ghidra 定案（FUN_180930AB0 = ProductionManager.UpdateStirlingGenerator）：
///   1. 烧录容器 = productionData.inventoryData1（不是 Stirling.fuelInventoryData！读档场景两者是不同对象）
///   2. 启动门 = 从容器尾扫描首个 attr.itemFeatures.Contains(Combustible) 的物品（腐肉无 Combustible → 永远不启动）
///   3. 消耗 = item.itemNumberFloat 直减（不走 CostItemDurability → 旧半速 hook 打空）
///   4. 产出 = TryAddItem(炭 id=6) 回仓（白名单必须豁免 6 号）
/// 四件套：
///   A. UpdateStirlingGenerator prefix：900103 判定 → 标记 inventoryData1（真烧录容器）+ ref addedTime×0.5（半速）+ 开扫描窗
///   B. ItemManager.GetItemAttrById prefix：扫描窗内对白名单燃料返回木头 attr（自带 Combustible）→ 启动门放行（腐肉也能烧）
///   C. PassesFeatureLimit prefix：生物容器 attr 级粗筛（205/炭/Food 放行，木头/金属拒）
///   D. TryAddItem/AddItem prefix：item 级白名单（Food 类全放行 / 腐肉205 / 炭6 豁免；木头·金属拒）——v0.8.10 终版
/// 容器识别：指针标记集合（来自 inventoryData1 + get_fuelInventoryData 双来源），不再依赖 ActiveObjects 遍历。
/// </summary>
public static class BioGenFuel
{
    private static readonly System.Collections.Generic.HashSet<long> _marked = new();
    private static bool _inBioScan;          // UpdateStirlingGenerator 窗口（单线程同步，窗口=单次方法调用，安全）
    private static ItemAttr _woodAttr;       // 假 Combustible：木头 attr（id 0 自带 Combustible）
    private static float _lastRejectLog;

    /// <summary>v0.8.9 A：UpdateStirlingGenerator prefix——生物仓三件事：标记烧录容器 / 半速 / 开扫描窗。</summary>
    public static bool StirlingUpdatePrefix(ProductionData generatorData, ref float addedTime)
    {
        try
        {
            if (generatorData == null) return true;
            if (!IsBioGenProduction(generatorData)) return true; // 非 900103 走原版
            try
            {
                var inv = generatorData.inventoryData1;
                if (inv != null) Mark(inv, true);
            }
            catch (Exception ea) { Plugin.L.LogWarning($"[TS] BioGen 烧录容器标记异常: {ea.Message.Split('\n')[0]}"); }
            addedTime *= 0.5f;   // 半速消耗（发电量不变）
            _inBioScan = true;   // 扫描窗：让 GetItemAttrById 为白名单燃料伪造 Combustible
            return true;
        }
        catch { return true; }
    }

    public static void StirlingUpdatePostfix(ProductionData generatorData)
    {
        _inBioScan = false; // 窗口必须清除（哪怕原方法异常也由 Harmony 保证 postfix 执行）
    }

    /// <summary>v0.8.9 B：启动门伪造——扫描窗内 GetItemAttrById 对白名单燃料返回木头 attr（含 Combustible）。
    /// 炭(6) 放行原版 attr（灰烬注入需要真实炭 attr）；窗口外零开销直通。</summary>
    public static bool GetAttrByIdPrefix(ItemManager __instance, int itemId, ref ItemAttr __result)
    {
        try
        {
            if (!_inBioScan) return true;
            if (itemId == 6) return true;
            if (_woodAttr == null)
            {
                bool save = _inBioScan; _inBioScan = false; // 防递归
                try { _woodAttr = __instance.GetItemAttrById(0); } catch { }
                _inBioScan = save;
            }
            if (_woodAttr == null) return true; // 取不到就放行（走原逻辑，不阻塞）
            __result = _woodAttr;
            return false;
        }
        catch { return true; }
    }

    /// <summary>v0.8.9 C：PassesFeatureLimit prefix——生物燃料仓 attr 级粗筛（205/炭/Food 放行；木头/金属等拒）。
    /// attr 直传（interop 直接访问，不走反射，杜绝 id=0 误读）。expiry 严格判定在 D 环。</summary>
    public static bool PassesFeatureLimitPrefix(InventoryData __instance, ItemAttr attr, ref bool __result)
    {
        try
        {
            if (__instance == null || attr == null) return true;
            if (!IsMarked(__instance)) return true; // 非生物燃料仓走原版
            int id = -1; try { id = attr.itemId; } catch { }
            bool isFood = false; try { isFood = attr.itemType.ToString().Contains("Food"); } catch { }
            if (id == 205 || id == 6 || isFood)
            {
                __result = true; // 粗筛放行（严格判定交给 WhitelistPrefix）
                return false;
            }
            __result = false;
            LogReject(id);
            return false;
        }
        catch { return true; }
    }

    /// <summary>v0.8.9 D：TryAddItem/AddItem prefix——item 级严格白名单（腐肉205 / 炭6 / 过期食品）。</summary>
    public static bool WhitelistPrefix(InventoryData __instance, ItemData __0)
    {
        try
        {
            if (__instance == null) return true;
            if (!IsMarked(__instance)) return true;
            if (__0 == null) { LogReject(-2); return false; }
            if (IsAllowedFuel(__0)) return true;
            LogReject(FuelItemId(__0));
            return false; // 拒绝放入（物品回到原处）
        }
        catch (Exception e) { Plugin.L.LogWarning($"[TS] WhitelistPrefix 异常: {e.Message.Split('\n')[0]}"); return true; }
    }

    /// <summary>v0.8.1 保留：get_fuelInventoryData postfix——UI 侧容器标记（双来源之一，不接管准入、不清 itemFeatureLimit）。</summary>
    public static void GetFuelInventoryPostfix(TerrainObject_Production_StirlingGenerator __instance, ref InventoryData __result)
    {
        try
        {
            if (__result == null || !IsBioGen(__instance)) return;
            Mark(__result, false);
        }
        catch (Exception e) { Plugin.L.LogWarning($"[TS] GetFuelInventoryPostfix 异常: {e.Message.Split('\n')[0]}"); }
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
        catch (Exception e) { Plugin.L.LogWarning($"[TS] BioGen OnStop 异常: {e.Message.Split('\n')[0]}"); }
    }

    // ───────────────── 内部工具 ─────────────────

    private static void Mark(InventoryData fd, bool burnContainer)
    {
        long ptr = 0;
        try { ptr = (long)fd.Pointer; } catch { ptr = fd.GetHashCode(); }
        if (_marked.Add(ptr))
        {
            try { Reflect.Set(fd, "inventoryTitleName", GameLocale.T("生物燃料仓", "Bio Fuel Hopper")); } catch { }
            Plugin.L.LogInfo($"[TS] BioGen 燃料仓已标记 ({(burnContainer ? "烧录容器" : "UI 容器")}) size=({fd.inventorySizeX}x{fd.inventorySizeY}) _marked={_marked.Count}");
        }
    }

    private static bool IsMarked(InventoryData fd)
    {
        try { return _marked.Contains((long)fd.Pointer); } catch { return false; }
    }

    /// <summary>严格白名单（v0.8.10 终版）：Food 类物品全部可入（含腐肉 205、含未过期食品）+ 炭 6（副产品回仓）；木头/金属等非食品拒。
    /// 过期判定已按用户要求移除——「只要是有新鲜度的食物类都可以放入」。
    /// 注意：ItemData 无 itemAttr 成员（那是 BasicItem 的 protected 字段）——attr 一律经 ItemManager.GetItemAttrById(itemId) 解析（游戏同款路径）。
    /// 吞物品教训：D 环（TryAddItem/AddItem prefix）执行时物品可能已从源容器移除，拒绝=物品悬空丢失；
    /// 因此 D 环只应拒绝 C 环已拦下的非 Food（木头/金属在 C 环 PFL 即被 UI 层挡回，D 环极少触发）。</summary>
    private static bool IsAllowedFuel(ItemData it)
    {
        try
        {
            int id = it.itemId;
            if (id == 205 || id == 6) return true;           // 腐肉 / 炭（副产品回仓）
            if (id <= 0) return false;                       // 无法识别的物品一律拒
            var attr = ItemManager.instance?.GetItemAttrById(id);
            if (attr == null) return false;
            return attr.itemType.ToString().Contains("Food"); // 所有食品类放行（含未过期）
        }
        catch { return false; }
    }

    private static int FuelItemId(ItemData it)
    {
        try { return it.itemId; } catch { return -1; }
    }

    private static void LogReject(int id)
    {
        if (Time.unscaledTime - _lastRejectLog < 3f) return;
        _lastRejectLog = Time.unscaledTime;
        Plugin.L.LogInfo($"[TS] BioGen 拒绝燃料: id={id}");
    }

    /// <summary>ProductionData → 900103 判定：terrainObjectAttr 引用/ID 双保险。</summary>
    private static bool IsBioGenProduction(ProductionData pd)
    {
        try
        {
            var attr = pd.terrainObjectAttr;
            if (attr == null) return false;
            if (RegistrationStore.Attrs.TryGetValue(900103, out var ours) && ReferenceEquals(attr, ours)) return true;
            return AttrId(attr) == 900103;
        }
        catch { return false; }
    }

    private static bool IsBioGen(TerrainObject_Production_StirlingGenerator g)
    {
        try
        {
            var to = FindTerrainObject(g.transform);
            if (to == null) return false;
            object attr = null;
            try { attr = Reflect.Get(to, "attr"); } catch { }
            if (attr == null) return false;
            if (RegistrationStore.Attrs.TryGetValue(900103, out var our) && ReferenceEquals(attr, our)) return true;
            return AttrId(attr) == 900103;
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