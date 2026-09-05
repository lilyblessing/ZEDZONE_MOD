using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using ZedZoneShared;

namespace TeleportStationPlugin;

/// <summary>
/// v0.9.4 P3 二期：充电台克隆盘（900102 新形态）适配。
/// 克隆源切换（斯特林120→充电台126）后：
///   1. 盘天然是电力消费端 → 游戏真实电线路由（电线杆接线）原生可用，充电走原版 UpdateBatteryCharger；
///   2. 本类只做两件事：
///      A. 容器微调（一次性）：productionData.inventoryData1（原版槽位扫描容器）→ 8×8 + 标题「电池仓」+ totalBatterySoltNumber=4；
///      B. ×4 倍率 hook：Supply 源含生物能（900103）时，UpdateBatteryCharger 前后把 powerInputSufficientFloat ×4/恢复
///         （原版公式 totalWh = sufficient × electricWattage × addedTime × 24 → 放大 sufficient 即等效倍率）。
///         v0.9.71 起目标含 126 原版充电台（同条件同享×4）。
///   旧档斯特林组件盘继续由 BatteryChargeFix（虚拟供电充电）照顾。
/// </summary>
public static class ChargerPadFix
{
    private const int PadId = 900102;
    private const int VanillaChargerId = 126; // 原版电池充电台（v0.9.71 起同享生物能×4）
    private const int BioGenId = 900103;
    private static readonly bool EnableDiag = false; // 诊断开关：ScaleDiag/PadSRDump/Stirling/Grid 探针，正常运行关闭以减刷屏
    internal static bool DiagCut = false; // v0.9.97-r5：恢复生产（r4五大切断证伪与塌无关，全部恢复）
    // ═══ v0.9.97-r5 RegFill：跳过即记录、安静即补登记 ═══
    internal static readonly System.Collections.Generic.HashSet<TerrainObject_Production> _skippedReg = new();
    internal static int _regFillCap = 5000;
    internal static double _lastTripUtc = 0; // epoch秒（DateTime.UtcNow计时，暂停不停）
    internal static double _lastRegFillLog = -99;
    internal static bool _regFillCapWarned = false;
    internal static bool DiagCutArmed() // r4：恒返false（r3证伪：武装=开车流送风暴即时SO，断路器永不能开；计数恢复见X）
    {
        return false;
    }
    private static readonly System.Collections.Generic.HashSet<long> _initKeys = new();
    private static float _lastScan = -1f;
    internal static TerrainObject_Production[] _sharedProdSnapshot; // P2-2：双Tick共享快照（任一Tick先到且过期则拷贝一次刷新）
    internal static float _sharedProdSnapTime = -999f; // P2-2：快照时间戳（Time.realtimeSinceStartup）
    private static bool _boosted; // ×4 窗口（prefix 置位 / postfix 恢复）
    private static bool _warnedTypeMiss; // 判定诊断（一次性）
    private static bool _warnedHit;      // ×4 判定诊断（一次性）
    private static readonly System.Collections.Generic.HashSet<long> _pdFixed = new(); // PD 六表已补的实例（去重）
    private static bool _pdTablesCompleted; // P2-1：CompleteAllPdTables会话级脏位（OnEnable新克隆注册/读档重建时复位）
    private static float _lastGridLog;
    private static float _nextHeavyWork = -999f; // v0.9.95-perf：重型表操作 1s 节流门
    private static float _nextHeavyLog = -999f; // v0.9.95-perf：诊断日志 5s 共享窗
    private static bool _stirProbed; // v0.9.11 延迟重试：Stirling表段完成旗标
    private static bool _prefabProbed; // v0.9.11 延迟重试：Prefab段完成旗标
    private static float _lastDicLog = -999f;
    private static float _lastDirtyLog = -999f;
    private static float _savedConnectRange = float.NaN;
    private static float _savedPowerRange = float.NaN;
    private static bool _rangeHijacked;
    private static bool _log103Dist;
    private static float _lastSpriteFix = -999f;
    private static bool _dicDiag;
    private static bool _buildDiag;
    private static readonly System.Collections.Generic.List<object> _knownClones = new();
    // SO兼容：克隆期抑制旗标（RegistrarLogic.Run 入口置位、finally 复位；OnEnable/Init 系 postfix 在克隆 Instantiate 期直接退，掐断 game-side 原生递归环的我方触发段）
    internal static bool IsCloning;
    private static int _onEnableDepth; // SO兼容：OnEnable/Init 系递归深度守卫（>4 直接退）
    private static bool _clonesScanDone; // P1-1：会话级全扫描一次性旗标（OnEnable 注册表续增，扫描只覆盖冷启动竞态）
    private static Il2CppSystem.Type _il2cppProdType;
    private static Il2CppSystem.Type _il2cppStirType;

    // ═══ v0.9.22 R12-2 克隆实例注册表（OnEnable 记录，不依赖可见性）═══
    // SO兼容：postfix void 方法，首行 return 只跳过自体逻辑、不影响游戏原生（非 bool prefix，无跳过原生语义）。
    internal static void NoteClone(object o)
    {
        try
        {
            if (o == null) return;
            if (_knownClones.Contains(o)) return;
            _knownClones.Add(o);
        }
        catch { }
    }

    public static void OnEnableRecorder_P(TerrainObject_Production __instance)
    {
        if (IsCloning) return;
        if (!RegistrarState.Done) return;
        if (_onEnableDepth > 4) return;
        try
        {
            _onEnableDepth++;
            try
            {
                if (__instance == null) return;
                int id = -1;
                try { var a = __instance.attr; if (a != null) id = a.id; } catch { }
                if (id == 900101 || id == 900102 || id == 900103)
                {
                    if (!_knownClones.Contains(__instance))
                    {
                        _knownClones.Add(__instance);
                        try { _pdTablesCompleted = false; } catch { } // P2-1：新克隆注册→脏位复位，下次GridConsume重做一次全扫
                    }
                    try { StampConsumingFlag(__instance); } catch { }
                }
            }
            catch { }
        }
        finally { try { _onEnableDepth--; } catch { } }
    }

    // ═══ OnEnable 断路器（诊断版）：Harmony prefix 返回 false 跳过重入过深的原生 OnEnable ═══
    private static readonly Dictionary<int, int> _oeDepth = new Dictionary<int, int>();
    private static int _oeGlobal = 0;
    private static int _oeLogCount = 0;
    private static int _oePeakGlobal = 0; // v0.9.91-diag：断路器计数历史峰值（只读普查用）
    private static int _oePeakInst = 0;
    private static float _lastBreakerStatLog = -999f; // P4-B2：BreakerStat日志5s窗（峰值照常更新，只压日志）
    // 断路器 prefix（bool 返回：false 跳过原生；pair postfix 必配对减计数。Harmony 里 prefix 跳过后 postfix 照跑，计数平衡。）
    public static bool OnEnableBreaker_P(TerrainObject_Production __instance)
    {
        try
        {
            try { if (DiagCut && DiagCutArmed()) return true; } catch { } // v0.9.96-diag r3：运行时+读档沉降后放行；克隆期/读档风暴不断路器防SO
            int key = 0;
            try { if (__instance != null) key = __instance.GetInstanceID(); } catch { }
            _oeDepth.TryGetValue(key, out int d);
            _oeGlobal++;
            if (d >= 8 || _oeGlobal > 64)
            {
                if (_oeLogCount < 12)
                {
                    _oeLogCount++;
                    string nm = "?"; int aid = -1; bool act = false;
                    try { nm = __instance != null && __instance.gameObject != null ? __instance.gameObject.name : "?"; } catch { }
                    try { var a = __instance.attr; if (a != null) aid = a.id; } catch { }
                    try { act = __instance != null && __instance.gameObject != null && __instance.gameObject.activeSelf; } catch { }
                    Plugin.L.LogWarning($"[TS][Breaker] 跳过重入 OnEnable #{_oeLogCount} key={key} name={nm} attr={aid} active={act} perInst={d} global={_oeGlobal}");
                }
                // v0.9.97-r5 RegFill：跳过即记录（Breaker_P/S共用，Stirling派生类直接Add进同一集）
                try { _lastTripUtc = (System.DateTime.UtcNow - new System.DateTime(1970,1,1)).TotalSeconds; if (_skippedReg.Count < _regFillCap) { if (__instance != null) _skippedReg.Add(__instance); } else if (!_regFillCapWarned) { _regFillCapWarned = true; try { Plugin.L.LogWarning("[TS][RegFill] 队列满 cap=" + _regFillCap); } catch { } } } catch { }
                return false;
            }
            _oeDepth[key] = d + 1;
        }
        catch { }
        return true;
    }
    public static void OnEnableBreaker_X(TerrainObject_Production __instance)
    {
        try
        {
            try { if (false && DiagCut) return; } catch { } // r4：恢复计数（断路器回生产逻辑，P/X配对平衡；读stall期跳过数即判据①证据）
            try // v0.9.91-diag：断路器计数峰值普查（只读+日志，零行为改动；减前采样，天然覆盖真峰值）
            {
                int curKeys = 0;
                try { curKeys = _oeDepth.Count; } catch { }
                if (_oeGlobal > _oePeakGlobal || curKeys > _oePeakInst)
                {
                    if (_oeGlobal > _oePeakGlobal) _oePeakGlobal = _oeGlobal;
                    if (curKeys > _oePeakInst) _oePeakInst = curKeys;
                    try // P4-B2：5s窗节流（峰值照常更新，只压日志）
                    {
                        float nbs = Time.unscaledTime;
                        if (nbs - _lastBreakerStatLog >= 5f) { _lastBreakerStatLog = nbs; try { Plugin.L.LogInfo($"[TS][BreakerStat] peakGlobal={_oePeakGlobal} peakInst={_oePeakInst}"); } catch { } }
                    }
                    catch { }
                }
            }
            catch { }
            try { _oeGlobal--; } catch { }
            int key = 0;
            try { if (__instance != null) key = __instance.GetInstanceID(); } catch { }
            bool hasDepth = true;
            try { hasDepth = _oeDepth.ContainsKey(key); } catch { hasDepth = true; } // 读表失败不误报
            if (!hasDepth) { try { Plugin.L.LogWarning($"[TS][BreakerLeak] key={key}"); } catch { } } // v0.9.91-diag：P未加过或已泄漏
            if (_oeDepth.TryGetValue(key, out int d)) { if (d <= 1) _oeDepth.Remove(key); else _oeDepth[key] = d - 1; }
        }
        catch { }
    }

