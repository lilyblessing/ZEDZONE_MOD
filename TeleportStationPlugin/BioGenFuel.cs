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
///   C. PassesFeatureLimit prefix：生物容器 attr 级粗筛（205/炭/木头/Food 放行，金属拒）
///   D. TryAddItem/AddItem prefix：item 级白名单（Food 类全放行 / 腐肉205 / 木头0 / 炭6 豁免；金属拒）——v0.8.10 终版 + 木材类
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

    // ── v0.9.105 方案③：读档窗期标志 + 加法（F1b 否决已退役删除，见 git 历史）──
    // 顺序铁律：加在原生门前——场景加载时 ChargerPadFix.EnsureBioFuelCombustible 先快照后补键，
    // 早于原生启动判定，读档原生放行；摘在沉降后——读档沉降+5s 的 BioGenSaveHealOnce 末尾
    // RemoveBioFuelCombustible() 精确摘除（v0.9.105 A2：自愈先起机后摘，首行摘除已废止）；摘除后运行时认食物全靠 B 环加法窗伪造
    // （窗期标志摘除后的运行时认食物路径，见 GetAttrByIdPrefix）。
    // 精确恢复：补键时 RecordBioFuelAdded 记 _addedFuelIds（仅实际补过 Combustible 的 id，
    // 原生已带标志的项不入表）；摘除只摘表内项，快照集（NativeCombustibleIds）内原生 id 永不碰，无全量摘除。
    private static readonly System.Collections.Generic.HashSet<int> _addedFuelIds = new(); // 本次补过标志的燃料 id（摘完清空）

    internal static void RecordBioFuelAdded(int itemId)
    {
        try { _addedFuelIds.Add(itemId); } catch { }
    }

    /// <summary>窗期标志摘除（沉降后执行，调用点见 ChargerPadFix.BioGenSaveHealOnce 末尾及各提前返回处；A 自愈逻辑不动）：
    /// 只摘 _addedFuelIds 表内项（我们加的），快照集内原生 id 与炭 6 永不碰；摘完打 info 日志。</summary>
    internal static void RemoveBioFuelCombustible()
    {
        try
        {
            if (_addedFuelIds.Count == 0) { try { Plugin.L.LogInfo("[TS][Fuel] 窗期标志已摘除 n=0"); } catch { } return; }
            int n = 0;
            ItemManager mgr = null;
            try { mgr = ItemManager.instance; } catch { }
            System.Collections.Generic.List<int> ids = null;
            try { ids = new System.Collections.Generic.List<int>(_addedFuelIds); } catch { }
            try { _addedFuelIds.Clear(); } catch { } // 先清后摘（单线程；异常也不重摘）
            if (ids != null && mgr != null)
            {
                for (int i = 0; i < ids.Count; i++)
                {
                    int fid = ids[i];
                    try { if (NativeCombustibleIds.Contains(fid)) continue; } catch { } // 原生 id 永不摘
                    if (fid == 6) continue; // 炭：灰烬，原生语义
                    ItemAttr a = null;
                    try { a = mgr.GetItemAttrById(fid); } catch { continue; }
                    if (a == null) continue;
                    try
                    {
                        var feats = a.itemFeatures;
                        if (feats != null && feats.Contains(ItemFeatureType.Combustible)) { feats.Remove(ItemFeatureType.Combustible); n++; }
                    }
                    catch { }
                }
            }
            try { Plugin.L.LogInfo($"[TS][Fuel] 窗期标志已摘除 n={n}"); } catch { }
        }
        catch { }
    }


    /// <summary>v0.8.9 B → v0.9.105 加法窗：扫描窗内 GetItemAttrById 对燃料集（IsBioGenFuel 全集）返回木头 attr（含 Combustible）。
    /// 窗期标志摘除后的运行时认食物路径：仅 BioGen burner 窗内（沿既有 _inBioScan 判定，不新增 hook 点），非燃料集直通。
    /// 炭(6) 放行原版 attr（灰烬注入需要真实炭 attr）；窗口外零开销直通。</summary>
    public static bool GetAttrByIdPrefix(ItemManager __instance, int itemId, ref ItemAttr __result)
    {
        try
        {
            if (!_inBioScan) return true;
            if (itemId == 6) return true;
            bool isFuel = false;
            try
            {
                bool save = _inBioScan; _inBioScan = false; // 防递归：IsBioGenFuel 内查 attr 走同方法，窗外直通取真值
                try { isFuel = IsBioGenFuel(itemId); } finally { _inBioScan = save; }
            }
            catch { return true; }
            if (!isFuel) return true; // 非燃料集直通（不伪造）
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

    /// <summary>v0.8.9 C：PassesFeatureLimit prefix——生物燃料仓 attr 级粗筛（205/炭/木头/Food 放行；金属等拒）。
    /// attr 直传（interop 直接访问，不走反射，杜绝 id=0 误读）。expiry 严格判定在 D 环。</summary>
    public static bool PassesFeatureLimitPrefix(InventoryData __instance, ItemAttr attr, ref bool __result)
    {
        try
        {
            if (__instance == null || attr == null) return true;
            if (!IsMarked(__instance)) return true; // 非生物燃料仓走原版
            int id = -1; try { id = attr.itemId; } catch { }
            bool isFood = false; try { isFood = attr.itemType.ToString().Contains("Food"); } catch { }
            if (id == 205 || id == 6 || IsBioGenFuel(id) || isFood) // 木材类走快照集（IsBioGenFuel 内），零硬编码
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
            string sAid = "?"; // B=attr id（用户BioGen=900103，原版斯特林=120）
            string sPid = "?"; // A=productionObjectId后8位（取不到则回退pos）
            try
            {
                Component sto = null;
                try { sto = __instance != null ? FindTerrainObject(__instance.transform) : null; } catch { }
                object sattr = null;
                try { sattr = sto != null ? Reflect.Get(sto, "attr") : null; } catch { }
                if (sattr != null) { try { sAid = AttrId(sattr).ToString(); } catch { } }
                string full = null;
                try
                {
                    var sod = sto != null ? Reflect.Get(sto, "objectData") : null;
                    var spd = sod != null ? Reflect.Get(sod, "productionData") as ProductionData : null;
                    if (spd != null) full = spd.productionObjectId;
                }
                catch { }
                if (!string.IsNullOrEmpty(full)) sPid = full.Length > 8 ? full.Substring(full.Length - 8) : full;
                else { try { var spp = __instance.transform.position; sPid = $"({spp.x:F1},{spp.y:F1})"; } catch { } }
            }
            catch { }
            Plugin.L.LogInfo($"[TS] BioGen 启动（开始观察） id={sPid} attr={sAid}");
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

    /// <summary>v0.9.104 燃料集唯一定义（E1，F1a/D 环共用同一函数，grep 定义恰 1 处）：
    /// 快照集（原生自带 Combustible＝原版斯特林燃料类，木头/煤等天然在内，零硬编码）∪ 食物类（一切 itemType 含 Food，不过期判定）；
    /// 炭 6 除外（灰烬走原生语义）。W1 手枚举已退役（用户拍板）：dump 证据仅作旁证（0=木材 dump.cs:31759＋75951；6=炭 dump.cs:79240；
    /// 煤/木炭无独立 id），活体 source of truth 以快照集为准。
    /// 顺序铁律：快照必须在 F1a 补键前完成（补键后 Food 也带 Combustible，重拍会污染）；只拍一次（_snapDone 锁存，资产未就绪则不限存等下次）。</summary>
    internal static readonly System.Collections.Generic.HashSet<int> NativeCombustibleIds = new(); // 原生可燃快照集（static 只读引用；内容只增不改）
    private static bool _snapDone;

    /// <summary>快照原生可燃集：F1a 补键前调用（EnsureBioFuelCombustible 首行；补键前拍到真相，只拍一次）。</summary>
    internal static void SnapshotNativeCombustible()
    {
        try
        {
            if (_snapDone) return;
            ItemAttr[] all = null;
            try { all = UnityEngine.Resources.FindObjectsOfTypeAll<ItemAttr>(); } catch { }
            if (all == null || all.Length == 0) return; // 资产未就绪：不限存，下次再拍（仍在补键前）
            _snapDone = true;
            for (int i = 0; i < all.Length; i++)
            {
                var a = all[i];
                if (a == null) continue;
                bool has = false;
                try { var f = a.itemFeatures; has = (f != null && f.Contains(ItemFeatureType.Combustible)); } catch { continue; }
                if (!has) continue;
                try { NativeCombustibleIds.Add(a.itemId); } catch { }
            }
            try { Plugin.L.LogInfo($"[TS] F1a 原生可燃快照完成：{NativeCombustibleIds.Count} 种（原版斯特林燃料类）"); } catch { }
        }
        catch { }
    }

    internal static bool IsBioGenFuel(int itemId)
    {
        try
        {
            if (itemId == 205) return true;   // 回归：腐肉（205 的 Food 归属未在 dump 验证，留一行兜底；非木材行，不触"无木材硬编码"验收，确认是 Food 后可删）
            if (itemId == 6) return false;    // 炭：灰烬，原生语义，不计入燃料集
            try { if (NativeCombustibleIds.Contains(itemId)) return true; } catch { } // 原生燃料类（快照集，零硬编码）
            if (itemId <= 0) return false;    // 无法识别一律拒
            ItemAttr attr = null;
            try { attr = ItemManager.instance?.GetItemAttrById(itemId); } catch { }
            if (attr == null) return false;
            try { return attr.itemType.ToString().Contains("Food"); } catch { return false; }
        }
        catch { return false; }
    }

    /// <summary>attr 级变体（含灰烬开关）：D 环白名单（IsAllowedFuel）传 includeAsh=true（炭必须回仓）；
    /// F1a 补键一律走 IsBioGenFuel(id)（炭除外）。非灰烬路径直接委托 IsBioGenFuel，不两份逻辑。</summary>
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

    /// <summary>严格白名单（v0.8.10 终版 + 木材类）：Food 类物品全部可入（含腐肉 205、含未过期食品）+ 炭 6（副产品回仓）+ 木头 0；金属等非食品拒。
    /// 过期判定已按用户要求移除——「只要是有新鲜度的食物类都可以放入」。
    /// 注意：ItemData 无 itemAttr 成员（那是 BasicItem 的 protected 字段）——attr 一律经 ItemManager.GetItemAttrById(itemId) 解析（游戏同款路径）。
    /// 吞物品教训：D 环（TryAddItem/AddItem prefix）执行时物品可能已从源容器移除，拒绝=物品悬空丢失；
    /// 因此 D 环只应拒绝 C 环已拦下的非 Food（木头/金属在 C 环 PFL 即被 UI 层挡回，D 环极少触发）。</summary>
    private static bool IsAllowedFuel(ItemData it)
    {
        try
        {
            int id = it.itemId;
            if (id == 205 || id == 6 || IsBioGenFuel(id)) return true;   // 腐肉 / 炭（副产品回仓）/ 原生燃料类（快照集，零硬编码）
            if (id <= 0) return false;                       // 无法识别的物品一律拒（木头 id 0 已在上方命中）
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