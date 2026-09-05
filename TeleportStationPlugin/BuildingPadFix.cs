using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace TeleportStationPlugin;

/// <summary>
/// v0.9.0：建筑圆盘（900102）层钉 v2 —— ActiveObjects 实例定位 + SortingGroup 钉。
/// 定案（2026-08-31，dump.cs + Ghidra 反编译）：
///   1. 建筑排序真实写入对象 = TerrainObject.m_sortingGroup（SortingGroup 组件，基类字段 0x40），不是 SR 层；
///      P1 的 PadLayerGuard（拦 SR setter）与 PadLayerPin（钉 SR layer）全失效根因 = 钉错对象。
///   2. 游戏写 SortingGroup 仅两条路径：Init（初始化）/ RestoreRootSortingOrderFactory（factory 模式还原），
///      两处都写 set_sortingOrder(缓存0x4C) + set_sortingLayerID(缓存0x50)；UpdateLifted/TerrainObjectUpdate 不写排序
///      （同层 y 深度排序是渲染管线规则，非游戏代码逐帧改写）。
///   3. 方案：ActiveObjects_Production 活列表定位实例（900102 克隆自斯特林，列表含实例）→
///      取 m_sortingGroup → 钉 sortingLayerID=FX_BG（层 FX_BG 低于玩家 Character → 玩家永远盖盘，盘永不盖玩家）。
///      "玩家/车在盘中心以北被盖"（同层 y-sort）因层不同而彻底消除。
///   4. 0.5s 周期复查：防 factory 模式进出时 RestoreRoot 把层写回建筑默认。
/// </summary>
/// <summary>
/// v0.9.4：实例定位改 ActiveObjects_Production（基类活列表）——900102 克隆源已切充电台 126（TerrainObject_Production_Inventory 系，
/// 实例在 Production 列表）；旧档斯特林组件盘同样在 Production 列表（Stirling 也是 Production 子类）→ 单一列表全覆盖。
/// </summary>
public static class BuildingPadFix
{
    private const int PadId = 900102;
    private static float _lastScan = -1f;
    private static int _fxBgId = -1;
    private static readonly System.Collections.Generic.HashSet<long> _pinned = new(); // 层钉日志去重（写回重钉不再刷屏）
    private static readonly Dictionary<int, byte> _classified = new(); // P1-5 分类缓存：1=是盘/2=非盘（key=GetInstanceID，未命中跑一次IsPadInstance后记表）
    private static readonly Dictionary<int, SortingGroup> _sgCache = new(); // P1-5 盘SortingGroup句柄缓存（使用前判空，被销毁删键回退重取）
    private static float _nextFullScan = -1f; // P1-5 10s整表重扫兜底（到期清空_classified强制重判，不清_sgCache）

