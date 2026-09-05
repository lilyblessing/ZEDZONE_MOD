using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace GridObserver;

/// <summary>电网只读观察插件：只记录、零干预（任何地方不 return false、不写游戏字段）。</summary>
[BepInPlugin("com.zedzone.gridobserver", "GridObserver", "0.1.0")]
public class GridObserverPlugin : BasePlugin
{
    internal static ManualLogSource L;
    internal static ProductionManager LastMgr;
    internal static long DirtyCount;
    internal static float BucketStart = -99f;
    internal static int BucketCount;
    internal static readonly System.Collections.Generic.List<int> BucketIds = new();

    public override void Load()
    {
        L = Log;
        var h = new Harmony("com.zedzone.gridobserver");
        Patch(h, typeof(ProductionManager), "MarkElectricGridDirty", null, nameof(Hooks.DirtyPostfix), "MarkElectricGridDirty");
        Patch(h, typeof(ProductionManager), "ConsumeElectricGridDirtyFlag", nameof(Hooks.ConsumePrefix), nameof(Hooks.ConsumePostfix), "ConsumeElectricGridDirtyFlag");
        Patch(h, typeof(ProductionManager), "RebuildElectricGraph", nameof(Hooks.RebuildPrefix), nameof(Hooks.RebuildPostfix), "RebuildElectricGraph");
        PatchArg(h, "AddProductionData", nameof(Hooks.AddPostfix));
        PatchArg(h, "RemoveProductionData", nameof(Hooks.RemovePostfix));
        Patch(h, typeof(TerrainObject_Production), "OnEnable", null, nameof(Hooks.EnablePostfix), "TerrainObject_Production.OnEnable");
        try
        {
            ClassInjector.RegisterTypeInIl2Cpp<Beat>();
            var go = new GameObject("GridObserverBeat");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<Beat>();
            L.LogInfo(Pfx() + " [GO] 心跳已启动");
        }
        catch (Exception e) { L.LogWarning(Pfx() + " [GO] 心跳启动失败(仅缺心跳): " + OneLine(e)); }
        L.LogInfo(Pfx() + " [GO] loaded v0.1.0（只读观察）");
    }

    internal static void Patch(Harmony h, Type t, string m, string pre, string post, string label)
    {
        try
        {
            var mi = AccessTools.Method(t, m);
            if (mi == null) { L.LogWarning(Pfx() + " [GO] 挂钩失败(未找到): " + label); return; }
            h.Patch(mi, Prefix(pre), Postfix(post));
            L.LogInfo(Pfx() + " [GO] 已挂钩 " + label);
        }
        catch (Exception e) { L.LogWarning(Pfx() + " [GO] 挂钩异常 " + label + ": " + OneLine(e)); }
    }

    internal static void PatchArg(Harmony h, string m, string post)
    {
        try
        {
            var mi = AccessTools.Method(typeof(ProductionManager), m, new Type[] { typeof(ProductionData) });
            if (mi == null) { L.LogWarning(Pfx() + " [GO] 挂钩失败(未找到): ProductionManager." + m); return; }
            h.Patch(mi, null, Postfix(post));
            L.LogInfo(Pfx() + " [GO] 已挂钩 ProductionManager." + m);
        }
        catch (Exception e) { L.LogWarning(Pfx() + " [GO] 挂钩异常 ProductionManager." + m + ": " + OneLine(e)); }
    }

    internal static HarmonyMethod Prefix(string n) => n == null ? null : new HarmonyMethod(typeof(Hooks).GetMethod(n, BindingFlags.Public | BindingFlags.Static));
    internal static HarmonyMethod Postfix(string n) => n == null ? null : new HarmonyMethod(typeof(Hooks).GetMethod(n, BindingFlags.Public | BindingFlags.Static));

    internal static string Pfx() => $"[GO {DateTime.Now:HH:mm:ss} rt={Time.realtimeSinceStartup:F1} f={Time.frameCount}]";
    internal static string OneLine(Exception e) { try { return e.Message.Split('\n')[0]; } catch { return "?"; } }

    internal static int Cnt(object list)
    {
        try
        {
            if (list == null) return -1;
            var p = list.GetType().GetProperty("Count");
            if (p == null) return -1;
            return Convert.ToInt32(p.GetValue(list));
        }
        catch { return -1; }
    }

    internal static string Counts(ProductionManager mgr)
    {
        try
        {
            int ao = -1, pl = -1, grids = -1, edges = -1;
            try { var l = TerrainObject_Production.ActiveObjects_Production; if (l != null) ao = l.Count; } catch { }
            try { var l = mgr?.productionDataList; if (l != null) pl = l.Count; } catch { }
            try { var l = mgr?.electricGrids; if (l != null) grids = l.Count; } catch { }
            try { var l = mgr?.electricEdges; if (l != null) edges = l.Count; } catch { }
            return $"ao={ao} pl={pl} grids={grids} edges={edges}";
        }
        catch { return "ao=-1 pl=-1 grids=-1 edges=-1"; }
    }
}

