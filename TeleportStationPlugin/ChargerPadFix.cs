using System;
using UnityEngine;

namespace TeleportStationPlugin;

/// <summary>
/// v0.9.4 P3 二期：充电台克隆盘（900102 新形态）适配。
/// 克隆源切换（斯特林120→充电台126）后：
///   1. 盘天然是电力消费端 → 游戏真实电线路由（电线杆接线）原生可用，充电走原版 UpdateBatteryCharger；
///   2. 本类只做两件事：
///      A. 容器微调（一次性）：productionData.inventoryData1（原版槽位扫描容器）→ 4×4 + 标题「电池仓」+ totalBatterySoltNumber=4；
///      B. ×4 倍率 hook：Supply 源含生物能（900103）时，UpdateBatteryCharger 前后把 powerInputSufficientFloat ×4/恢复
///         （原版公式 totalWh = sufficient × electricWattage × addedTime × 24 → 放大 sufficient 即等效倍率）。
///   旧档斯特林组件盘继续由 BatteryChargeFix（虚拟供电充电）照顾。
/// </summary>
public static class ChargerPadFix
{
    private const int PadId = 900102;
    private const int BioGenId = 900103;
    private static readonly System.Collections.Generic.HashSet<long> _initKeys = new();
    private static float _lastScan = -1f;
    private static bool _boosted; // ×4 窗口（prefix 置位 / postfix 恢复）
    private static bool _warnedTypeMiss; // 判定诊断（一次性）
    private static bool _warnedHit;      // ×4 判定诊断（一次性）