    // ═══ v0.9.97-r5 RegFill：安静即补登记（Add-if-absent + Mark）═══
    internal static void DrainSkippedReg()
    {
        try
        {
            // ①世界未活跃直接返（世界活跃判定：gc+playerCharacter非空）
            try { var gc = GameController.instance; if (gc == null || gc.playerCharacter == null) return; } catch { return; }
            // ②安静门：风暴后3s内不补
            double nowUtc = 0;
            try { nowUtc = (System.DateTime.UtcNow - new System.DateTime(1970,1,1)).TotalSeconds; } catch { return; }
            if ((nowUtc - _lastTripUtc) < 3.0) return;
            // ③空集返
            int pending = 0;
            try { pending = _skippedReg.Count; } catch { return; }
            if (pending <= 0) return;
            // ④取表（Il2Cpp List；抄RebuildIO pre段现成路径）
            Il2CppSystem.Collections.Generic.List<TerrainObject_Production> prodList = null;
            try { prodList = TerrainObject_Production.ActiveObjects_Production; } catch { return; }
            if (prodList == null) return;
            // ⑤预算10个/次，快照防修改枚举
            System.Collections.Generic.List<TerrainObject_Production> snap = null;
            try { snap = new System.Collections.Generic.List<TerrainObject_Production>(_skippedReg); } catch { return; }
            int added = 0, present = 0, dead = 0, budget = 10;
            foreach (var inst in snap)
            {
                if (budget <= 0) break;
                try
                {
                    if (inst == null) { dead++; try { _skippedReg.Remove(inst); } catch { } budget--; continue; }
                }
                catch { try { dead++; _skippedReg.Remove(inst); } catch { } budget--; continue; }
                bool contains = false;
                try { contains = prodList.Contains(inst); } catch { contains = false; }
                if (contains) { present++; try { _skippedReg.Remove(inst); } catch { } budget--; continue; }
                try { prodList.Add(inst); added++; } catch { }
                try { _skippedReg.Remove(inst); } catch { }
                budget--;
            }
            // ⑥added>0调一次Mark（抄TeleportPadTrigger.cs约110行直接调用写法）
            if (added > 0)
            {
                try { ProductionManager.MarkElectricGridDirty(); } catch { }
            }
            // ⑦节流日志2s
            try
            {
                int done = added + present + dead;
                if (done > 0 && (nowUtc - _lastRegFillLog) > 2.0)
                {
                    _lastRegFillLog = nowUtc;
                    try { Plugin.L.LogInfo($"[TS][RegFill] add={added} present={present} dead={dead} remain={_skippedReg.Count}"); } catch { }
                }
            }
            catch { }
        }
        catch { }
    }

    // P6.1 新增：通用 TerrainObject OnEnable 捕获（900101 控制台类型为 TerrainObject_Furniture_Commu，不走 Production）
    public static void OnEnableRecorder_All(TerrainObject __instance)
    {
        if (IsCloning) return;
        if (!RegistrarState.Done) return;
        if (_onEnableDepth > 4) return;
        try
        {
            _onEnableDepth++;
            try
            {
                if (__instance == null) return;
                int id = -1;
                try { var a = __instance.attr; if (a != null) id = a.id; } catch { }
                if (id == 900101 || id == 900102 || id == 900103)
                {
                    if (!_knownClones.Contains(__instance))
                    {
                        _knownClones.Add(__instance);
                        try { _pdTablesCompleted = false; } catch { } // P2-1：新克隆注册→脏位复位，下次GridConsume重做一次全扫
                    }
                    try { StampConsumingFlag(__instance); } catch { }
                }
            }
            catch { }
        }
        finally { try { _onEnableDepth--; } catch { } }
    }

    /// <summary>P2-2 共享快照：命中（≤0.5s）直接复用；过期则拷贝一次刷新。取不到活列表返回旧快照（可null），
    /// 调用方回退活列表直读，不崩。只用 Count+索引器（全仓既有用法），不调 ToArray（无盘上先例，免编译风险）。</summary>
    internal static TerrainObject_Production[] GetSharedProdSnapshot()
    {
        try
        {
            float now = Time.realtimeSinceStartup;
            var s = _sharedProdSnapshot;
            try { if (s != null && now - _sharedProdSnapTime <= 0.5f) return s; } catch { }
            var live = TerrainObject_Production.ActiveObjects_Production;
            if (live == null) return s;
            TerrainObject_Production[] arr = null;
            try
            {
                int c = live.Count;
                var tmp = new TerrainObject_Production[c];
                for (int i = 0; i < c; i++) { try { tmp[i] = live[i]; } catch { } }
                arr = tmp;
            }
            catch { arr = null; }
            if (arr != null)
            {
                _sharedProdSnapshot = arr;
                try { _sharedProdSnapTime = now; } catch { }
                return arr;
            }
            return s;
        }
        catch { try { return _sharedProdSnapshot; } catch { return null; } }
    }

    /// <summary>由 RegistrationProbe.Update 每帧调用（内部 0.5s 节流）。</summary>
    public static void Tick()
    {
        try
        {
            float now = Time.unscaledTime;
            if (now - _lastScan < 0.5f) return;
            _lastScan = now;
            try { DrainSkippedReg(); } catch { } // P4-B3：挪入0.5s门（语义不变，安静3s才补）
            try { ProbeOnce(); } catch { }
            try { EnsureGameDictionaries(); } catch { }
            try { EnsurePadSprites(); } catch { }
            try { EnsureScales(); } catch { }
            try { EnsureConsumingFlag(); } catch { }
            // P6.8: 控制台外部已原生化，不再每 0.5s 轮询
            // try { TeleportConsoleInteractFix.Tick(); } catch { }
            // P2-2：吃共享快照（本Tick先到且过期则在Get内拷贝一次；BuildingPadFix侧同吃）。节流相位与下述判定逻辑不动。
            var list = GetSharedProdSnapshot();
            if (list == null) return;
            for (int i = 0; i < list.Length; i++)
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

    // ── v0.9.12 R1：字典精确补键（消灭 NRE + 恢复新建/查询，5s 节流）── v0.9.19 编译期直访改造
    public static void EnsureGameDictionaries()
    {
        try
        {
            float now = Time.unscaledTime;
            if (now - _lastDicLog < 5f) return;
            GameController gc = null;
            try { gc = GameController.instance; } catch { }
            if (gc == null) return;
            bool did = false;
            if (!_dicDiag)
            {
                _dicDiag = true;
                bool hadAttr = false, hadPrefab = false;
                try { var d = gc.terrainObjectAttrDic; hadAttr = d != null && d.ContainsKey(900102); } catch { }
                try { var d = gc.terrainObjectPrefabDic; hadPrefab = d != null && d.ContainsKey(900102); } catch { }
                Plugin.L.LogInfo($"[TS] 字典补键诊断: instance=ok attrHad={hadAttr} prefabHad={hadPrefab}");
            }
            try
            {
                var d = gc.terrainObjectAttrDic;
                if (d != null)
                {
                    int[] ids = { 900101, 900102, 900103 };
                    foreach (var id in ids)
                    {
                        if (RegistrationStore.Attrs.TryGetValue(id, out var attr) && attr != null && !d.ContainsKey(id)) { d.Add(id, attr); did = true; }
                    }
                }
            }
            catch { }
            try
            {
                var d = gc.terrainObjectPrefabDic;
                if (d != null)
                {
                    int[] ids = { 900101, 900102, 900103 };
                    foreach (var id in ids)
                    {
                        if (RegistrationStore.Prefabs.TryGetValue(id, out var clone) && clone != null && !d.ContainsKey(id)) { d.Add(id, clone); did = true; }
                    }
                }
            }
            catch { }
            if (did)
            {
                _lastDicLog = now;
                Plugin.L.LogInfo("[TS] 字典补键完成（attr/prefab 已补）");
            }
        }
        catch { }
    }

    // ── v0.9.21 R11-1 权威补键 hook：GameController.InitTerrainObjectAttrs postfix（场景加载权威重建后补键）──
    public static void InitTerrainObjectAttrsPostfix(GameController __instance)
    {
        try
        {
            if (__instance == null) return;
            try { _pdTablesCompleted = false; } catch { } // P2-1：场景加载权威重建→新世界新PD集，脏位复位
            bool did = false;
            try
            {
                var d = __instance.terrainObjectAttrDic;
                if (d != null)
                {
                    int[] ids = { 900101, 900102, 900103 };
                    foreach (var id in ids)
                    {
                        if (RegistrationStore.Attrs.TryGetValue(id, out var attr) && attr != null && !d.ContainsKey(id)) { d.Add(id, attr); did = true; }
                    }
                }
            }
            catch { }
            try
            {
                var d = __instance.terrainObjectPrefabDic;
                if (d != null)
                {
                    int[] ids = { 900101, 900102, 900103 };
                    foreach (var id in ids)
                    {
                        if (RegistrationStore.Prefabs.TryGetValue(id, out var clone) && clone != null && !d.ContainsKey(id)) { d.Add(id, clone); did = true; }
                    }
                }
            }
            catch { }
            if (did) Plugin.L.LogInfo("[TS] InitTerrainObjectAttrs 补键完成（attr/prefab 已补）");
        }
        catch { }
    }

    // ── v0.9.24 Fix: 巡检回钉改用 BodyCache（ppu=worldH 正确），Icon 缓存 ppu100 会导致 3.13x2.24 缩小，禁用于实体 ──
    private static void EnsurePadSprites()
    {
        try
        {
            float now = Time.unscaledTime;
            if (now - _lastSpriteFix < 5f) return;
            _lastSpriteFix = now;
            var list = TerrainObject_Production.ActiveObjects_Production;
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                var g = list[i];
                if (g == null) continue;
                var to = FindTerrainObject(g.transform) as TerrainObject;
                if (to == null || to.attr == null) continue;
                int aid = to.attr.id;
                if (aid != 900101 && aid != 900102 && aid != 900103) continue;
                // 诊断：900102 每轮 dump 全部 SR 详情（已关闭，EnableDiag=false 时不刷屏）
                if (EnableDiag && aid == 900102 && i == 0)
                {
                    try
                    {
                        var allSrs = g.GetComponentsInChildren<SpriteRenderer>(true);
                        var sb = new System.Text.StringBuilder($"[TS][PadSRDump] id={aid} go={to.name} n={allSrs.Length} t={now:F1}s | ");
                        for (int di = 0; di < allSrs.Length && di < 10; di++)
                        {
                            var sr = allSrs[di];
                            if (sr == null) continue;
                            string sn = sr.name ?? "?";
                            string spn = sr.sprite != null ? sr.sprite.name : "null";
                            string col = $"{sr.color.r:F2},{sr.color.g:F2},{sr.color.b:F2},{sr.color.a:F2}";
                            string mat = sr.sharedMaterial != null ? sr.sharedMaterial.name : (sr.material != null ? sr.material.name : "null");
                            sb.Append($"[{di}:{sn} sp={spn} ppu={(sr.sprite!=null?sr.sprite.pixelsPerUnit:0):F1} col={col} mat={mat} en={sr.enabled} layer={sr.sortingLayerName}:{sr.sortingOrder} pos={sr.transform.localPosition.x:F2},{sr.transform.localPosition.y:F2}] ");
                        }
                        try
                        {
                            var sh = Reflect.Get(to, "shadowSR") as SpriteRenderer;
                            var refl = Reflect.Get(to, "reflectedSpriteRenderer") as SpriteRenderer;
                            sb.Append($"| shadowSR={(sh!=null?sh.name+":"+(sh.sprite!=null?sh.sprite.name:"null"):"null")} refl={(refl!=null?refl.name+":"+(refl.sprite!=null?refl.sprite.name:"null"):"null")}");
                        }
                        catch { }
                        Plugin.L.LogInfo(sb.ToString());
                    }
                    catch { }
                }
                // 优先 BodyCache（实体正确 ppu），回退 Cache（图标）仅防空
                Sprite cacheSp = null;
                bool hasBody = SpriteInjector.BodyCache.TryGetValue(aid, out cacheSp) && cacheSp != null;
                if (!hasBody && (!SpriteInjector.Cache.TryGetValue(aid, out cacheSp) || cacheSp == null)) continue;
                SpriteRenderer[] instSrs = null;
                try { instSrs = g.GetComponentsInChildren<SpriteRenderer>(true); } catch { continue; }
                if (instSrs == null || instSrs.Length == 0) continue;
                int n = instSrs.Length;
                bool changed = false;
                // 900102 绿叠加层即时屏蔽（ChargingStateSprite 11 个，通电后绿覆盖，需禁用；历史存量每 5s 清一次）
                if (aid == 900102)
                {
                    try
                    {
                        for (int kk = 0; kk < instSrs.Length; kk++)
                        {
                            var s2 = instSrs[kk];
                            if (s2 == null) continue;
                            string sn2 = s2.name ?? "";
                            if (sn2.Contains("ChargingState") && s2.enabled)
                            {
                                s2.enabled = false;
                                changed = true;
                            }
                        }
                    }
                    catch { }
                }
                int limit = n > 8 ? 8 : n;
                for (int k = 0; k < limit; k++)
                {
                    var sr = instSrs[k];
                    if (sr == null) continue;
                    try
                    {
                        // 只有主 SR 需要 Body，非主保持禁用逻辑已在别处；此处仅当 sprite 与 Body 不一致且不是空时回钉
                        if (sr.sprite != null && !ReferenceEquals(sr.sprite, cacheSp))
                        {
                            // 兼容：若当前已是 Body（name 含 _Body 且 ppu 与 Body 一致）则跳过，避免 icon↔body 乒乓
                            bool curIsBody = sr.sprite.name != null && sr.sprite.name.EndsWith("_Body");
                            bool cacheIsBody = cacheSp.name != null && cacheSp.name.EndsWith("_Body");
                            if (curIsBody && cacheIsBody) { /* 都是 Body，引用不同但 ppu 一致则不强制 */ if (Math.Abs(sr.sprite.pixelsPerUnit - cacheSp.pixelsPerUnit) < 0.1f) continue; }
                            if (curIsBody && !cacheIsBody) continue; // 当前已是 Body，缓存是 Icon 时绝不覆写（防缩小）
                            sr.sprite = cacheSp; changed = true;
                        }
                    }
                    catch { }
                }
                if (changed) Plugin.L.LogInfo($"[TS] 巡检贴图重钉: id={aid} srs={n} ppu={cacheSp.pixelsPerUnit:F1}");
            }
        }
        catch { }
    }

    // ── v0.9.12 R2：electricConsuming 兜底 ── P1-3：会话级扫一次存量后永久返回（增量由注册点/Build-Add 直接盖）
    private static bool _consumingSwept;
    public static void EnsureConsumingFlag()
    {
        try
        {
            if (_consumingSwept) return;
            _consumingSwept = true;
            var list = TerrainObject_Production.ActiveObjects_Production;
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                var g = list[i];
                if (g == null) continue;
                if (GetClonedAttrId(g) != 900102) continue;
                var to = FindTerrainObject(g.transform) as TerrainObject;
                if (to == null) continue;
                var attr = to.attr;
                if (attr == null) continue;
                if (!attr.electricConsuming)
                {
                    try { attr.electricConsuming = true; } catch { }
                }
            }
        }
        catch { }
    }