/// <summary>全部 void prefix/postfix，只读+计数+日志，全包 try/catch，不 return false、不写游戏字段。</summary>
public static class Hooks
{
    public static void DirtyPostfix()
    {
        try { GridObserverPlugin.DirtyCount++; GridObserverPlugin.L.LogInfo(GridObserverPlugin.Pfx() + $" [GO][Dirty] n={GridObserverPlugin.DirtyCount}"); } catch { }
    }

    public static void ConsumePrefix(ProductionManager __instance)
    {
        try { GridObserverPlugin.L.LogInfo(GridObserverPlugin.Pfx() + " [GO][Consume] begin"); } catch { }
    }

    public static void ConsumePostfix(ProductionManager __instance)
    {
        try { GridObserverPlugin.L.LogInfo(GridObserverPlugin.Pfx() + " [GO][Consume] end"); } catch { }
    }

    public static void RebuildPrefix(ProductionManager __instance)
    {
        try { GridObserverPlugin.L.LogInfo(GridObserverPlugin.Pfx() + " [GO][Rebuild] pre " + GridObserverPlugin.Counts(__instance)); } catch { }
    }

    public static void RebuildPostfix(ProductionManager __instance)
    {
        try
        {
            GridObserverPlugin.LastMgr = __instance;
            GridObserverPlugin.L.LogInfo(GridObserverPlugin.Pfx() + " [GO][Rebuild] post " + GridObserverPlugin.Counts(__instance));
        }
        catch { }
    }

    public static void AddPostfix(ProductionManager __instance, ProductionData pd)
    {
        try
        {
            string shortId = "?"; int total = -1;
            try { var s = pd?.productionObjectId; shortId = string.IsNullOrEmpty(s) ? "?" : (s.Length <= 8 ? s : s.Substring(0, 8)); } catch { }
            try { var l = __instance?.productionDataList; if (l != null) total = l.Count; } catch { }
            GridObserverPlugin.L.LogInfo(GridObserverPlugin.Pfx() + $" [GO][Add] id={shortId} pl={total}");
        }
        catch { }
    }

    public static void RemovePostfix(ProductionManager __instance, ProductionData pd)
    {
        try
        {
            string shortId = "?"; int total = -1;
            try { var s = pd?.productionObjectId; shortId = string.IsNullOrEmpty(s) ? "?" : (s.Length <= 8 ? s : s.Substring(0, 8)); } catch { }
            try { var l = __instance?.productionDataList; if (l != null) total = l.Count; } catch { }
            GridObserverPlugin.L.LogInfo(GridObserverPlugin.Pfx() + $" [GO][Remove] id={shortId} pl={total}");
        }
        catch { }
    }

    public static void EnablePostfix(TerrainObject_Production __instance)
    {
        try
        {
            int id = -1;
            try { var a = __instance?.attr; if (a != null) id = a.id; } catch { id = -1; }
            float now = Time.realtimeSinceStartup;
            if (now - GridObserverPlugin.BucketStart >= 5f)
            {
                if (GridObserverPlugin.BucketCount > 0)
                {
                    string ids = "?";
                    try { ids = string.Join(",", GridObserverPlugin.BucketIds); } catch { }
                    GridObserverPlugin.L.LogInfo(GridObserverPlugin.Pfx() + $" [GO][Enable] 桶内n={GridObserverPlugin.BucketCount} ids={{{ids}}}");
                }
                GridObserverPlugin.BucketStart = now;
                GridObserverPlugin.BucketCount = 0;
                GridObserverPlugin.BucketIds.Clear();
            }
            GridObserverPlugin.BucketCount++;
            if (GridObserverPlugin.BucketIds.Count < 8) GridObserverPlugin.BucketIds.Add(id);
        }
        catch { }
    }
}

/// <summary>心跳：1s 节流只读快照。</summary>
public class Beat : MonoBehaviour
{
    private float _next;

    public void Update()
    {
        try
        {
            float now = Time.unscaledTime;
            if (now < _next) return;
            _next = now + 1f;
            string world = "?"; string pos = "?";
            try
            {
                var gc = GameController.instance;
                var pc = gc != null ? gc.playerCharacter : null;
                world = pc != null ? "True" : "False";
                if (pc != null) { var p = pc.transform.position; pos = $"{p.x:F1},{p.y:F1}"; }
            }
            catch { }
            string ao = "?", pl = "?", grids = "?", edges = "?";
            try { var l = TerrainObject_Production.ActiveObjects_Production; if (l != null) ao = l.Count.ToString(); } catch { }
            try
            {
                var m = GridObserverPlugin.LastMgr;
                if (m != null)
                {
                    try { var l = m.productionDataList; if (l != null) pl = l.Count.ToString(); } catch { }
                    try { var l = m.electricGrids; if (l != null) grids = l.Count.ToString(); } catch { }
                    try { var l = m.electricEdges; if (l != null) edges = l.Count.ToString(); } catch { }
                }
            }
            catch { }
            GridObserverPlugin.L.LogInfo(GridObserverPlugin.Pfx() + $" [GO][Beat] world={world} pos={pos} ao={ao} pl={pl} grids={grids} edges={edges}");
        }
        catch { }
    }
}