    /// <summary>由 RegistrationProbe.Update 每帧调用（内部 0.5s 节流）。</summary>
    public static void Tick()
    {
        try
        {
            float now = Time.unscaledTime;
            if (now - _lastScan < 0.5f) return;
            _lastScan = now;
            var list = TerrainObject_Production.ActiveObjects_Production;
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                var g = list[i];
                if (g == null || !IsChargerPad(g)) continue;
                try { EnsureContainer(g); }
                catch (Exception e) { Plugin.L.LogWarning($"[TS] 充电台盘初始化异常: {e.Message.Split('\n')[0]}"); }
            }
        }
        catch { }
    }

    /// <summary>充电台克隆盘判定：组件类型含 BatteryCharger + attr.id == 900102。类型名不匹配时一次性诊断（排查 v0.9.4 初始化缺失）。</summary>
    private static bool IsChargerPad(TerrainObject_Production g)
    {
        try
        {
            bool typeOk = g.GetType().Name.IndexOf("BatteryCharger", StringComparison.Ordinal) >= 0;
            var to = FindTerrainObject(g.transform);
            if (to == null) return false;
            object attr = null;
            try { attr = Reflect.Get(to, "attr"); } catch { }
            if (attr == null) { if (!_warnedTypeMiss) { _warnedTypeMiss = true; Plugin.L.LogWarning($"[TS] ChargerPad 判定诊断: attr=null type='{g.GetType().Name}'"); } return false; }
            bool isPad = false;
            try { isPad = (RegistrationStore.Attrs.TryGetValue(PadId, out var our) && ReferenceEquals(attr, our)) || AttrId(attr) == PadId; } catch { }
            if (isPad && !typeOk && !_warnedTypeMiss)
            {
                _warnedTypeMiss = true;
                Plugin.L.LogWarning($"[TS] ChargerPad 判定诊断: 是900102但组件类型名不含 BatteryCharger: '{g.GetType().Name}' attrId={AttrId(attr)}");
            }
            return isPad && typeOk;
        }
        catch { return false; }
    }

    /// <summary>一次性容器微调：4×4 + 标题 + 槽数 4（原版充电台默认 2 槽 2×1 容器）。</summary>
    private static void EnsureContainer(TerrainObject_Production g)
    {
        long key = 0;
        try { key = (long)g.Pointer; } catch { key = g.GetHashCode(); }
        if (_initKeys.Contains(key)) return;
        _initKeys.Add(key);
        object pd = null;
        try
        {
            var tod = Reflect.Get(g, "objectData");
            if (tod != null) pd = Reflect.Get(tod, "productionData");
        }
        catch { }
        if (pd == null) return;
        var inv = Reflect.Get(pd, "inventoryData1") as InventoryData;
        if (inv == null) return;
        try { Reflect.Set(inv, "inventoryTitleName", GameLocale.T("电池仓", "Battery Cell")); } catch { }
        try { Reflect.Set(inv, "inventorySize", new Vector2Int(4, 4)); } catch { }
        try { Reflect.Set(inv, "inventorySizeX", 4); } catch { }
        try { Reflect.Set(inv, "inventorySizeY", 4); } catch { }
        try { Reflect.Set(g, "totalBatterySoltNumber", 4); } catch { }
        Plugin.L.LogInfo($"[TS] 充电台盘初始化: 4×4 槽数=4 size=({inv.inventorySizeX}x{inv.inventorySizeY})");
    }

    /// <summary>×4 倍率 prefix：pd 是 900102 且供电含生物能 → sufficient ×4（postfix 恢复）。</summary>
    public static bool ChargerUpdatePrefix(ProductionData productionData, float addedTime)
    {
        try
        {
            if (productionData == null) return true;
            if (!IsPadPd(productionData))
            {
                if (!_warnedHit) { _warnedHit = true; Plugin.L.LogWarning("[TS] ×4 诊断: UpdateBatteryCharger 触发但 IsPadPd=false（attr 判定未命中）"); }
                return true;
            }
            if (!IsBioGenSupplied(productionData))
            {
                if (!_warnedHit) { _warnedHit = true; Plugin.L.LogWarning("[TS] ×4 诊断: IsPadPd=true 但供电判定无生物能（联网列表/距离均未命中）"); }
                return true;
            }
            try
            {
                productionData.powerInputSufficientFloat = productionData.powerInputSufficientFloat * 4f;
                _boosted = true;
                if (!_warnedHit) { _warnedHit = true; Plugin.L.LogInfo("[TS] ×4 倍率生效: sufficient×4"); }
            }
            catch { }
        }
        catch { }
        return true;
    }

    public static void ChargerUpdatePostfix(ProductionData productionData)
    {
        if (!_boosted) return;
        _boosted = false;
        try { productionData.powerInputSufficientFloat = productionData.powerInputSufficientFloat / 4f; } catch { }
    }

    private static bool IsPadPd(ProductionData pd)
    {
        try
        {
            var attr = pd.terrainObjectAttr;
            if (attr == null) return false;
            if (RegistrationStore.Attrs.TryGetValue(PadId, out var our) && ReferenceEquals(attr, our)) return true;
            return AttrId(attr) == PadId;
        }
        catch { return false; }
    }

    /// <summary>供电含生物能（900103）：真实路由联网列表优先，兜底 20m 距离检测。</summary>
    private static bool IsBioGenSupplied(ProductionData pd)
    {
        try
        {
            var genList = Reflect.Get(pd, "connectedElectricGeneratorList") as Il2CppSystem.Collections.Generic.List<ProductionData>;
            if (genList != null)
            {
                for (int i = 0; i < genList.Count; i++)
                {
                    var gen = genList[i];
                    if (gen == null) continue;
                    var attr = gen.terrainObjectAttr;
                    if (attr == null) continue;
                    if (RegistrationStore.Attrs.TryGetValue(BioGenId, out var ours) && ReferenceEquals(attr, ours)) return true;
                    if (AttrId(attr) == BioGenId) return true;
                }
            }
            // 兜底：距离检测（真实路由尚未建立时）
            var to = pd.terrainObjectTemp;
            if (to == null) return false;
            var pos = to.transform.position;
            var list = TerrainObject_Production_StirlingGenerator.ActiveObjects_StirlingGenerator;
            if (list == null) return false;
            for (int i = 0; i < list.Count; i++)
            {
                var g = list[i];
                if (g == null) continue;
                var to2 = FindTerrainObject(g.transform);
                if (to2 == null) continue;
                object attr = null;
                try { attr = Reflect.Get(to2, "attr"); } catch { }
                if (attr == null) continue;
                bool isBio = false;
                try { isBio = (RegistrationStore.Attrs.TryGetValue(BioGenId, out var ours) && ReferenceEquals(attr, ours)) || AttrId(attr) == BioGenId; } catch { }
                if (!isBio) continue;
                var dp = g.transform.position - pos;
                if (dp.x * dp.x + dp.y * dp.y <= 20f * 20f) return true;
            }
            return false;
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
                if (n.Contains("TerrainObject") || n.Contains("BatteryCharger"))
                    return c;
            }
            t = t.parent;
        }
        return null;
    }
}