    // ── v0.9.12 R3：表+桶注册兜底（重扫前把克隆塞回游戏视线）──
    // ── v0.9.14 R6-1：事件驱动补表（重扫前用 FindObjectsOfType 含 inactive 回找回克隆实例）──
    private static void EnsureTypeCacheForClones()
    {
        try { if (_il2cppProdType == null) _il2cppProdType = Il2CppSystem.Type.GetType(typeof(TerrainObject_Production).FullName) ?? Il2CppSystem.Type.GetType("TerrainObject_Production, Assembly-CSharp"); } catch {}
        try { if (_il2cppStirType == null) _il2cppStirType = Il2CppSystem.Type.GetType(typeof(TerrainObject_Production_StirlingGenerator).FullName) ?? Il2CppSystem.Type.GetType("TerrainObject_Production_StirlingGenerator, Assembly-CSharp"); } catch {}
    }

    private static void EnsureClonesInTables()
    {
        try { EnsureTypeCacheForClones(); } catch {}
        try
        {
            // hidden 存量实例（FindObjectsOfType 默认跳过 HideAndDontSave/HideInHierarchy）→ 必须 Resources 通道 — 900102
            var hiddenRaw = UnityEngine.Resources.FindObjectsOfTypeAll(_il2cppProdType ?? Il2CppSystem.Type.GetType(typeof(TerrainObject_Production).FullName) ?? Il2CppSystem.Type.GetType("TerrainObject_Production, Assembly-CSharp"));
            if (hiddenRaw != null)
            {
                for (int h = 0; h < hiddenRaw.Length; h++)
                {
                    var hg = hiddenRaw[h] as TerrainObject_Production;
                    if (hg == null) continue;
                    bool isPadH = false;
                    try { var to = FindTerrainObject(hg.transform); if (to != null) { object a = null; try { a = Reflect.Get(to, "attr"); } catch { } if (a != null) { if (RegistrationStore.Attrs.TryGetValue(900102, out var our) && ReferenceEquals(a, our)) isPadH = true; else if (AttrId(a) == 900102) isPadH = true; } } } catch { }
                    if (!isPadH) continue;
                    bool sceneOk = false;
                    try { var sc = hg.gameObject.scene; sceneOk = sc.IsValid(); } catch { sceneOk = false; }
                    if (!sceneOk) continue;
                    try { hg.gameObject.hideFlags = HideFlags.None; } catch { }
                    var listH = TerrainObject_Production.ActiveObjects_Production;
                    if (listH == null) continue;
                    bool containsH = false;
                    try { containsH = listH.Contains(hg); } catch { for (int k = 0; k < listH.Count; k++) if (ReferenceEquals(listH[k], hg)) { containsH = true; break; } }
                    if (!containsH) { try { listH.Add(hg); } catch { } }
                }
            }
            // 900102 → ActiveObjects_Production
            var prodsRaw = UnityEngine.Object.FindObjectsOfType(_il2cppProdType ?? Il2CppSystem.Type.GetType(typeof(TerrainObject_Production).FullName) ?? Il2CppSystem.Type.GetType("TerrainObject_Production, Assembly-CSharp"));
            if (prodsRaw != null)
            {
                for (int i = 0; i < prodsRaw.Length; i++)
                {
                    var g = prodsRaw[i] as TerrainObject_Production;
                    if (g == null) continue;
                    bool isPad = false;
                    try { var to = FindTerrainObject(g.transform); if (to != null) { object a = null; try { a = Reflect.Get(to, "attr"); } catch { } if (a != null) { if (RegistrationStore.Attrs.TryGetValue(900102, out var our) && ReferenceEquals(a, our)) isPad = true; else if (AttrId(a) == 900102) isPad = true; } } } catch { }
                    if (!isPad) continue;
                    try { g.gameObject.hideFlags = HideFlags.None; } catch { }
                    var list = TerrainObject_Production.ActiveObjects_Production;
                    if (list == null) continue;
                    bool contains = false;
                    try { contains = list.Contains(g); } catch { for (int k = 0; k < list.Count; k++) if (ReferenceEquals(list[k], g)) { contains = true; break; } }
                    if (!contains) { try { list.Add(g); } catch { } }
                }
            }
        }
        catch { }
        try
        {
            // hidden 存量实例（FindObjectsOfType 默认跳过 HideAndDontSave/HideInHierarchy）→ 必须 Resources 通道 — 900103
            var hiddenRaw2 = UnityEngine.Resources.FindObjectsOfTypeAll(_il2cppStirType ?? Il2CppSystem.Type.GetType(typeof(TerrainObject_Production_StirlingGenerator).FullName) ?? Il2CppSystem.Type.GetType("TerrainObject_Production_StirlingGenerator, Assembly-CSharp"));
            if (hiddenRaw2 != null)
            {
                for (int h = 0; h < hiddenRaw2.Length; h++)
                {
                    var hg2 = hiddenRaw2[h] as TerrainObject_Production_StirlingGenerator;
                    if (hg2 == null) continue;
                    bool isBioH = false;
                    try { var to = FindTerrainObject(hg2.transform); if (to != null) { object a = null; try { a = Reflect.Get(to, "attr"); } catch { } if (a != null) { if (RegistrationStore.Attrs.TryGetValue(900103, out var our) && ReferenceEquals(a, our)) isBioH = true; else if (AttrId(a) == 900103) isBioH = true; } } } catch { }
                    if (!isBioH) continue;
                    bool sceneOk2 = false;
                    try { var sc2 = hg2.gameObject.scene; sceneOk2 = sc2.IsValid(); } catch { sceneOk2 = false; }
                    if (!sceneOk2) continue;
                    try { hg2.gameObject.hideFlags = HideFlags.None; } catch { }
                    var stirListH = TerrainObject_Production.ActiveObjects_Production;
                    if (stirListH == null) continue;
                    bool containsH2 = false;
                    try { containsH2 = stirListH.Contains(hg2); } catch { for (int k = 0; k < stirListH.Count; k++) if (ReferenceEquals(stirListH[k], hg2)) { containsH2 = true; break; } }
                    if (!containsH2) { try { stirListH.Add(hg2); } catch { } }
                }
            }
            // 900103 → ActiveObjects_Production（新版：发电子类静态表已删，并入基类表）
            var stirsRaw = UnityEngine.Object.FindObjectsOfType(_il2cppStirType ?? Il2CppSystem.Type.GetType(typeof(TerrainObject_Production_StirlingGenerator).FullName) ?? Il2CppSystem.Type.GetType("TerrainObject_Production_StirlingGenerator, Assembly-CSharp"));
            if (stirsRaw != null)
            {
                for (int i = 0; i < stirsRaw.Length; i++)
                {
                    var sg = stirsRaw[i] as TerrainObject_Production_StirlingGenerator;
                    if (sg == null) continue;
                    bool isBio = false;
                    try { var to = FindTerrainObject(sg.transform); if (to != null) { object a = null; try { a = Reflect.Get(to, "attr"); } catch { } if (a != null) { if (RegistrationStore.Attrs.TryGetValue(900103, out var our) && ReferenceEquals(a, our)) isBio = true; else if (AttrId(a) == 900103) isBio = true; } } } catch { }
                    if (!isBio) continue;
                    try { sg.gameObject.hideFlags = HideFlags.None; } catch { }
                    var stirList = TerrainObject_Production.ActiveObjects_Production;
                    if (stirList == null) continue;
                    bool contains2 = false;
                    try { contains2 = stirList.Contains(sg); } catch { for (int k = 0; k < stirList.Count; k++) if (ReferenceEquals(stirList[k], sg)) { contains2 = true; break; } }
                    if (!contains2) { try { stirList.Add(sg); } catch { } }
                }
            }
        }
        catch { }
    }