    /// <summary>由 RegistrationProbe.Update 每帧调用（内部 1.0s 节流——实例级微秒开销）。</summary>
    public static void Tick()
    {
        try
        {
            float now = Time.unscaledTime;
            if (now - _lastScan < 1.0f) return;
            _lastScan = now;
            if (_fxBgId < 0) _fxBgId = SortingLayer.NameToID("FX_BG");
            if (_fxBgId <= 0) return; // FX_BG 未注册（内置层，理论不存在）

            // P1-5 10s整表重扫兜底：清空_classified强制重判（不清_sgCache，按需失效）
            try
            {
                if (_nextFullScan < 0f) _nextFullScan = now + 10f;
                else if (now >= _nextFullScan) { _nextFullScan = now + 10f; try { _classified.Clear(); } catch { } }
            }
            catch { }

            // P2-2：吃ChargerPadFix共享快照（对侧先到已刷则复用，零拷贝；null/过期则自己刷一份，不崩）。节流相位与下述判定逻辑不动。
            TerrainObject_Production[] snap = null;
            try { snap = ChargerPadFix.GetSharedProdSnapshot(); } catch { snap = null; }
            if (snap == null)
            {
                try
                {
                    var live = TerrainObject_Production.ActiveObjects_Production;
                    if (live == null) return;
                    int c = live.Count;
                    var tmp = new TerrainObject_Production[c];
                    for (int k = 0; k < c; k++) { try { tmp[k] = live[k]; } catch { } }
                    snap = tmp;
                }
                catch { return; }
                if (snap == null) return;
            }
            var list = snap;
            for (int i = 0; i < list.Length; i++)
            {
                var g = list[i];
                if (g == null) continue;
                int key = 0;
                try { key = g.GetInstanceID(); }
                catch { continue; }
                bool isPad;
                try
                {
                    if (_classified.TryGetValue(key, out byte cls))
                    {
                        if (cls == 2) continue; // 已知非盘：O(1)跳过
                        isPad = true; // 已知是盘：跳过IsPadInstance直达钉层逻辑
                    }
                    else
                    {
                        isPad = IsPadInstance(g); // 未命中：跑一次原判定后记表
                        try { _classified[key] = isPad ? (byte)1 : (byte)2; }
                        catch { }
                        if (!isPad)
                        {
                            try { _sgCache.Remove(key); } // 按需失效：非盘不留旧句柄
                            catch { }
                            continue;
                        }
                    }
                }
                catch { continue; }
                SortingGroup sg = null;
                try
                {
                    if (!_sgCache.TryGetValue(key, out sg) || sg == null)
                    {
                        sg = Reflect.Get(g, "m_sortingGroup") as SortingGroup; // 缓存未命中/句柄失效：回退重取
                        if (sg == null) continue; // 本轮取不到不缓存，下个0.5s周期重试
                        try { _sgCache[key] = sg; }
                        catch { }
                    }
                }
                catch { continue; }
                if (sg == null) continue;
                try
                {
                    if (sg.sortingLayerID != _fxBgId)
                    {
                        sg.sortingLayerID = _fxBgId;
                        long k = 0;
                        try { k = (long)sg.GetInstanceID(); } catch { try { k = sg.GetHashCode(); } catch { } } // P-键统一：SortingGroup取GetInstanceID
                        if (_pinned.Add(k)) Plugin.L.LogInfo($"[TS] 建筑盘层钉 v2: SortingGroup→FX_BG（id={_fxBgId}）");
                    }
                }
                catch (Exception e) { Plugin.L.LogWarning($"[TS] 建筑盘层钉异常: {e.Message.Split('\n')[0]}"); }
            }
        }
        catch { }
    }

    /// <summary>实例判定：TerrainObject 组件的 attr.id == 900102（引用/ID 双保险）。</summary>
    private static bool IsPadInstance(TerrainObject_Production g)
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

    internal static void ResetForIdentity()
    {
        try
        {
            _classified.Clear();
            _sgCache.Clear();
            _pinned.Clear();
        }
        catch { }
    }

    internal static void PruneCaches()
    {
        try
        {
            // _classified按活体修枝（活体=ActiveObjects_Production；_sgCache已有使用前空判自愈，此处顺带清空调亡句柄；_pinned为日志去重键，不动以免重刷日志）
            var live = new System.Collections.Generic.HashSet<int>();
            try
            {
                var list = TerrainObject_Production.ActiveObjects_Production;
                if (list != null) for (int i = 0; i < list.Count; i++)
                {
                    try { var g = list[i]; if (g != null) live.Add(g.GetInstanceID()); } catch { }
                }
            }
            catch { }
            try
            {
                var dead = new System.Collections.Generic.List<int>();
                foreach (var kv in _classified) { try { if (!live.Contains(kv.Key)) dead.Add(kv.Key); } catch { } }
                foreach (var k in dead) { try { _classified.Remove(k); } catch { } }
            }
            catch { }
            try
            {
                var deadSg = new System.Collections.Generic.List<int>();
                foreach (var kv in _sgCache)
                {
                    try { if (kv.Value == null || !live.Contains(kv.Key)) deadSg.Add(kv.Key); } catch { }
                }
                foreach (var k in deadSg) { try { _sgCache.Remove(k); } catch { } }
            }
            catch { }
        }
        catch { }
    }
}