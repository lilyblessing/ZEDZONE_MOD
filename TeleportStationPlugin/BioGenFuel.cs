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

    // ── v0.9.104 F1b：燃料集限定燃烧·消费侧否决（与 F1a 成对，缺一不可）──
    // F1a 给燃料集原生 Combustible 后，原版斯特林（120，BioGen 克隆源）的燃料扫描也会命中燃料集，必须否决。
    // 钩点选择实证（dump.cs 全表仅 2 处 Combustible 命中）：
    //   - dump.cs:53597 ItemFeatureType.Combustible = 1（纯标志位定义）；
    //   - dump.cs:79293 ProductionManager.FindLastCombustibleIndex(InventoryData)（燃料扫描，public static，
    //     仅收燃料容器、无 burner 参数——任务书“fuel==205 且 burner.attr!=900103 返回 false”形状无干净钩点，
    //     burner 身份不可得；判定体 Combustible 读取循环内联在 UpdateStirlingGenerator 体内，
    //     dump 仅签名（dump.cs:79361，VA 0x1809ACA60），无独立方法）。
    // 否决方案：hook 仍有 burner 身份的调用方——ProductionManager.UpdateStirlingGenerator /
    // UpdateStoneFurnace（ProductionData.generatorData/productionData 经 terrainObjectAttr 直接判定，
    // 复用 IsBioGenProduction，比 FindTerrainObject 爬链更直接）：非 BioGen burner 调用窗内临时摘除燃料集
    // 全部 attr 的 Combustible（游戏单线程串行，原子），postfix 恢复。原生扫描自然跳过燃料集、木头照烧；
    // BioGen 窗不摘除。prefix 恒返 true（不跳过原生；除燃料集标志位外不改变任何状态）；与既有
    // StirlingUpdatePrefix/Postfix 同挂一方法但条件互斥（BioGen vs 非 BioGen），顺序无关。
    // 燃料集定义：唯一 source of truth = IsBioGenFuel(itemId)（见下；F1a/F1b/D 环同一函数，grep 定义恰 1 处）。
    // 炭 6 排除在外（灰烬原生语义：B 环伪造已对其放行原版 attr，此处不摘除）。
    private static readonly System.Collections.Generic.List<ItemAttr> _fuelAttrs = new(); // 否决集缓存（懒建一次）
    private static bool _fuelResolved;
    private static readonly System.Collections.Generic.List<ItemAttr> _stripped = new(); // 本窗实际摘除项（postfix 只恢复这些；串行调用，无嵌套）
    private static bool _fuelAttrWarned;

    private static void EnsureFuelSet()
    {
        try
        {
            if (_fuelResolved) return;
            _fuelResolved = true; // 先锁后建（资产未就绪则空集，本局后续tick不再扫描；F1a场景postfix会补，见注释）
            ItemAttr[] all = null;
            try { all = UnityEngine.Resources.FindObjectsOfTypeAll<ItemAttr>(); } catch { }
            if (all == null) return;
            for (int i = 0; i < all.Length; i++)
            {
                var a = all[i];
                if (a == null) continue;
                int fid = -1;
                try { fid = a.itemId; } catch { continue; }
                bool hit = false;
                try { hit = IsBioGenFuel(fid); } catch { continue; }
                if (!hit) continue;
                bool dup = false;
                for (int j = 0; j < _fuelAttrs.Count; j++) try { if (ReferenceEquals(_fuelAttrs[j], a)) { dup = true; break; } } catch { }
                if (!dup) try { _fuelAttrs.Add(a); } catch { }
            }
            if (_fuelAttrs.Count == 0 && !_fuelAttrWarned)
            { _fuelAttrWarned = true; Plugin.L.LogWarning("[TS] F1b: 燃料集未就绪（本次跳过否决）"); }
        }
        catch { }
    }

    public static bool MeatVetoPrefix(ProductionData generatorData)
    {
        try
        {
            if (generatorData == null) return true;
            if (IsBioGenProduction(generatorData)) return true; // BioGen 不摘除（燃料集照烧）
            EnsureFuelSet();
            try { _stripped.Clear(); } catch { }
            for (int i = 0; i < _fuelAttrs.Count; i++)
            {
                var m = _fuelAttrs[i];
                if (m == null) continue;
                try
                {
                    var feats = m.itemFeatures;
                    if (feats != null && feats.Contains(ItemFeatureType.Combustible)) { feats.Remove(ItemFeatureType.Combustible); try { _stripped.Add(m); } catch { } }
                }
                catch { }
            }
            return true;
        }
        catch { return true; }
    }

    public static void MeatVetoPostfix()
    {
        try
        {
            if (_stripped.Count == 0) return;
            for (int i = 0; i < _stripped.Count; i++)
            {
                var m = _stripped[i];
                if (m == null) continue;
                try
                {
                    var feats = m.itemFeatures;
                    if (feats != null && !feats.Contains(ItemFeatureType.Combustible)) feats.Add(ItemFeatureType.Combustible);
                }
                catch { }
            }
            try { _stripped.Clear(); } catch { }
        }
        catch { }
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

    /// <summary>v0.8.9 D：TryAddItem prefix——item 级严格白名单（腐肉205 / 炭6 / 过期食品）。</summary>
    public static bool WhitelistPrefix(InventoryData __instance, ItemData __0, ref bool __result)
    {
        try
        {
            if (__instance == null) return true;
            if (!IsMarked(__instance)) return true;
            if (__0 == null) { LogReject(-2); __result = false; return false; }
            if (IsAllowedFuel(__0)) return true;
            LogReject(FuelItemId(__0));
            __result = false;
            return false; // 拒绝放入（物品回到原处）
        }
        catch (Exception e) { Plugin.L.LogWarning($"[TS] WhitelistPrefix 异常: {e.Message.Split('\n')[0]}"); return true; }
    }

    /// <summary>v0.8.9 D：AddItem prefix——item 级严格白名单（腐肉205 / 炭6 / 过期食品，int 返回版）。</summary>
    public static bool WhitelistPrefixInt(InventoryData __instance, ItemData __0, ref int __result)
    {
        try
        {
            if (__instance == null) return true;
            if (!IsMarked(__instance)) return true;
            if (__0 == null) { LogReject(-2); __result = 0; return false; }
            if (IsAllowedFuel(__0)) return true;
            LogReject(FuelItemId(__0));
            __result = 0;
            return false; // 拒绝放入（物品回到原处）
        }
        catch (Exception e) { Plugin.L.LogWarning($"[TS] WhitelistPrefixInt 异常: {e.Message.Split('\n')[0]}"); return true; }
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

    /// <summary>统一实例键（GetInstanceID→Pointer→GetHashCode 三段式，TeleportStationUid.GetInstanceKey 范式）：
    /// InventoryData 系纯数据类（dump.cs TypeDefIndex: 1108，无 Unity 基类），未必有 GetInstanceID，故首段经反射探测
    /// （TeleportConsoleInteractFix 同款 GetMethod("GetInstanceID") 路径，一次性缓存），缺失则回退 Pointer→GetHashCode。
    /// Mark/IsMarked 必须同走此函数（旧不对称：Mark 回退 GetHashCode 而 IsMarked 只读 Pointer，回退写入的键永远查不到）。</summary>
    private static System.Reflection.MethodInfo _gidMethod; // InventoryData.GetInstanceID（若有；一次性探测缓存）
    private static bool _gidProbed;

    private static long GetInstanceKey(InventoryData fd)
    {
        if (fd == null) return 0;
        try
        {
            if (!_gidProbed)
            {
                _gidProbed = true;
                try { _gidMethod = fd.GetType().GetMethod("GetInstanceID"); } catch { _gidMethod = null; }
            }
            if (_gidMethod != null) return Convert.ToInt64(_gidMethod.Invoke(fd, null));
        }
        catch { }
        try { return (long)fd.Pointer; } catch { }
        try { return fd.GetHashCode(); } catch { return 0; }
    }

    /// <summary>心跳契约：换档/读档时清空标记（跨档结转消除）。签名必须精确，心跳调用方直接引用。</summary>
    internal static void ResetForIdentity() { try { _marked.Clear(); } catch { } }

    /// <summary>心跳契约：活体求交修枝。留空原因——本文件内无可枚举的 InventoryData 活体全集
    /// （_marked 来源 inventoryData1 + fuelInventoryData 双来源；本文件刻意不走 ActiveObjects 遍历，见文件头注释），
    /// 全量清理交 ResetForIdentity 覆盖。</summary>
    internal static void PruneCaches() { try { /* 本文件内无可枚举的 InventoryData 活体全集，修枝无全集可求交；全量清理交 ResetForIdentity 覆盖 */ } catch { } }

    private static void Mark(InventoryData fd, bool burnContainer)
    {
        long key = 0;
        try { key = GetInstanceKey(fd); } catch { return; }
        if (_marked.Add(key))
        {
            try { Reflect.Set(fd, "inventoryTitleName", GameLocale.T("生物燃料仓", "Bio Fuel Hopper")); } catch { }
            Plugin.L.LogInfo($"[TS] BioGen 燃料仓已标记 ({(burnContainer ? "烧录容器" : "UI 容器")}) size=({fd.inventorySizeX}x{fd.inventorySizeY}) _marked={_marked.Count}");
        }
    }

    private static bool IsMarked(InventoryData fd)
    {
        try { return _marked.Contains(GetInstanceKey(fd)); } catch { return false; }
    }

    /// <summary>v0.9.104 燃料集唯一定义（E1，F1a/F1b/D 环共用同一函数，grep 定义恰 1 处）：
    /// 所有带新鲜度的食物 = 腐肉 205（回归项）+ 一切 itemType 含 Food 的物品；炭 6 除外（灰烬走原生语义）。
    /// 运行时可算：id 快路 + ItemManager 现场解析 attr 读 itemType，不硬编码零散 id。
    /// 判定照抄既有 C/D 环逻辑，行为与 v0.8.10 白名单一致，不两份逻辑。</summary>
    internal static bool IsBioGenFuel(int itemId)
    {
        try
        {
            if (itemId == 205) return true;   // 回归：腐肉
            if (itemId == 6) return false;    // 炭：灰烬，原生语义，不计入燃料集
            if (itemId <= 0) return false;    // 无法识别一律拒（含木头 id 0）
            ItemAttr attr = null;
            try { attr = ItemManager.instance?.GetItemAttrById(itemId); } catch { }
            if (attr == null) return false;
            try { return attr.itemType.ToString().Contains("Food"); } catch { return false; }
        }
        catch { return false; }
    }

    /// <summary>attr 级变体（含灰烬开关）：D 环白名单（IsAllowedFuel）传 includeAsh=true（炭必须回仓）；
    /// F1a 补键 / F1b 否决一律走 IsBioGenFuel(id)（炭除外）。非灰烬路径直接委托 IsBioGenFuel，不两份逻辑。</summary>
    internal static bool IsBioFuelAttr(ItemAttr attr, bool includeAsh)
    {
        try
        {
            if (attr == null) return false;
            int id = -1; try { id = attr.itemId; } catch { }
            if (id == 6) return includeAsh;          // 炭：灰烬，原生语义
            return IsBioGenFuel(id);
        }
        catch { return false; }
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
            return IsBioFuelAttr(attr, true);                // Food 判定走共用函数（含未过期）
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