    public static void GridConsumePrefix(ProductionManager __instance)
    {
        if (!RegistrarState.Done) return; // 读档期冻结，沉降后才补表/劫持range
        float nowF = Time.unscaledTime; // v0.9.95-perf：单快照驱动 heavy/log 双门
        bool doHeavy = nowF >= _nextHeavyWork; if (doHeavy) _nextHeavyWork = nowF + 1f;
        bool doLog = nowF >= _nextHeavyLog; // 置位点在 Postfix 末处 PoleCensus，同窗四处同 tick 同过
        try // v0.9.91-diag：重建前 IO 普查（只读+日志，零行为改动）
        {
            int preAO = -1, prePD = -1;
            try { var ao = TerrainObject_Production.ActiveObjects_Production; if (ao != null) preAO = ao.Count; } catch { }
            try { var pl = __instance != null ? __instance.productionDataList : null; if (pl != null) prePD = pl.Count; } catch { prePD = -1; }
            try { if (doLog) Plugin.L.LogInfo($"[TS][RebuildIO] pre ActiveObjects={preAO} prodList={prePD}"); } catch { }
        }
        catch { }
        try
        {
            try { CompleteAllPdTables(__instance); } catch { } // 脏位门（廉价），每 tick 照跑
            if (doHeavy) // v0.9.95-perf：重型段 1s 节流（Ensure/补表/range劫持/回灌/Stirling 原样内移）
            {
            try { if (!_clonesScanDone || _knownClones.Count == 0) { EnsureClonesInTables(); _clonesScanDone = true; } } catch { }
            // v0.9.22：从克隆注册表补表（H&D 下 FindObjectsOfType 不可见，注册表不依赖可见性）
            try
            {
                int reinforceAdds = 0; // v0.9.91-diag：实际 Add 计数（只读计数+尾部日志，零行为改动）
                var all = _knownClones.ToArray(); // P4-A3：DiagCut恒false，切断回灌分支已删
                foreach (var o in all)
                {
                    try
                    {
                        var g102 = o as TerrainObject_Production;
                        if (g102 != null)
                        {
                            if (g102.gameObject == null) continue;
                            int tid = -1;
                            try { var ta = g102.attr; if (ta != null) tid = ta.id; } catch { }
                            if (tid == 900102 || tid == 900101)
                            {
                                try { g102.gameObject.hideFlags = HideFlags.None; } catch { }
                                var plist = TerrainObject_Production.ActiveObjects_Production;
                                if (plist != null && !plist.Contains(g102)) { plist.Add(g102); reinforceAdds++; }
                            }
                            else if (tid == 900103)
                            {
                                var s = o as TerrainObject_Production_StirlingGenerator;
                                if (s != null)
                                {
                                    try { s.gameObject.hideFlags = HideFlags.None; } catch { }
                                    var slist = TerrainObject_Production.ActiveObjects_Production;
                                    if (slist != null && !slist.Contains(s)) { slist.Add(s); reinforceAdds++; }
                                }
                            }
                        }
                    }
                    catch { }
                }
                try { if (doLog) Plugin.L.LogInfo($"[TS][Reinforce] adds={reinforceAdds}"); } catch { }
            }
            catch { }
            if (!_rangeHijacked)
            {
                try
                {
                    var t = typeof(TerrainObject_Production_ElectricPole);
                    var fc = t.GetField("maxConnectRange", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    var fp = t.GetField("maxPowerRange", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (fc != null) { _savedConnectRange = Convert.ToSingle(fc.GetValue(null)); fc.SetValue(null, 30f); }
                    if (fp != null) { _savedPowerRange = Convert.ToSingle(fp.GetValue(null)); fp.SetValue(null, 30f); }
                    _rangeHijacked = fc != null || fp != null;
                }
                catch { }
            }
            // 900102：Production 表 + BatteryCharger 桶（7）
            try
            {
                var prodList = TerrainObject_Production.ActiveObjects_Production;
                if (prodList != null)
                {
                    TerrainObject_Production padInst = null;
                    object padPd = null;
                    for (int i = 0; i < prodList.Count; i++)
                    {
                        var g = prodList[i];
                        if (g == null) continue;
                        int aid = -1;
                        try { aid = GetClonedAttrId(g); } catch { }
                        bool isTarget = aid == 900102;
                        if (!isTarget)
                        {
                            try
                            {
                                var to = FindTerrainObject(g.transform);
                                if (to != null)
                                {
                                    object a = null;
                                    try { a = Reflect.Get(to, "attr"); } catch { }
                                    if (a != null && AttrId(a) == 900102) isTarget = true;
                                }
                            }
                            catch { }
                        }
                        if (isTarget) { padInst = g; break; }
                    }
                    if (padInst != null)
                    {
                        // a. Production 活动表
                        try
                        {
                            bool contains = false;
                            try { contains = prodList.Contains(padInst); } catch { contains = false; if (prodList != null) { for (int k = 0; k < prodList.Count; k++) if (ReferenceEquals(prodList[k], padInst)) { contains = true; break; } } }
                            if (!contains)
                            {
                                try { prodList.Add(padInst); } catch { }
                            }
                        }
                        catch { }
                        // b. BatteryCharger 桶（ProductionObjectType.BatteryCharger = 7）
                        try
                        {
                            var tod = Reflect.Get(padInst, "objectData");
                            if (tod != null) padPd = Reflect.Get(tod, "productionData");
                        }
                        catch { }
                        if (padPd != null && __instance != null)
                        {
                            try
                            {
                                object dicObj = null;
                                try { dicObj = Reflect.Get(__instance, "productionDataTypeDic"); } catch { }
                                if (dicObj == null) dicObj = __instance.productionDataTypeDic;
                                if (dicObj != null)
                                {
                                    object bucket = null;
                                    // 尝试 TryGetValue(BatteryCharger)
                                    try
                                    {
                                        var tryGet = dicObj.GetType().GetMethod("TryGetValue");
                                        if (tryGet != null)
                                        {
                                            object[] args = new object[] { ProductionObjectType.BatteryCharger, null };
                                            bool ok = (bool)tryGet.Invoke(dicObj, args);
                                            if (ok) bucket = args[1];
                                        }
                                    }
                                    catch { }
                                    if (bucket == null)
                                    {
                                        // 枚举键线性遍历兜底
                                        try
                                        {
                                            var keysProp = dicObj.GetType().GetProperty("Keys");
                                            var keys = keysProp?.GetValue(dicObj) as System.Collections.IEnumerable;
                                            if (keys != null)
                                            {
                                                foreach (var key in keys)
                                                {
                                                    try { if (Convert.ToInt32(key) == (int)ProductionObjectType.BatteryCharger) { var getItem = dicObj.GetType().GetMethod("get_Item") ?? dicObj.GetType().GetProperty("Item")?.GetGetMethod(); if (getItem != null) bucket = getItem.Invoke(dicObj, new object[] { key }); break; } } catch { }
                                                }
                                            }
                                        }
                                        catch { }
                                    }
                                    if (bucket != null && padPd is ProductionData pdTyped)
                                    {
                                        bool containsPd = false;
                                        try { containsPd = ((Il2CppSystem.Collections.Generic.List<ProductionData>)bucket).Contains(pdTyped); } catch { try { var m = bucket.GetType().GetMethod("Contains"); if (m != null) containsPd = (bool)m.Invoke(bucket, new object[] { pdTyped }); else { var cntProp = bucket.GetType().GetProperty("Count"); int cnt = cntProp != null ? Convert.ToInt32(cntProp.GetValue(bucket)) : 0; for (int k = 0; k < cnt; k++) { var getItem = bucket.GetType().GetMethod("get_Item") ?? bucket.GetType().GetProperty("Item")?.GetGetMethod(); var it = getItem?.Invoke(bucket, new object[] { k }); if (ReferenceEquals(it, pdTyped)) { containsPd = true; break; } } } } catch { } }
                                        if (!containsPd)
                                        {
                                            try { ((Il2CppSystem.Collections.Generic.List<ProductionData>)bucket).Add(pdTyped); } catch { try { var m = bucket.GetType().GetMethod("Add"); m?.Invoke(bucket, new object[] { pdTyped }); } catch { } }
                                        }
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }
            // 900103：Stirling 表
            try
            {
                var stirList = TerrainObject_Production.ActiveObjects_Production;
                if (stirList != null)
                {
                    TerrainObject_Production_StirlingGenerator sgInst = null;
                    for (int i = 0; i < stirList.Count; i++)
                    {
                        var sg = stirList[i] as TerrainObject_Production_StirlingGenerator;
                        if (sg == null) continue;
                        bool isTarget = false;
                        try
                        {
                            var to = FindTerrainObject(sg.transform);
                            if (to != null)
                            {
                                object a = null;
                                try { a = Reflect.Get(to, "attr"); } catch { }
                                if (a != null)
                                {
                                    if (RegistrationStore.Attrs.TryGetValue(900103, out var our) && ReferenceEquals(a, our)) isTarget = true;
                                    else if (AttrId(a) == 900103) isTarget = true;
                                }
                            }
                        }
                        catch { }
                        // 兜底：Production 表里也可能有 900103 实例但未入 Stirling 表，此处仅处理 Stirling 表现有实例的归属已正确；若实例本身不在 Stirling 表需另从 Production 表搬运——此处不搬，避免类型错
                        if (isTarget) { sgInst = sg; break; }
                    }
                    // 若遍历未找到但 Production 表里有 900103 的 StirlingGenerator 实例幽灵，需搬入 Stirling 表
                    if (sgInst == null)
                    {
                        var prodList2 = TerrainObject_Production.ActiveObjects_Production;
                        if (prodList2 != null)
                        {
                            for (int i = 0; i < prodList2.Count; i++)
                            {
                                var g = prodList2[i];
                                if (g == null) continue;
                                // 900103 的实例在 Production 表里的类型可能是 BatteryCharger 克隆误判？仅当它是 StirlingGenerator 类型
                                if (g is TerrainObject_Production_StirlingGenerator sg2)
                                {
                                    bool isBio = false;
                                    try
                                    {
                                        var to = FindTerrainObject(sg2.transform);
                                        if (to != null)
                                        {
                                            object a = null;
                                            try { a = Reflect.Get(to, "attr"); } catch { }
                                            if (a != null)
                                            {
                                                if (RegistrationStore.Attrs.TryGetValue(900103, out var our2) && ReferenceEquals(a, our2)) isBio = true;
                                                else if (AttrId(a) == 900103) isBio = true;
                                            }
                                        }
                                    }
                                    catch { }
                                    if (isBio) { sgInst = sg2; break; }
                                }
                            }
                        }
                    }
                    if (sgInst != null)
                    {
                        bool contains = false;
                        try { contains = stirList.Contains(sgInst); } catch { contains = false; for (int k = 0; k < stirList.Count; k++) if (ReferenceEquals(stirList[k], sgInst)) { contains = true; break; } }
                        if (!contains)
                        {
                            try { stirList.Add(sgInst); } catch { }
                        }
                    }
                }
            }
            catch { }
            } // v0.9.95-perf：doHeavy 关
        }
        catch { }
    }

    // ── v0.9.12 R4：启停触发重扫（2s 节流）──
    public static void TriggerGridDirty()
    {
        try
        {
            float now = Time.unscaledTime;
            if (now - _lastDirtyLog < 2f) return;
            _lastDirtyLog = now;
            var m = typeof(ProductionManager).GetMethod("MarkElectricGridDirty", BindingFlags.Public | BindingFlags.Static);
            if (m != null) m.Invoke(null, null);
        }
        catch { }
    }

    /// <summary>v0.9.74 读档重扫 NRE 根治：重扫前把工作集里全部 PD（含原生物件）的六连接表补齐。
    /// 原生 ElectricPole.RefreshElectricConnection:106 对表 Add 无守卫，读档时序下任一表 null 即掀翻整轮重扫
    /// （杆-盘线消失、盘断电，拨燃料手动重扫才自愈）。返回补齐字段数（0 即无事发生）。</summary>
    private static int CompleteAllPdTables(ProductionManager mgr)
    {
        int n = 0;
        try { if (DiagCut) return 0; } catch { } // v0.9.96-diag：切断PD补空表（LoadGuardFix.cs不动，读档安全保留）
        // P2-1 脏位：会话内全扫一次即够；增量实例不依赖本全扫（Tick.EnsurePdTablesOnce经_pdFixed去重逐个补，OnEnable新注册复位脏位）。
        try { if (_pdTablesCompleted) return 0; } catch { }
        try { if (_knownClones.Count == 0) return 0; } catch { } // 无克隆早退（不置脏位，后续新放盘仍会全扫一次）
        try
        {
            if (mgr != null)
            {
                try
                {
                    var all = mgr.productionDataList;
                    if (all != null)
                    {
                        for (int i = 0; i < all.Count; i++)
                        {
                            try { n += EnsurePdTables(all[i]); } catch { }
                        }
                    }
                }
                catch { }
            }
        }
        catch { }
        try
        {
            var list = TerrainObject_Production.ActiveObjects_Production;
            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    try
                    {
                        var g = list[i];
                        if (g == null) continue;
                        object pd = null;
                        var tod = Reflect.Get(g, "objectData");
                        if (tod != null) pd = Reflect.Get(tod, "productionData");
                        n += EnsurePdTables(pd);
                    }
                    catch { }
                }
            }
        }
        catch { }
        try
        {
            var slist = TerrainObject_Production.ActiveObjects_Production;
            if (slist != null)
            {
                for (int i = 0; i < slist.Count; i++)
                {
                    try
                    {
                        var s = slist[i];
                        if (s == null) continue;
                        object pd = null;
                        var tod = Reflect.Get(s, "objectData");
                        if (tod != null) pd = Reflect.Get(tod, "productionData");
                        n += EnsurePdTables(pd);
                    }
                    catch { }
                }
            }
        }
        catch { }
        if (n > 0) Plugin.L.LogInfo($"[TS] 重扫前PD全表补齐 {n} 字段（含原生物件）");
        try { _pdTablesCompleted = true; } catch { } // P2-1：首次跑完置位，后续GridConsume跳过
        return n;
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
        try { k = KeyOf(pd); } catch { try { k = pd.GetType().GetHashCode(); } catch { } } // P-键统一：经KeyOf（PD纯数据对象无GetInstanceID→内部沿用GetHashCode旧式，见KeyOf注释）
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
            // P1-2 快路：编译期直访 g.attr，先 ReferenceEquals 再比 id；仅 null 时回退旧爬链＋反射
            try
            {
                var ta = g != null ? g.attr : null;
                if (ta != null)
                {
                    if (RegistrationStore.Attrs.TryGetValue(900101, out var fa1) && ReferenceEquals(ta, fa1)) return 900101;
                    if (RegistrationStore.Attrs.TryGetValue(900102, out var fa2) && ReferenceEquals(ta, fa2)) return 900102;
                    if (RegistrationStore.Attrs.TryGetValue(900103, out var fa3) && ReferenceEquals(ta, fa3)) return 900103;
                    try { return ta.id; } catch { }
                }
            }
            catch { }
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
    public static void GridConsumePostfix(ProductionManager __instance)
    {
        float nowF = Time.unscaledTime; // v0.9.95-perf：与 Prefix 同窗快照（置位在末处 PoleCensus）
        bool doLog = nowF >= _nextHeavyLog;
        try
        {
            if (_rangeHijacked)
            {
                try
                {
                    var t = typeof(TerrainObject_Production_ElectricPole);
                    var fc = t.GetField("maxConnectRange", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    var fp = t.GetField("maxPowerRange", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (fc != null && !float.IsNaN(_savedConnectRange)) fc.SetValue(null, _savedConnectRange);
                    if (fp != null && !float.IsNaN(_savedPowerRange)) fp.SetValue(null, _savedPowerRange);
                    _rangeHijacked = false;
                }
                catch { }
            }
            try // v0.9.91-diag：重建后 IO 普查（只读+日志，零行为改动；EnableDiag 门控之外独立运行）
            {
                int postAO = -1, postPD = -1;
                try { var ao = TerrainObject_Production.ActiveObjects_Production; if (ao != null) postAO = ao.Count; } catch { }
                try { var pl = __instance != null ? __instance.productionDataList : null; if (pl != null) postPD = pl.Count; } catch { postPD = -1; }
                try { if (doLog) Plugin.L.LogInfo($"[TS][RebuildIO] post ActiveObjects={postAO} prodList={postPD}"); } catch { }
            }
            catch { }
            try { if (doLog) _nextHeavyLog = nowF + 5f; } catch { } // P4-B1：窗置位前提（原杆普查尾，保pre/post同窗节流语义）
            if (EnableDiag) // P4-B1：杆普查加门（EnableDiag恒false，常态跳过全表扫描）
            try // v0.9.91-diag：杆普查（只读+日志，零行为改动）
            {
                int emptyPoles = 0, totalPoles = 0;
                try
                {
                    var listC = TerrainObject_Production.ActiveObjects_Production;
                    if (listC != null)
                    {
                        for (int ci = 0; ci < listC.Count; ci++)
                        {
                            var cg = listC[ci];
                            if (cg == null) continue;
                            int caid = -1;
                            try { caid = GetClonedAttrId(cg); } catch { continue; }
                            if (caid != 125) continue;
                            totalPoles++;
                            try
                            {
                                object cpd = null;
                                try { var ctod = Reflect.Get(cg, "objectData"); if (ctod != null) cpd = Reflect.Get(ctod, "productionData"); } catch { }
                                if (cpd == null) continue;
                                int oc = CountOf(cpd, "outputProductionObjectList");
                                int cc = CountOf(cpd, "connectedProductionObjectList");
                                if (oc <= 0 && cc <= 0) emptyPoles++;
                            }
                            catch { }
                        }
                    }
                }
                catch { }
                try { if (doLog) Plugin.L.LogInfo($"[TS][PoleCensus] emptyPoles={emptyPoles}/total={totalPoles}"); } catch { }
            }
            catch { }
            if (!EnableDiag) return;
            // v0.9.9 对照实验：采样全部 Production 实例（含原版充电台/冰箱/杆子），对比克隆建筑 vs 原版的连接表
            string sb = "[TS] 电网重扫完成，全实例连接表:";
            var list = TerrainObject_Production.ActiveObjects_Production;
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                var g = list[i];
                if (g == null) continue;
                try
                {
                    object pd = null;
                    var tod = Reflect.Get(g, "objectData");
                    if (tod != null) pd = Reflect.Get(tod, "productionData");
                    if (pd == null) { sb += " [?.PD=null]"; continue; }
                    int aid = GetClonedAttrId(g);
                    sb += $" [{aid}:" + CountOf(pd, "inputProductionObjectList") + "/" + CountOf(pd, "outputProductionObjectList") + "/"
                        + CountOf(pd, "connectedProductionObjectList") + "/" + CountOf(pd, "inputProductionDataList") + "/"
                        + CountOf(pd, "outputProductionDataList") + "/" + CountOf(pd, "connectedProductionDataList") + "]";
                }
                catch { }
            }
            // ── v0.9.11 诊断探针：杆子表内容 + 900102/900103 判定链 + 距离取证（全部只读、不改逻辑，异常静默）──
            // v0.9.12 探针噪音收敛：仅当链上有 900102/900103 时输出 pole 段
            bool _hasCloneForPole = false;
            try
            {
                for (int _ci = 0; _ci < list.Count; _ci++)
                {
                    var _cg = list[_ci];
                    if (_cg == null) continue;
                    int _caid = -1;
                    try { _caid = GetClonedAttrId(_cg); } catch { }
                    if (_caid == 900102 || _caid == 900103) { _hasCloneForPole = true; break; }
                    try
                    {
                        var _cto = FindTerrainObject(_cg.transform);
                        if (_cto != null)
                        {
                            object _ca = null;
                            try { _ca = Reflect.Get(_cto, "attr"); } catch { }
                            if (_ca != null)
                            {
                                int _cid = AttrId(_ca);
                                if (_cid == 900102 || _cid == 900103) { _hasCloneForPole = true; break; }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
            if (_hasCloneForPole)
            try
            {
                // ① 杆子表内容：遍历 ElectricPole 实例（v0.9.11 判定修正：类型名或 attrId==125），取 output/connected 表前20 id，补打 type
                bool hasPole = false;
                for (int _pi = 0; _pi < list.Count; _pi++)
                {
                    var _pg = list[_pi];
                    if (_pg == null) continue;
                    if (!IsProbePole(_pg)) continue;
                    hasPole = true; break;
                }
                if (hasPole)
                {
                    int poleIdx = 0;
                    for (int _pi = 0; _pi < list.Count; _pi++)
                    {
                        var _pg = list[_pi];
                        if (_pg == null) continue;
                        if (!IsProbePole(_pg)) continue;
                        try
                        {
                            string _typeName = "";
                            try { _typeName = _pg.GetType().Name; } catch { _typeName = "<err>"; }
                            object _ppd = null;
                            var _tod2 = Reflect.Get(_pg, "objectData");
                            if (_tod2 != null) _ppd = Reflect.Get(_tod2, "productionData");
                            if (_ppd == null) { sb += $" [pole{poleIdx}(type={_typeName}):PD=null]"; poleIdx++; continue; }
                            var _outList = Reflect.Get(_ppd, "outputProductionObjectList") as Il2CppSystem.Collections.Generic.List<string>;
                            var _connList = Reflect.Get(_ppd, "connectedProductionObjectList") as Il2CppSystem.Collections.Generic.List<string>;
                            string _outStr = "<空>";
                            string _connStr = "<空>";
                            try
                            {
                                if (_outList != null && _outList.Count > 0)
                                {
                                    var _tmp = new System.Text.StringBuilder();
                                    int _lim = _outList.Count > 20 ? 20 : _outList.Count;
                                    for (int _k = 0; _k < _lim; _k++) { if (_k > 0) _tmp.Append(","); _tmp.Append(_outList[_k]); }
                                    _outStr = _tmp.ToString();
                                }
                            }
                            catch { }
                            try
                            {
                                if (_connList != null && _connList.Count > 0)
                                {
                                    var _tmp2 = new System.Text.StringBuilder();
                                    int _lim2 = _connList.Count > 20 ? 20 : _connList.Count;
                                    for (int _k2 = 0; _k2 < _lim2; _k2++) { if (_k2 > 0) _tmp2.Append(","); _tmp2.Append(_connList[_k2]); }
                                    _connStr = _tmp2.ToString();
                                }
                            }
                            catch { }
                            sb += $" [pole{poleIdx}(type={_typeName}):out={_outStr} conn={_connStr}]";
                        }
                        catch { }
                        poleIdx++;
                    }
                }
            }
            catch { }
            try
            {
                // ② 900102/900103 判定链取证：在 Production 表里各找实例，打印 chain + comps
                for (int _targetIdx = 0; _targetIdx < 2; _targetIdx++)
                {
                    int _targetId = _targetIdx == 0 ? 900102 : 900103;
                    TerrainObject_Production _padInst = null;
                    object _padPd = null;
                    object _padAttr = null;
                    int _padAttrId = -1;
                    string _padTypeName = "<未知>";
                    for (int _si = 0; _si < list.Count; _si++)
                    {
                        var _g2 = list[_si];
                        if (_g2 == null) continue;
                        int _aid2 = -1;
                        try { _aid2 = GetClonedAttrId(_g2); } catch { }
                        bool _isTarget = false;
                        try
                        {
                            var _to2 = FindTerrainObject(_g2.transform);
                            if (_to2 != null)
                            {
                                object _attr2 = null;
                                try { _attr2 = Reflect.Get(_to2, "attr"); } catch { }
                                if (_attr2 != null)
                                {
                                    if (RegistrationStore.Attrs.TryGetValue(_targetId, out var _our) && ReferenceEquals(_attr2, _our)) _isTarget = true;
                                    else if (AttrId(_attr2) == _targetId) _isTarget = true;
                                    if (_isTarget) { _padInst = _g2; _padAttr = _attr2; _padAttrId = AttrId(_attr2); try { _padTypeName = _to2.GetType().Name; } catch { } break; }
                                }
                            }
                            if (!_isTarget && _aid2 == _targetId)
                            {
                                var _to3 = FindTerrainObject(_g2.transform);
                                object _attr3 = null;
                                try { _attr3 = Reflect.Get(_to3, "attr"); } catch { }
                                _padInst = _g2; _padAttr = _attr3; _padAttrId = _attr3 != null ? AttrId(_attr3) : _targetId;
                                try { if (_to3 != null) _padTypeName = _to3.GetType().Name; else _padTypeName = _g2.GetType().Name; } catch { }
                                break;
                            }
                        }
                        catch { }
                    }
                    if (_padInst != null)
                    {
                        bool _pdNonNull = false;
                        int _prodType = -1;
                        try
                        {
                            var _tod3 = Reflect.Get(_padInst, "objectData");
                            object _pd3 = null;
                            if (_tod3 != null) _pd3 = Reflect.Get(_tod3, "productionData");
                            _padPd = _pd3;
                            _pdNonNull = _pd3 != null;
                        }
                        catch { }
                        try { if (_padAttr != null) _prodType = Convert.ToInt32(Reflect.Get(_padAttr, "productionObjectType")); } catch { }
                        try { if (_padTypeName == "<未知>") _padTypeName = _padInst.GetType().Name; } catch { }
                        sb += $" [{_targetId}chain: pd非空={_pdNonNull} attrId={_padAttrId} productionObjectType={_prodType} type={_padTypeName} active={_padInst.gameObject.activeInHierarchy}]";
                        // comps：GetComponentsInChildren<Component>(true) 去重前6
                        try
                        {
                            var _comps = _padInst.GetComponentsInChildren<Component>(true);
                            var _seen = new System.Collections.Generic.HashSet<string>();
                            var _sb2 = new System.Text.StringBuilder();
                            int _cnt = 0;
                            bool _hasBC = false, _hasStir = false;
                            if (_comps != null)
                            {
                                foreach (var _c in _comps)
                                {
                                    if (_c == null) continue;
                                    string _tn = "";
                                    try { _tn = _c.GetType().Name; } catch { continue; }
                                    if (_tn.Contains("BatteryCharger")) _hasBC = true;
                                    if (_tn.Contains("Stirling")) _hasStir = true;
                                    if (_seen.Contains(_tn)) continue;
                                    _seen.Add(_tn);
                                    if (_cnt > 0) _sb2.Append(",");
                                    _sb2.Append(_tn);
                                    _cnt++;
                                    if (_cnt >= 6) break;
                                }
                            }
                            string _compList = _sb2.Length > 0 ? _sb2.ToString() : "<空>";
                            sb += $" [{_targetId}comps: {_compList} 含BatteryCharger={_hasBC} 含Stirling={_hasStir}]";
                        }
                        catch { }
                        // ③ 距离取证：该 pad 到全部杆子的 min dist²（杆子判定用新判定）
                        try
                        {
                            var _padPos = _padInst.transform.position;
                            float _minD2 = float.MaxValue;
                            bool _foundPole = false;
                            for (int _pi2 = 0; _pi2 < list.Count; _pi2++)
                            {
                                var _pole = list[_pi2];
                                if (_pole == null) continue;
                                if (!IsProbePole(_pole)) continue;
                                _foundPole = true;
                                var _dp = _pole.transform.position - _padPos;
                                float _d2 = _dp.x * _dp.x + _dp.y * _dp.y;
                                if (_d2 < _minD2) _minD2 = _d2;
                            }
                            if (_foundPole) sb += $" [{_targetId}dist2min={_minD2:F1} 阈6²=36 阈16²=256]";
                            else sb += $" [{_targetId}dist2min=无杆子]";
                        }
                        catch { }
                    }
                    else
                    {
                        sb += $" [{_targetId}chain: 未找到实例]";
                        sb += $" [{_targetId}dist2min=无实例]";
                    }
                }
            }
            catch { }
            if (!_log103Dist)
            {
                try
                {
                    TerrainObject_Production_StirlingGenerator sg103 = null;
                    var stirList103 = TerrainObject_Production.ActiveObjects_Production;
                    if (stirList103 != null)
                    {
                        for (int _si = 0; _si < stirList103.Count; _si++)
                        {
                            var _sg = stirList103[_si] as TerrainObject_Production_StirlingGenerator;
                            if (_sg == null) continue;
                            bool _isBio = false;
                            try
                            {
                                var _to = FindTerrainObject(_sg.transform);
                                if (_to != null)
                                {
                                    object _a = null;
                                    try { _a = Reflect.Get(_to, "attr"); } catch { }
                                    if (_a != null)
                                    {
                                        if (RegistrationStore.Attrs.TryGetValue(900103, out var _our) && ReferenceEquals(_a, _our)) _isBio = true;
                                        else if (AttrId(_a) == 900103) _isBio = true;
                                    }
                                }
                            }
                            catch { }
                            if (_isBio) { sg103 = _sg; break; }
                        }
                    }
                    if (sg103 != null)
                    {
                        var _padPos2 = sg103.transform.position;
                        float _minD2_2 = float.MaxValue;
                        bool _foundPole2 = false;
                        var _prodList2 = TerrainObject_Production.ActiveObjects_Production;
                        if (_prodList2 != null)
                        {
                            for (int _pi3 = 0; _pi3 < _prodList2.Count; _pi3++)
                            {
                                var _pole2 = _prodList2[_pi3];
                                if (_pole2 == null) continue;
                                if (!IsProbePole(_pole2)) continue;
                                _foundPole2 = true;
                                var _dp2 = _pole2.transform.position - _padPos2;
                                float _d2_2 = _dp2.x * _dp2.x + _dp2.y * _dp2.y;
                                if (_d2_2 < _minD2_2) _minD2_2 = _d2_2;
                            }
                        }
                        if (_foundPole2)
                        {
                            Plugin.L.LogInfo($"[TS] 900103dist2min={_minD2_2:F1} 阈6²=36 阈16²=256 active={sg103.gameObject.activeInHierarchy}");
                        }
                        else
                        {
                            Plugin.L.LogInfo($"[TS] 900103dist2min=无杆子 active={sg103.gameObject.activeInHierarchy}");
                        }
                        _log103Dist = true;
                    }
                }
                catch { }
            }
            LogThrottled(sb);
        }
        catch { }
    }

    private static bool IsProbePole(TerrainObject_Production g)
    {
        try { if (g.GetType().Name.Contains("ElectricPole")) return true; } catch { }
        try { if (GetClonedAttrId(g) == 125) return true; } catch { }
        try
        {
            var to = FindTerrainObject(g.transform);
            if (to != null)
            {
                object attr = null;
                try { attr = Reflect.Get(to, "attr"); } catch { }
                if (attr != null && AttrId(attr) == 125) return true;
            }
        }
        catch { }
        return false;
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

    // ── v0.9.11 延迟重试探针：Stirling 表归属 + Prefab 查询链（延迟重试，只读，中文前缀）──
    private static void ProbeOnce()
    {
        if (!EnableDiag) return;
        try
        {
            if (Time.unscaledTime < 3f) return;
            if (_stirProbed && _prefabProbed) return;
            // 1) Stirling 表归属：遍历 ActiveObjects_Production，打 attrId（独立 try/catch，失败可重试）
            if (!_stirProbed)
            {
                bool _stirSuccess = false;
                try
                {
                    var stirList = TerrainObject_Production.ActiveObjects_Production;
                    if (stirList == null)
                    {
                        Plugin.L.LogInfo("[TS] Stirling表实例: ActiveObjects_Production=null");
                        _stirSuccess = true;
                    }
                    else if (stirList.Count == 0)
                    {
                        // 辅助判据：生产表里是否存在 900103 实例（幽灵实例检测）
                        bool _hasBioInProd = false;
                        try
                        {
                            var _prodList = TerrainObject_Production.ActiveObjects_Production;
                            if (_prodList != null)
                            {
                                for (int _pi = 0; _pi < _prodList.Count; _pi++)
                                {
                                    var _g = _prodList[_pi];
                                    if (_g == null) continue;
                                    int _aid = -1;
                                    try { _aid = GetClonedAttrId(_g); } catch { }
                                    if (_aid == 900103) { _hasBioInProd = true; break; }
                                    try
                                    {
                                        var _to = FindTerrainObject(_g.transform);
                                        if (_to != null)
                                        {
                                            object _attr = null;
                                            try { _attr = Reflect.Get(_to, "attr"); } catch { }
                                            if (_attr != null && AttrId(_attr) == 900103) { _hasBioInProd = true; break; }
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                        catch { }
                        Plugin.L.LogInfo($"[TS] Stirling表实例: <空> (Count=0, 生产表900103存在={_hasBioInProd}, t={Time.unscaledTime:F1}s)");
                        if (Time.unscaledTime > 20f) _stirSuccess = true;
                    }
                    else
                    {
                        int hit = 0;
                        for (int si = 0; si < stirList.Count && hit < 20; si++)
                        {
                            var sg = stirList[si];
                            if (sg == null) continue;
                            try
                            {
                                var to = FindTerrainObject(sg.transform);
                                object attr = null;
                                try { attr = Reflect.Get(to, "attr"); } catch { }
                                int aid = -1;
                                if (attr != null)
                                {
                                    if (RegistrationStore.Attrs.TryGetValue(PadId, out var our) && ReferenceEquals(attr, our)) aid = PadId;
                                    else try { aid = AttrId(attr); } catch { aid = -1; }
                                }
                                Plugin.L.LogInfo($"[TS] Stirling表实例: attrId={aid} type={sg.GetType().Name}");
                                hit++;
                            }
                            catch { }
                        }
                        if (hit == 0) Plugin.L.LogInfo($"[TS] Stirling表实例: <空> (Count={stirList.Count}, 无有效实例, t={Time.unscaledTime:F1}s)");
                        _stirSuccess = true;
                    }
                }
                catch (Exception e) { Plugin.L.LogInfo($"[TS] ProbeOnce Stirling 异常: {e.Message.Split('\n')[0]}"); }
                if (_stirSuccess) _stirProbed = true;
            }
            // 2) Prefab 查询链取证：900102/900103 与 126 对照 + defaultTerrainObjectPrefab（独立 try/catch，失败可重试）
            if (!_prefabProbed)
            {
                bool _prefabSuccess = false;
                try
                {
                    object gc = null;
                    try { gc = typeof(GameController).GetProperty("instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.GetValue(null); } catch { }
                    System.Reflection.MethodInfo mi = null;
                    try { mi = typeof(GameController).GetMethod("GetTerrainObjectPrefabById"); } catch { }
                    bool _hasResult = false;
                    void ProbePrefab(int id)
                    {
                        try
                        {
                            if (gc == null || mi == null) { Plugin.L.LogInfo($"[TS] Prefab探针 id={id}: GameController/method 不可用"); return; }
                            var prefab = mi.Invoke(gc, new object[] { id }) as GameObject;
                            if (prefab == null) { Plugin.L.LogInfo($"[TS] Prefab探针 id={id}: 返回 null"); return; }
                            string name = prefab.name ?? "<null>";
                            string rootName = "";
                            try { rootName = prefab.transform.root != null ? prefab.transform.root.name : "<no root>"; } catch { rootName = "<err>"; }
                            string compList = "";
                            bool hasBC = false, hasStir = false, hasPole = false;
                            try
                            {
                                var comps = prefab.GetComponentsInChildren<Component>(true);
                                var seen = new System.Collections.Generic.HashSet<string>();
                                var sb2 = new System.Text.StringBuilder();
                                int cnt = 0;
                                foreach (var c in comps)
                                {
                                    if (c == null) continue;
                                    string tn = c.GetType().Name;
                                    if (tn.Contains("BatteryCharger")) hasBC = true;
                                    if (tn.Contains("Stirling")) hasStir = true;
                                    if (tn.Contains("ElectricPole")) hasPole = true;
                                    if (seen.Contains(tn)) continue;
                                    seen.Add(tn);
                                    if (cnt > 0) sb2.Append(",");
                                    sb2.Append(tn);
                                    cnt++;
                                    if (cnt >= 10) break;
                                }
                                compList = sb2.ToString();
                            }
                            catch { compList = "<err>"; }
                            Plugin.L.LogInfo($"[TS] Prefab探针 id={id}: name={name} root={rootName} comps=[{compList}] 含BatteryCharger={hasBC} 含Stirling={hasStir} 含ElectricPole={hasPole}");
                            _hasResult = true;
                        }
                        catch (Exception e) { Plugin.L.LogInfo($"[TS] Prefab探针 id={id} 异常: {e.Message.Split('\n')[0]}"); }
                    }
                    ProbePrefab(900102);
                    ProbePrefab(900103);
                    ProbePrefab(126);
                    // defaultTerrainObjectPrefab 静态字段
                    try
                    {
                        var fld = typeof(GameController).GetField("defaultTerrainObjectPrefab", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                        if (fld != null)
                        {
                            var val = fld.GetValue(null) as GameObject;
                            if (val != null) Plugin.L.LogInfo($"[TS] defaultTerrainObjectPrefab: name={val.name}");
                            else Plugin.L.LogInfo("[TS] defaultTerrainObjectPrefab: null");
                        }
                    }
                    catch { }
                    if (gc != null && mi != null && _hasResult) _prefabSuccess = true;
                    else if (Time.unscaledTime > 20f) _prefabSuccess = true;
                }
                catch (Exception e) { Plugin.L.LogInfo($"[TS] ProbeOnce Prefab 异常: {e.Message.Split('\n')[0]}"); if (Time.unscaledTime > 20f) _prefabSuccess = true; }
                if (_prefabSuccess) _prefabProbed = true;
            }
        }
        catch { }
    }

    /// <summary>充电台克隆盘判定：attr.id == 900102 即放行（v0.9.72：不再要求组件类型名含 BatteryCharger——存量实例组件为基类 TerrainObject_Production，类型门曾导致 EnsureContainer 永不执行）。</summary>
    private static bool IsChargerPad(TerrainObject_Production g)
    {
        try
        {
            // P1-2 快路：编译期直访 g.attr；仅 null 时回退旧爬链＋反射
            try
            {
                var ta = g != null ? g.attr : null;
                if (ta != null)
                {
                    bool fast = false;
                    try { fast = (RegistrationStore.Attrs.TryGetValue(PadId, out var ourFast) && ReferenceEquals(ta, ourFast)) || ta.id == PadId; } catch { }
                    if (fast && !_warnedTypeMiss)
                    {
                        _warnedTypeMiss = true;
                        try
                        {
                            string tnFast = g.GetType().Name ?? "?";
                            if (tnFast.IndexOf("BatteryCharger", StringComparison.Ordinal) < 0)
                                Plugin.L.LogInfo($"[TS] ChargerPad 判定: 900102 组件为基类型 '{tnFast}'（存量实例），按 attr 放行容器初始化");
                        }
                        catch { }
                    }
                    return fast;
                }
            }
            catch { }
            var to = FindTerrainObject(g.transform);
            if (to == null) return false;
            object attr = null;
            try { attr = Reflect.Get(to, "attr"); } catch { }
            if (attr == null) return false;
            bool isPad = false;
            try { isPad = (RegistrationStore.Attrs.TryGetValue(PadId, out var our) && ReferenceEquals(attr, our)) || AttrId(attr) == PadId; } catch { }
            if (isPad && !_warnedTypeMiss)
            {
                _warnedTypeMiss = true;
                try
                {
                    string tn = g.GetType().Name ?? "?";
                    if (tn.IndexOf("BatteryCharger", StringComparison.Ordinal) < 0)
                        Plugin.L.LogInfo($"[TS] ChargerPad 判定: 900102 组件为基类型 '{tn}'（存量实例），按 attr 放行容器初始化");
                }
                catch { }
            }
            return isPad;
        }
        catch { return false; }
    }

    /// <summary>一次性容器微调：8×8 + 标题 + 槽数 4（v0.9.71：格子 4×4→8×8，并行槽数保持 4；原版充电台默认 2 槽 2×1 容器）。</summary>
    private static void EnsureContainer(TerrainObject_Production g)
    {
        long key = 0;
        try { key = KeyOf(g); } catch { try { key = g.GetHashCode(); } catch { } } // P-键统一：TerrainObject经KeyOf取GetInstanceID
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
        try { Reflect.Set(inv, "inventorySize", new Vector2Int(8, 8)); } catch { }
        try { Reflect.Set(inv, "inventorySizeX", 8); } catch { }
        try { Reflect.Set(inv, "inventorySizeY", 8); } catch { }
        try { Reflect.Set(g, "totalBatterySoltNumber", 4); } catch { }
        Plugin.L.LogInfo($"[TS] 充电台盘初始化: 8×8 槽数=4 size=({inv.inventorySizeX}x{inv.inventorySizeY}) PD表={(_pdTablesFixed ? "已重建" : "完整")}");
    }

    private static bool _pdTablesFixed; // 一次性日志用

    /// <summary>ProductionData 六连接表完整性保障（反编译 ElectricPole.RefreshElectricConnection 0x1809BD350：
    /// 对 input/output/connected 三组列表做 List.Add 无守卫，任一 null 即 NRE「已隔离」→ 存档重建电网时建筑加载异常）。
    /// 原版 ctor new 六表；克隆/池化/读档时序路径可能缺——缺则补 Il2Cpp List（字段名以 dump.cs 为准）。
    /// 返回补齐字段数。</summary>
    private static int EnsurePdTables(object pd)
    {
        int n = 0;
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
                    n++;
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
                    n++;
                }
            }
            catch { }
        }
        return n;
    }

    /// <summary>×4 倍率 prefix：pd 是 900102 传送盘或 126 原版充电台、且供电含生物能 → sufficient ×4（postfix 恢复）。</summary>
    public static bool ChargerUpdatePrefix(ProductionData productionData, float addedTime)
    {
        try
        {
            if (productionData == null) return true;
            if (!IsBoostPd(productionData))
            {
                if (!_warnedHit) { _warnedHit = true; Plugin.L.LogWarning("[TS] ×4 诊断: UpdateBatteryCharger 触发但 IsBoostPd=false（非 900102/126）"); }
                return true;
            }
            if (!IsBioGenSupplied(productionData))
            {
                if (!_warnedHit) { _warnedHit = true; Plugin.L.LogWarning($"[TS] ×4 诊断: IsPadPd=true 但供电判定无生物能（距离未命中） gridSupply={GridFactorDiag(productionData)}"); }
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

    // ═══ v0.9.73 状态字位域上限屏蔽（反编译实证 subagent/data/battery-state-cap-decompile.md）═══
    // batteryChargeStateTemp uint @0xE0 按 2bit/槽打包共 16 槽；Set 用 if(0xf<index)throw，Get 用 if(index<0x10)；
    // UpdateBatteryCharger 传的是格子槽位号（sizeX*y+x），8×8 下 ≥16 即崩且掀翻当次充电。
    // 充电循环（ChargeBattery）与 index 无关 → 越界 Set/Get 直接吞掉，16+ 格无 LED（渲染本就只管前 totalBatterySoltNumber 槽）。
    public static bool BatteryStateSetPrefix(int __0, int __1)
    {
        try { if (__0 < 0 || __0 > 15) return false; } catch { }
        return true;
    }

    public static bool BatteryStateGetPrefix(int __0, ref int __result)
    {
        try { if (__0 < 0 || __0 > 15) { __result = 0; return false; } } catch { }
        return true;
    }

    /// <summary>×4 目标判定：900102 传送盘（克隆引用或 id）或 126 原版充电台。</summary>
    private static bool IsBoostPd(ProductionData pd)
    {
        try
        {
            var attr = pd.terrainObjectAttr;
            if (attr == null) return false;
            if (RegistrationStore.Attrs.TryGetValue(PadId, out var our) && ReferenceEquals(attr, our)) return true;
            int id = AttrId(attr);
            return id == PadId || id == VanillaChargerId;
        }
        catch { return false; }
    }

    /// <summary>供电含生物能（900103）：50m 距离检测（09-05 更新删除联网列表字段后唯一路径）。</summary>
    private static bool IsBioGenSupplied(ProductionData pd)
    {
        try
        {
            // 09-05 更新已删除 connectedElectricGeneratorList：原联网列表优先路径移除，改走距离检测；gridSupplyFactor 仅记诊断日志（见 ChargerUpdatePrefix），不参门。
            // 兜底：距离检测（真实路由尚未建立时）
            var to = pd.terrainObjectTemp;
            if (to == null) return false;
            var pos = to.transform.position;
            var list = TerrainObject_Production.ActiveObjects_Production;
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
                if (dp.x * dp.x + dp.y * dp.y <= 50f * 50f) return true;
            }
            return false;
        }
        catch { return false; }
    }

    /// <summary>gridSupplyFactor 只读观察（09-05 新增）：读取失败记 n/a，绝不参门、不抛异常。</summary>
    private static string GridFactorDiag(ProductionData pd)
    {
        try { return Convert.ToSingle(Reflect.Get(pd, "gridSupplyFactor")).ToString("F2"); } catch { return "n/a"; }
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

    // ═══ v0.9.17 治本贴图重钉：双入口 postfix 在 TerrainObject.Init 之后用 prefab MOD 贴图覆盖模板贴图 ═══
    // ── v0.9.21 R11-4 Build 探针 prefix（一次性诊断）──
    public static void BuildTerrainObjectPrefix(int __0, GameController __instance)
    {
        if (_buildDiag) return;
        _buildDiag = true;
        try
        {
            bool attrReg = RegistrationStore.Attrs.ContainsKey(900102);
            bool prefabReg = RegistrationStore.Prefabs.ContainsKey(900102);
            bool attrDicHas = TryInDic(__instance != null ? __instance.terrainObjectAttrDic : null, 900102);
            bool prefabDicHas = TryInDic(__instance != null ? __instance.terrainObjectPrefabDic : null, 900102);
            Plugin.L.LogInfo($"[TS] Build探针: instOk={__instance != null} attrReg={attrReg} prefabReg={prefabReg} attrDicHas={attrDicHas} prefabDicHas={prefabDicHas}");
        }
        catch { }
    }

    private static bool TryInDic(object d, int id)
    {
        try { if (d == null) return false; var m = d.GetType().GetMethod("ContainsKey"); if (m == null) return false; return (bool)m.Invoke(d, new object[] { id }); } catch { return false; }
    }

    // ── P1-3：900102 electricConsuming 放置即盖（一次性语义前移到注册点，本方法供 OnEnable/Build/Add 复用）──
    private static void StampConsumingFlag(TerrainObject t)
    {
        try
        {
            if (t == null) return;
            var a = t.attr;
            if (a == null) return;
            int sid = -1;
            try
            {
                if (RegistrationStore.Attrs.TryGetValue(900102, out var our) && ReferenceEquals(a, our)) sid = 900102;
                else sid = a.id;
            }
            catch { return; }
            if (sid == 900102) { try { a.electricConsuming = true; } catch { } }
        }
        catch { }
    }

    public static void BuildTerrainObjectPostfix(TerrainObject __result) { if (!RegistrarState.Done) return; try { FixCloneSprites(__result); StampConsumingFlag(__result); if (EnableDiag) ScaleDiagLog(__result, "Build"); TeleportBindingManager.OnPlaced(__result); } catch { } }
    public static void AddTerrainObjectPostfix(TerrainObject __result) { if (!RegistrarState.Done) return; try { FixCloneSprites(__result); StampConsumingFlag(__result); if (EnableDiag) ScaleDiagLog(__result, "Add"); TeleportBindingManager.OnPlaced(__result); } catch { } }

    // ═══ v0.9.23 诊断：缩放追踪（不改值，只日志，定位几秒后缩小真凶）═══
    // SO兼容：postfix void 方法，首行 return 只跳过自体逻辑、不影响游戏原生（非 bool prefix，无跳过原生语义）。
    public static void ScaleGuardPostfix(TerrainObject __instance)
    {
        if (IsCloning) return;
        if (_onEnableDepth > 4) return;
        try
        {
            _onEnableDepth++;
            try { if (EnableDiag) ScaleDiagLog(__instance, "Init"); TeleportBindingManager.OnPlaced(__instance); } catch { }
        }
        finally { try { _onEnableDepth--; } catch { } }
    }
    private static int _diagCount = 0;
    private static void ScaleDiagLog(TerrainObject t, string tag)
    {
        if (!EnableDiag) return;
        if (t == null) return;
        try
        {
            var attr = t.attr;
            if (attr == null) return;
            int id = 0;
            try
            {
                if (RegistrationStore.Attrs.TryGetValue(900101, out var a1) && ReferenceEquals(attr, a1)) id = 900101;
                else if (RegistrationStore.Attrs.TryGetValue(900102, out var a2) && ReferenceEquals(attr, a2)) id = 900102;
                else if (RegistrationStore.Attrs.TryGetValue(900103, out var a3) && ReferenceEquals(attr, a3)) id = 900103;
                else { int r = attr.id; if (r == 900101 || r == 900102 || r == 900103) id = r; }
            }
            catch { }
            if (id == 0) return;
            if (_diagCount > 200) return; // 限流 200 条
            _diagCount++;
            var tr = t.transform;
            Vector3 ls = tr != null ? tr.localScale : new Vector3(-1, -1, -1);
            float sf = -1f; Vector3 lst = new Vector3(-1, -1, -1);
            try { var o = Reflect.Get(t, "scalefloat"); if (o != null) sf = System.Convert.ToSingle(o); } catch { }
            try { var o2 = Reflect.Get(t, "localScaleTemp"); if (o2 is Vector3 v) lst = v; } catch { }
            string spInfo = "noSR";
            try
            {
                var sr = t.GetComponentInChildren<SpriteRenderer>(true);
                if (sr != null && sr.sprite != null) spInfo = $"{sr.sprite.name} {sr.sprite.rect.width}x{sr.sprite.rect.height} ppu={sr.sprite.pixelsPerUnit:F1} bounds={sr.bounds.size.x:F2}x{sr.bounds.size.y:F2}";
                else if (sr != null) spInfo = "srNoSprite";
            }
            catch { }
            Plugin.L.LogInfo($"[TS][ScaleDiag][{tag}#{_diagCount}] id={id} go={t.name} scale={ls.x:F3},{ls.y:F3},{ls.z:F3} sf={sf:F3} lst={lst.x:F3},{lst.y:F3},{lst.z:F3} sp={spInfo} t={UnityEngine.Time.unscaledTime:F1}s");
        }
        catch { }
    }
    private static float _lastScaleFix = -999f;
    private static void EnsureScales()
    {
        try
        {
            float now = Time.unscaledTime;
            if (now - _lastScaleFix < 1.0f) return; // 1s 诊断节流
            _lastScaleFix = now;
            // 诊断：每秒对 _knownClones + ActiveObjects 各扫一次，只日志不修复
            try
            {
                for (int i = 0; i < _knownClones.Count; i++)
                {
                    var o = _knownClones[i] as Component;
                    if (o == null) continue;
                    var t = FindTerrainObject(o.transform) as TerrainObject;
                    if (t == null) continue;
                    var a = t.attr; if (a == null) continue;
                    int aid = a.id; if (aid != 900101 && aid != 900102 && aid != 900103) continue;
                    ScaleDiagLog(t, "TickKnown");
                    if (_diagCount > 200) break;
                }
            }
            catch { }
            try
            {
                var list = TerrainObject_Production.ActiveObjects_Production;
                if (list != null)
                {
                    int logged = 0;
                    for (int i = 0; i < list.Count; i++)
                    {
                        var g = list[i]; if (g == null) continue;
                        var to = FindTerrainObject(g.transform) as TerrainObject;
                        if (to == null) continue;
                        var a = to.attr; if (a == null) continue;
                        int aid = a.id; if (aid != 900101 && aid != 900102 && aid != 900103) continue;
                        // 限每轮最多2条，避免刷屏
                        if (logged < 2) { ScaleDiagLog(to, "TickActive"); logged++; }
                    }
                }
            }
            catch { }
        }
        catch { }
    }
    private static void FixCloneSprites(TerrainObject t)
    {
        if (t == null) return;
        TerrainObjectAttr attr = null; try { attr = t.attr; } catch { }
        if (attr == null) return;
        int id = 0;
        try
        {
            if (RegistrationStore.Attrs.TryGetValue(900101, out var a1) && ReferenceEquals(attr, a1)) id = 900101;
            else if (RegistrationStore.Attrs.TryGetValue(900102, out var a2) && ReferenceEquals(attr, a2)) id = 900102;
            else if (RegistrationStore.Attrs.TryGetValue(900103, out var a3) && ReferenceEquals(attr, a3)) id = 900103;
            else { int r = attr.id; if (r == 900101 || r == 900102 || r == 900103) id = r; }
        }
        catch { }
        if (id == 0) return;
        // 优先 BodyCache（正确 ppu），回退 Icon Cache 仅防空
        Sprite cacheSp = null;
        bool hasBody = SpriteInjector.BodyCache.TryGetValue(id, out cacheSp) && cacheSp != null;
        if (!hasBody && (!SpriteInjector.Cache.TryGetValue(id, out cacheSp) || cacheSp == null)) return;
        SpriteRenderer[] instSrs = null;
        try { instSrs = t.GetComponentsInChildren<SpriteRenderer>(true); } catch { return; }
        if (instSrs == null || instSrs.Length == 0) return;
        int n = instSrs.Length;
        bool changed = false;
        // 900102 绿层即时屏蔽（存量/新建首次 Init 后也可能残留）
        if (id == 900102)
        {
            try
            {
                for (int kk = 0; kk < instSrs.Length; kk++)
                {
                    var s2 = instSrs[kk];
                    if (s2 == null) continue;
                    string sn2 = s2.name ?? "";
                    if (sn2.Contains("ChargingState") && s2.enabled) { s2.enabled = false; changed = true; }
                }
            }
            catch { }
        }
        int limit = n > 8 ? 8 : n;
        for (int i = 0; i < limit; i++)
        {
            var sr = instSrs[i];
            if (sr == null) continue;
            try
            {
                if (sr.sprite != null && !ReferenceEquals(sr.sprite, cacheSp))
                {
                    bool curIsBody = sr.sprite.name != null && sr.sprite.name.EndsWith("_Body");
                    bool cacheIsBody = cacheSp.name != null && cacheSp.name.EndsWith("_Body");
                    if (curIsBody && !cacheIsBody) continue; // 已是 Body 时不被 Icon 覆写
                    if (curIsBody && cacheIsBody && Math.Abs(sr.sprite.pixelsPerUnit - cacheSp.pixelsPerUnit) < 0.1f) continue;
                    sr.sprite = cacheSp; changed = true;
                }
            }
            catch { }
        }
        if (changed) Plugin.L.LogInfo($"[TS] 克隆贴图重钉: id={id} srs={n} ppu={cacheSp.pixelsPerUnit:F1}");
    }

    /// <summary>P-键统一：实例键唯一入口（_initKeys/_pdFixed 写键/查键必经此函数）。
    /// Unity 对象（TerrainObject/SortingGroup 等）取 GetInstanceID；纯数据对象（如 ProductionData）无该方法→沿用 GetHashCode 旧式。</summary>
    private static long KeyOf(object o)
    {
        try
        {
            if (o is UnityEngine.Object u)
            {
                try { if (u != null) return (long)u.GetInstanceID(); } catch { }
            }
        }
        catch { }
        try { if (o != null) return (long)o.GetHashCode(); } catch { }
        try { return (long)o.GetType().GetHashCode(); } catch { return 0L; }
    }

    internal static void ResetForIdentity()
    {
        try
        {
            _initKeys.Clear();
            _pdFixed.Clear();
            _knownClones.Clear();
            _clonesScanDone = false;
            _pdTablesCompleted = false;
            _consumingSwept = false;
            _stirProbed = false;
            _prefabProbed = false;
        }
        catch { }
    }

    internal static void PruneCaches()
    {
        try
        {
            /* _knownClones：以后端ActiveObjects_Production/Stirling两表活体集合＋gameObject空判为基准Remove僵尸；_initKeys/_pdFixed：以活体实例GetInstanceID集合求交集修枝 */
            try
            {
                var live = new System.Collections.Generic.HashSet<object>();
                try
                {
                    var plist = TerrainObject_Production.ActiveObjects_Production;
                    if (plist != null) for (int i = 0; i < plist.Count; i++) { try { var x = plist[i]; if (x != null) live.Add(x); } catch { } }
                }
                catch { }
                try
                {
                    var slist = TerrainObject_Production.ActiveObjects_Production;
                    if (slist != null) for (int i = 0; i < slist.Count; i++) { try { var x = slist[i]; if (x != null) live.Add(x); } catch { } }
                }
                catch { }
                var dead = new System.Collections.Generic.List<object>();
                try
                {
                    var arr = _knownClones.ToArray();
                    foreach (var o in arr)
                    {
                        try
                        {
                            if (o == null) { dead.Add(o); continue; }
                            var c = o as Component;
                            if (c == null) continue; // 非Component条目：保守不动
                            bool gone = false;
                            try { if (c.gameObject == null) gone = true; } catch { gone = true; }
                            if (!gone) { try { if (!live.Contains(o)) gone = true; } catch { } }
                            if (gone) dead.Add(o);
                        }
                        catch { }
                    }
                }
                catch { }
                foreach (var d in dead) { try { _knownClones.Remove(d); } catch { } }
            }
            catch { }
            try
            {
                var ids = new System.Collections.Generic.HashSet<long>();
                var pdKeys = new System.Collections.Generic.HashSet<long>();
                try
                {
                    var plist = TerrainObject_Production.ActiveObjects_Production;
                    if (plist != null) for (int i = 0; i < plist.Count; i++)
                    {
                        try
                        {
                            var g = plist[i];
                            if (g == null) continue;
                            try { ids.Add(KeyOf(g)); } catch { }
                            object pd = null;
                            try { var tod = Reflect.Get(g, "objectData"); if (tod != null) pd = Reflect.Get(tod, "productionData"); } catch { }
                            if (pd != null) { try { pdKeys.Add(KeyOf(pd)); } catch { } }
                        }
                        catch { }
                    }
                }
                catch { }
                try
                {
                    var slist = TerrainObject_Production.ActiveObjects_Production;
                    if (slist != null) for (int i = 0; i < slist.Count; i++)
                    {
                        try
                        {
                            var s = slist[i];
                            if (s == null) continue;
                            try { ids.Add(KeyOf(s)); } catch { }
                            object pd = null;
                            try { var tod = Reflect.Get(s, "objectData"); if (tod != null) pd = Reflect.Get(tod, "productionData"); } catch { }
                            if (pd != null) { try { pdKeys.Add(KeyOf(pd)); } catch { } }
                        }
                        catch { }
                    }
                }
                catch { }
                try { _initKeys.RemoveWhere(k => !ids.Contains(k)); } catch { }
                try { _pdFixed.RemoveWhere(k => !pdKeys.Contains(k)); } catch { }
            }
            catch { }
        }
        catch { }
    }
}