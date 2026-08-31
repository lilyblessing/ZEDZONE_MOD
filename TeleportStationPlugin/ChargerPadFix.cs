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
    private static readonly System.Collections.Generic.HashSet<long> _pdFixed = new(); // PD 六表已补的实例（去重）
    private static float _lastGridLog;

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
                if (g == null) continue;
                // v0.9.7：PD 六表防御扩展到全部克隆建筑（900101/102/103）——停机→电网重扫对任何克隆建筑建边都可能 Add null 表
                int aid = GetClonedAttrId(g);
                if (aid == 900101 || aid == 900102 || aid == 900103)
                    EnsurePdTablesOnce(g);
                if (aid != 900102) continue;
                if (!IsChargerPad(g)) continue;
                try { EnsureContainer(g); }
                catch (Exception e) { Plugin.L.LogWarning($"[TS] 充电台盘初始化异常: {e.Message.Split('\n')[0]}"); }
            }
        }
        catch { }
    }

    private static void EnsurePdTablesOnce(TerrainObject_Production g)
    {
        object pd = null;
        try
        {
            var tod = Reflect.Get(g, "objectData");
            if (tod != null) pd = Reflect.Get(tod, "productionData");
        }
        catch { }
        if (pd == null) return;
        long k = 0;
        try { k = (long)pd.GetHashCode(); } catch { k = pd.GetType().GetHashCode(); }
        if (_pdFixed.Contains(k)) return;
        EnsurePdTables(pd);
        _pdFixed.Add(k);
        if (_pdTablesFixed) Plugin.L.LogInfo($"[TS] PD 六表已重建（克隆建筑 {GetClonedAttrId(g)}）");
    }

    /// <summary>克隆建筑 attr id（900101/102/103 引用优先，含未知 id 兜底返回）。</summary>
    private static int GetClonedAttrId(TerrainObject_Production g)
    {
        try
        {
            var to = FindTerrainObject(g.transform);
            if (to == null) return -1;
            object attr = null;
            try { attr = Reflect.Get(to, "attr"); } catch { }
            if (attr == null) return -1;
            if (RegistrationStore.Attrs.TryGetValue(900101, out var a1) && ReferenceEquals(attr, a1)) return 900101;
            if (RegistrationStore.Attrs.TryGetValue(900102, out var a2) && ReferenceEquals(attr, a2)) return 900102;
            if (RegistrationStore.Attrs.TryGetValue(900103, out var a3) && ReferenceEquals(attr, a3)) return 900103;
            return AttrId(attr);
        }
        catch { return -1; }
    }

    // ── v0.9.7 电网重扫轨迹探针（定位"停机→线断→不重连"）──

    /// <summary>ProductionManager.MarkElectricGridDirty postfix：脏标（重扫排队）事件。</summary>
    public static void GridDirtyPostfix()
    {
        LogThrottled("[TS] 电网脏标（重扫排队）");
    }

    /// <summary>ProductionManager.ConsumeElectricGridDirtyFlag postfix：重扫完成 → 采样三建筑 PD 连接表。</summary>
    public static void GridConsumePostfix()
    {
        try
        {
            string sb = "[TS] 电网重扫完成，克隆建筑连接表:";
            var list = TerrainObject_Production.ActiveObjects_Production;
            if (list == null) return;
            bool found = false;
            for (int i = 0; i < list.Count; i++)
            {
                var g = list[i];
                if (g == null) continue;
                int aid = GetClonedAttrId(g);
                if (aid != 900101 && aid != 900102 && aid != 900103) continue;
                found = true;
                // v0.9.8：附 attr 耗电标志实证（重扫过滤依据）
                try
                {
                    var to = FindTerrainObject(g.transform);
                    object attr = null;
                    try { if (to != null) attr = Reflect.Get(to, "attr"); } catch { }
                    if (attr != null) sb += $" [id{aid}电耗={Reflect.Get(attr, "electricConsuming")}]";
                    else sb += $" [id{aid}无attr]";
                }
                catch { }
                object pd = null;
                try { var tod = Reflect.Get(g, "objectData"); if (tod != null) pd = Reflect.Get(tod, "productionData"); } catch { }
                if (pd == null) { sb += $" [{aid}:PD=null]"; continue; }
                sb += $" [{aid}:" + CountOf(pd, "inputProductionObjectList") + "/" + CountOf(pd, "outputProductionObjectList") + "/"
                    + CountOf(pd, "connectedProductionObjectList") + "/" + CountOf(pd, "inputProductionDataList") + "/"
                    + CountOf(pd, "outputProductionDataList") + "/" + CountOf(pd, "connectedProductionDataList") + "]";
            }
            if (found) LogThrottled(sb);
        }
        catch { }
    }

    private static int CountOf(object pd, string field)
    {
        try
        {
            var v = Reflect.Get(pd, field);
            if (v == null) return -1; // null 表（将 NRE！）
            var p = v.GetType().GetProperty("Count");
            if (p != null) return Convert.ToInt32(p.GetValue(v));
            return -2;
        }
        catch { return -3; }
    }

    private static void LogThrottled(string msg)
    {
        if (Time.unscaledTime - _lastGridLog < 2f) return;
        _lastGridLog = Time.unscaledTime;
        Plugin.L.LogInfo(msg);
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
        EnsurePdTables(pd); // v0.9.6：NRE 防御——电线杆重扫对盘 PD 连接表 Add 时若表 null 即炸（ElectricPole.cs:106）
        var inv = Reflect.Get(pd, "inventoryData1") as InventoryData;
        if (inv == null) return;
        try { Reflect.Set(inv, "inventoryTitleName", GameLocale.T("电池仓", "Battery Cell")); } catch { }
        try { Reflect.Set(inv, "inventorySize", new Vector2Int(4, 4)); } catch { }
        try { Reflect.Set(inv, "inventorySizeX", 4); } catch { }
        try { Reflect.Set(inv, "inventorySizeY", 4); } catch { }
        try { Reflect.Set(g, "totalBatterySoltNumber", 4); } catch { }
        Plugin.L.LogInfo($"[TS] 充电台盘初始化: 4×4 槽数=4 size=({inv.inventorySizeX}x{inv.inventorySizeY}) PD表={(_pdTablesFixed ? "已重建" : "完整")}");
    }

    private static bool _pdTablesFixed; // 一次性日志用

    /// <summary>ProductionData 六连接表完整性保障（反编译 ElectricPole.RefreshElectricConnection 0x1809BD350：
    /// 对 input/output/connected 三组列表做 List.Add 无守卫，任一 null 即 NRE「已隔离」→ 存档重建电网时建筑加载异常）。
    /// 原版 ctor new 六表；克隆/池化路径可能缺——缺则补 Il2Cpp List（字段名以 dump.cs 为准）。</summary>
    private static void EnsurePdTables(object pd)
    {
        string[] strTables = { "inputProductionObjectList", "outputProductionObjectList", "connectedProductionObjectList" };
        string[] pdTables = { "inputProductionDataList", "outputProductionDataList", "connectedProductionDataList" };
        foreach (var f in strTables)
        {
            try
            {
                if (Reflect.Get(pd, f) == null)
                {
                    Reflect.Set(pd, f, new Il2CppSystem.Collections.Generic.List<string>());
                    _pdTablesFixed = true;
                }
            }
            catch { }
        }
        foreach (var f in pdTables)
        {
            try
            {
                if (Reflect.Get(pd, f) == null)
                {
                    Reflect.Set(pd, f, new Il2CppSystem.Collections.Generic.List<ProductionData>());
                    _pdTablesFixed = true;
                }
            }
            catch { }
        }
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