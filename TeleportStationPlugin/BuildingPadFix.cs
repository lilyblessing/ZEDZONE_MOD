using System;
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
///   3. 方案：ActiveObjects_StirlingGenerator 活列表定位实例（900102 克隆自斯特林，列表含实例）→
///      取 m_sortingGroup → 钉 sortingLayerID=FX_BG（层 FX_BG 低于玩家 Character → 玩家永远盖盘，盘永不盖玩家）。
///      "玩家/车在盘中心以北被盖"（同层 y-sort）因层不同而彻底消除。
///   4. 0.5s 周期复查：防 factory 模式进出时 RestoreRoot 把层写回建筑默认。
/// </summary>
public static class BuildingPadFix
{
    private const int PadId = 900102;
    private static float _lastScan = -1f;
    private static int _fxBgId = -1;

    /// <summary>由 RegistrationProbe.Update 每帧调用（内部 0.5s 节流——实例级微秒开销）。</summary>
    public static void Tick()
    {
        try
        {
            float now = Time.unscaledTime;
            if (now - _lastScan < 0.5f) return;
            _lastScan = now;
            if (_fxBgId < 0) _fxBgId = SortingLayer.NameToID("FX_BG");
            if (_fxBgId <= 0) return; // FX_BG 未注册（内置层，理论不存在）

            var list = TerrainObject_Production_StirlingGenerator.ActiveObjects_StirlingGenerator;
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                var g = list[i];
                if (g == null || !IsPadInstance(g)) continue;
                var sg = Reflect.Get(g, "m_sortingGroup") as SortingGroup;
                if (sg == null) continue;
                try
                {
                    if (sg.sortingLayerID != _fxBgId)
                    {
                        sg.sortingLayerID = _fxBgId;
                        Plugin.L.LogInfo($"[TS] 建筑盘层钉 v2: SortingGroup→FX_BG（id={_fxBgId}）");
                    }
                }
                catch (Exception e) { Plugin.L.LogWarning($"[TS] 建筑盘层钉异常: {e.Message.Split('\n')[0]}"); }
            }
        }
        catch { }
    }

    /// <summary>实例判定：TerrainObject 组件的 attr.id == 900102（引用/ID 双保险）。</summary>
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