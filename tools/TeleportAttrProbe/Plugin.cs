using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using ZedZoneShared;

namespace TeleportAttrProbe;

/// <summary>
/// 建造源表侦查探针 v0.3.0（内测，发布前 .disabled）：
/// 目标：找到 ConstructionPanel.LoadConstructionMenu 的「建造源表」（源头注册点）。
/// 功能：
///   T+15s/40s/80s dump TerrainObjectAttrAry / TerrainObjectAttrFixManager 实例；
///   每 15s 扫描全部含 TerrainObjectAttr 元素的集合字段（MonoBehaviour 全类型去重）；
///   dump ItemManager.itemAttrDic 关键 id 对照；
///   hook LoadConstructionMenu（postfix 打印 genre + gridContent 卡片计数）。
/// 全部逻辑静态类；计时用 unscaledDeltaTime（建造菜单打开=游戏暂停）。
/// </summary>
[BepInPlugin("com.zedzone.tool.teleportattrprobe", "TeleportAttrProbe", "0.3.8")]
public class Plugin : BasePlugin
{
    internal static Plugin Instance;
    internal static ManualLogSource L;
    internal static string PluginDir;

    public override void Load()
    {
        Instance = this;
        L = Log;
        PluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
        SharedLog.Initialize(
            (m) => Log.LogError(m),
            (m) => Log.LogWarning(m),
            (m) => Log.LogInfo(m));

        Probe.PatchAll();
        var driver = AddComponent<ProbeDriver>();
        Probe.Driver = driver;
        Log.LogInfo("[TAP] 源表侦查探针 v0.3.8 已加载");
    }
}

public class ProbeDriver : MonoBehaviour
{
    private float _t;
    private float _nextScan = 12f;
    private bool _aryA, _aryB, _aryC;

    // v0.3.8：detailIcon 时序观察（点击标记 → 0/0.5/1.5s 采样）
    private float _observeAt1 = -1f, _observeAt2 = -1f, _observeAt3 = -1f;
    private int _observeId;

    private void Update()
    {
        _t += Time.unscaledDeltaTime;
        if (!_aryA && _t > 15f) { _aryA = true; Probe.DumpAttrAry("T+15s"); }
        if (!_aryB && _t > 40f) { _aryB = true; Probe.DumpAttrAry("T+40s"); Probe.DumpItemDic("T+40s"); }
        if (!_aryC && _t > 80f) { _aryC = true; Probe.DumpAttrAry("T+80s"); }
        if (_t >= _nextScan)
        {
            _nextScan = _t + 15f;
            Probe.ScanSourceCollections($"scan T+{_t:F0}s");
        }
        if (_observeAt1 > 0f && _t >= _observeAt1) { _observeAt1 = -1f; Patches.DumpDetailIconState($"T+{_t:F0}s 点击后0s"); }
        if (_observeAt2 > 0f && _t >= _observeAt2) { _observeAt2 = -1f; Patches.DumpDetailIconState($"T+{_t:F0}s 点击后0.5s"); }
        if (_observeAt3 > 0f && _t >= _observeAt3) { _observeAt3 = -1f; Patches.DumpDetailIconState($"T+{_t:F0}s 点击后1.5s"); }
    }

    internal void ScheduleDetailObserve()
    {
        _observeAt1 = _t + 0.1f;
        _observeAt2 = _t + 0.6f;
        _observeAt3 = _t + 1.6f;
    }
}

public static class Probe
{
    internal static ProbeDriver Driver;
    internal static void Line(string s) => Plugin.L.LogInfo("[TAP] " + s);

    internal static void PatchAll()
    {
        var h = new Harmony("com.zedzone.tool.teleportattrprobe");
        try
        {
            var m = AccessTools.Method(typeof(ConstructionPanel), "LoadConstructionMenu");
            if (m != null)
                h.Patch(m, postfix: new HarmonyMethod(typeof(Patches).GetMethod(nameof(Patches.LoadPostfix), BindingFlags.Public | BindingFlags.Static)));
            // 建造源表方法：GameController.GetAvailableTerrainObjectAttrsByTechGenre
            var avail = AccessTools.Method(typeof(GameController), "GetAvailableTerrainObjectAttrsByTechGenre");
            if (avail != null)
                h.Patch(avail, postfix: new HarmonyMethod(typeof(Patches).GetMethod(nameof(Patches.AvailPostfix), BindingFlags.Public | BindingFlags.Static)));
            // v0.3.8：GetTerrainObjectAttrById 观察（详情查询信号→detailIcon 时序采样）
            var byId = AccessTools.Method(typeof(GameController), "GetTerrainObjectAttrById");
            if (byId != null)
                h.Patch(byId, postfix: new HarmonyMethod(typeof(Patches).GetMethod(nameof(Patches.ByIdObserve), BindingFlags.Public | BindingFlags.Static)));
        }
        catch (Exception e) { Line($"hook 异常: {e.Message.Split('\n')[0]}"); }
    }

    internal static int AttrId(TerrainObjectAttr a)
    {
        try { var o = Reflect.Get(a, "id"); return o == null ? -1 : Convert.ToInt32(o); }
        catch { return -1; }
    }

    internal static string S(object o)
    {
        if (o == null) return "<null>";
        try { string s = o.ToString(); return s.Length > 60 ? s.Substring(0, 60) + "…" : s; }
        catch { return "<ToString异常>"; }
    }

    internal static int ListCount(object list)
    {
        if (list == null) return -1;
        try { var p = list.GetType().GetProperty("Count"); return p == null ? -1 : Convert.ToInt32(p.GetValue(list)); }
        catch { return -1; }
    }

    internal static object ListItem(object list, int i)
    {
        if (list == null) return null;
        try
        {
            var t = list.GetType();
            var p = t.GetProperty("Item");
            if (p != null) return p.GetValue(list, new object[] { i });
            var m = t.GetMethod("get_Item");
            return m == null ? null : m.Invoke(list, new object[] { i });
        }
        catch { return null; }
    }

    // ── TerrainObjectAttrAry ──
    internal static void DumpAttrAry(string when)
    {
        try
        {
            var ars = Resources.FindObjectsOfTypeAll<TerrainObjectAttrAry>();
            int n = ars == null ? 0 : ars.Count;
            if (n == 0) { Line($"AttrAry[{when}]：实例=0"); return; }
            foreach (var ar in ars)
            {
                var arr = Reflect.Get(ar, "terrainObjectAttrAry");
                int len = ListCount(arr);
                var sb = new StringBuilder($"AttrAry[{when}] obj='{ar.name}' len={len} ids=");
                if (len > 0)
                {
                    for (int i = 0; i < Math.Min(len, 80); i++)
                    {
                        var a = ListItem(arr, i) as TerrainObjectAttr;
                        if (a == null) { sb.Append('?').Append(','); continue; }
                        sb.Append(AttrId(a)).Append(',');
                    }
                    sb.Length--;
                }
                else sb.Append("<empty>");
                Line(sb.ToString());
            }
        }
        catch (Exception e) { Line($"AttrAry[{when}] 异常: {e.Message.Split('\n')[0]}"); }
    }

    // ── GameController 建筑字典状态（建造源表）──
    internal static void DumpGameAttrDic(string when)
    {
        try
        {
            var gc = typeof(GameController).GetProperty("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
            if (gc == null) { Line($"GameDic[{when}] instance=null"); return; }
            var dic = Reflect.Get(gc, "terrainObjectAttrDic");
            if (dic == null) { Line($"GameDic[{when}] terrainObjectAttrDic=null"); return; }
            try
            {
                var cntProp = dic.GetType().GetProperty("Count");
                int cnt = cntProp == null ? -1 : Convert.ToInt32(cntProp.GetValue(dic));
                var contains = dic.GetType().GetMethod("ContainsKey");
                var sb = new StringBuilder($"GameDic[{when}] count={cnt} 关键id:");
                foreach (int id in new[] { 120, 125, 150, 126, 130, 37, 900101, 900102, 900103 })
                {
                    bool has = false;
                    try { has = contains != null && (bool)contains.Invoke(dic, new object[] { id }); } catch { }
                    sb.Append($" {id}={(has ? "Y" : "N")}");
                }
                Line(sb.ToString());
            }
            catch (Exception e) { Line($"GameDic[{when}] 读取异常: {e.Message.Split('\n')[0]}"); }
            // techGenre 字典
            var gd = Reflect.Get(gc, "terrainObjectAttrTechGenreDic");
            if (gd != null)
            {
                try
                {
                    var cntProp = gd.GetType().GetProperty("Count");
                    Line($"GameDic[{when}] terrainObjectAttrTechGenreDic count={(cntProp == null ? -1 : Convert.ToInt32(cntProp.GetValue(gd)))}");
                }
                catch { }
            }
        }
        catch (Exception e) { Line($"GameDic[{when}] 异常: {e.Message.Split('\n')[0]}"); }
    }

    // ── ItemManager.itemAttrDic 对照 ──
    internal static void DumpItemDic(string when)
    {
        try
        {
            var mgr = typeof(ItemManager).GetProperty("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
            if (mgr == null) { Line($"ItemDic[{when}] instance=null"); return; }
            var dic = Reflect.Get(mgr, "itemAttrDic");
            if (dic == null) { Line($"ItemDic[{when}] itemAttrDic=null"); return; }
            try
            {
                var cntProp = dic.GetType().GetProperty("Count");
                int cnt = cntProp == null ? -1 : Convert.ToInt32(cntProp.GetValue(dic));
                var contains = dic.GetType().GetMethod("ContainsKey");
                var sb = new StringBuilder($"ItemDic[{when}] count={cnt} 关键id:");
                foreach (int id in new[] { 120, 125, 150, 900101, 900102, 900103, 37, 108 })
                {
                    bool has = false;
                    try { has = contains != null && (bool)contains.Invoke(dic, new object[] { id }); } catch { }
                    sb.Append($" {id}={(has ? "Y" : "N")}");
                }
                Line(sb.ToString());
            }
            catch (Exception e) { Line($"ItemDic[{when}] 读取异常: {e.Message.Split('\n')[0]}"); }
        }
        catch (Exception e) { Line($"ItemDic[{when}] 异常: {e.Message.Split('\n')[0]}"); }
    }

    // ── 建造源表扫描：全部 MonoBehaviour 类型的含 TerrainObjectAttr 集合字段 ──
    private static readonly HashSet<string> _scannedTypes = new();

    internal static void ScanSourceCollections(string when)
    {
        try
        {
            var seen = new HashSet<string>();
            var monos = Resources.FindObjectsOfTypeAll<MonoBehaviour>();
            foreach (var m in monos)
            {
                if (m == null) continue;
                var t = m.GetType();
                if (seen.Contains(t.FullName) || !_scannedTypes.Add(t.FullName)) continue;
                seen.Add(t.FullName);
                FieldInfo[] fields;
                try { fields = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance); }
                catch { continue; }
                foreach (var f in fields)
                {
                    if (f.Name.StartsWith("Native") || f.Name is "isWrapped" or "pooledPtr") continue;
                    Type ft = f.FieldType;
                    bool isAttrCollection = false;
                    string elemDesc = "";
                    if (ft.IsArray && ft.GetElementType() != null && ft.GetElementType().Name.Contains("TerrainObjectAttr"))
                    { isAttrCollection = true; elemDesc = ft.GetElementType().Name + "[]"; }
                    else if (ft.IsGenericType)
                    {
                        var ga = ft.GetGenericArguments();
                        if (ga.Length == 1 && ga[0].Name.Contains("TerrainObjectAttr"))
                        { isAttrCollection = true; elemDesc = ga[0].Name; }
                    }
                    if (ft.Name.Contains("TerrainObjectAttr")) { isAttrCollection = true; elemDesc = ft.Name; }
                    if (!isAttrCollection) continue;
                    object val;
                    try { val = f.GetValue(m); } catch { continue; }
                    if (val == null) continue;
                    int len = ListCount(val);
                    Line($"{when} 源表候选: {t.Name}.{f.Name} ({elemDesc}) len={len}");
                    if (len > 0 && len < 400)
                    {
                        var sb = new StringBuilder("  ids=");
                        for (int i = 0; i < Math.Min(len, 80); i++)
                        {
                            var item = ListItem(val, i);
                            if (item == null) { sb.Append('?').Append(','); continue; }
                            // 元素本身可能是 attr 或含 id 的对象
                            int id = -1;
                            try
                            {
                                var idObj = Reflect.Get(item, "id");
                                if (idObj == null) idObj = Reflect.Get(item, "terrainObjectId");
                                if (idObj != null) id = Convert.ToInt32(idObj);
                            }
                            catch { }
                            sb.Append(id).Append(',');
                        }
                        sb.Length--;
                        Line(sb.ToString());
                    }
                }
            }
            Line($"{when} 源表扫描完成（新扫类型 {seen.Count}，累计 {_scannedTypes.Count}）");
        }
        catch (Exception e) { Line($"{when} 源表扫描异常: {e.Message.Split('\n')[0]}"); }
    }

    // ── v0.3.6 卡片结构差异 dump（定位「点不动」：原版 vs 我们的事件层/几何差异）──
    internal static void DumpCardDiff(string when)
    {
        try
        {
            var inst = typeof(ConstructionPanel).GetProperty("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as Component;
            if (inst == null) return;
            var gc = Reflect.Get(inst, "gridContent") as RectTransform;
            if (gc == null) return;
            for (int i = 0; i < gc.childCount; i++)
            {
                var c = gc.GetChild(i);
                if (c == null || !c.name.StartsWith("Card_")) continue;
                var ui = c.GetComponent<ConstructionItemCardUI>();
                if (ui == null) continue;
                var sb = new StringBuilder($"{when} 卡 '{c.name}': ");
                var rt = c.GetComponent<RectTransform>();
                if (rt != null) sb.Append($"rect={rt.rect.width:F0}x{rt.rect.height:F0} pos=({rt.position.x:F0},{rt.position.y:F0})");
                sb.Append($" act={c.gameObject.activeInHierarchy}");
                // 组件清单
                var comps = new List<string>();
                try
                {
                    var cs = c.GetComponents<Component>();
                    foreach (var comp in cs)
                    {
                        if (comp == null) continue;
                        string tn = comp.GetType().Name;
                        if (!comps.Contains(tn)) comps.Add(tn);
                    }
                }
                catch { }
                sb.Append(" comps=[").Append(string.Join(",", comps)).Append("]");
                // 关键 Image 状态
                string ImageInfo(Image img)
                {
                    if (img == null) return "<null>";
                    string sn = img.sprite == null ? "-" : (img.sprite.name.Length > 0 ? img.sprite.name : "(ours)");
                    return $"en={img.enabled} ray={img.raycastTarget} a={img.color.a:F2} spr={sn}";
                }
                sb.Append($" bg={ImageInfo(ui.bgImage)}");
                sb.Append($" border={ImageInfo(ui.borderImage)}");
                sb.Append($" icon={ImageInfo(ui.iconImage)}");
                sb.Append($" tint={ImageInfo(ui.selectTintImage)}");
                sb.Append($" dot={ImageInfo(ui.availabilityDot)}");
                Line(sb.ToString());
            }
        }
        catch (Exception e) { Line($"{when} 卡片差异 dump 异常: {e.Message.Split('\n')[0]}"); }
    }

    // ── 卡片图标实际引用（原版 vs 我们的卡）──
    internal static void DumpCardSprites(string when)
    {
        try
        {
            var inst = typeof(ConstructionPanel).GetProperty("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as Component;
            if (inst == null) return;
            var gc = Reflect.Get(inst, "gridContent") as RectTransform;
            if (gc == null) return;
            for (int i = 0; i < gc.childCount; i++)
            {
                var c = gc.GetChild(i);
                if (c == null || !c.name.StartsWith("Card_")) continue;
                var ui = c.GetComponent<ConstructionItemCardUI>();
                if (ui == null) continue;
                string sp = "<null>";
                string st = "";
                try
                {
                    var img = ui.iconImage;
                    if (img != null)
                    {
                        if (img.sprite != null) sp = img.sprite.name;
                        st = $"enabled={img.enabled} colorA={img.color.a:F2}";
                    }
                    else st = "<iconImage=null>";
                }
                catch { }
                Line($"{when} 卡 '{c.name}' sprite={sp} {st}");
            }
        }
        catch (Exception e) { Line($"{when} 卡片 sprite dump 异常: {e.Message.Split('\n')[0]}"); }
    }
}

public static class Patches
{
    public static void LoadPostfix(object __0)
    {
        try
        {
            Probe.Line($"LoadConstructionMenu(genre={__0}) 帧={Time.frameCount}");
            Probe.DumpGameAttrDic("Load后");
            Probe.DumpCardDiff("Load后");
        }
        catch { }
    }

    public static void AvailPostfix(object __0, object __result)
    {
        try
        {
            if (__result == null) { Probe.Line($"GetAvailable(genre={__0}) → null"); return; }
            int n = Probe.ListCount(__result);
            var sb = new System.Text.StringBuilder($"GetAvailable(genre={__0}) → {n} 项: ");
            for (int i = 0; i < Math.Min(n, 30); i++)
            {
                var a = Probe.ListItem(__result, i) as TerrainObjectAttr;
                if (a == null) { sb.Append('?').Append(','); continue; }
                sb.Append(Probe.AttrId(a)).Append(',');
            }
            sb.Length -= sb.Length > 0 && sb[sb.Length - 1] == ',' ? 1 : 0;
            Probe.Line(sb.ToString());
        }
        catch (Exception e) { Probe.Line($"GetAvailable postfix 异常: {e.Message.Split('\n')[0]}"); }
    }

    public static void GetMainPostfix(object __0, object __result)
    {
        try
        {
            string rn = "<null>";
            try { if (__result is Sprite sp && sp != null) rn = sp.name; }
            catch { }
            Probe.Line($"ModSpriteRegistry.GetMain(id={__0}) → {rn}");
        }
        catch { }
    }

    // ── v0.3.8：详情查询信号 → detailIcon 时序采样 ──
    public static void ByIdObserve(object __0, object __result)
    {
        try
        {
            int id = -1;
            try { id = Convert.ToInt32(__0); }
            catch { }
            Probe.Line($"OBSERVE GetTerrainObjectAttrById(id={id}) → {(__result == null ? "<null>" : "有")} 帧={Time.frameCount}");
            if (id >= 900101 && id <= 900103 && Probe.Driver != null)
            {
                Probe.Driver.ScheduleDetailObserve();
            }
        }
        catch { }
    }

    // ── v0.3.7 纯观察：OnCardClicked 只读 + detailIcon 全状态 dump ──
    public static void CardClickObserve(object __0, object __1)
    {
        try
        {
            string cardName = "<?>";
            try
            {
                var go = Reflect.Get(__0, "gameObject") as GameObject;
                if (go != null) cardName = go.name;
            }
            catch { }
            int id = -1;
            try { id = Convert.ToInt32(__1); }
            catch { }
            Probe.Line($"OBSERVE OnCardClicked: card='{cardName}' id={id} 帧={Time.frameCount}");
            DumpDetailIconState($"OBSERVE 点击后");
        }
        catch (Exception e) { Probe.Line($"OBSERVE OnCardClicked 异常: {e.Message.Split('\n')[0]}"); }
    }

    internal static void DumpDetailIconState(string when)
    {
        try
        {
            var inst = typeof(ConstructionPanel).GetProperty("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as ConstructionPanel;
            if (inst == null) { Probe.Line($"{when} ConstructionPanel=null"); return; }
            var di = inst.detailIcon;
            if (di == null) { Probe.Line($"{when} detailIcon=<null>"); return; }
            var rt = di.rectTransform;
            string spr = di.sprite == null ? "<null>" : (di.sprite.name.Length > 0 ? di.sprite.name : "(ours)");
            string parents = "";
            var p = rt.parent;
            for (int i = 0; i < 5 && p != null; i++) { parents = p.name + "/" + parents; p = p.parent; }
            string ptr = "";
            try { ptr = "0x" + di.Pointer.ToString("X"); } catch { }
            Probe.Line($"{when} detailIcon: ptr={ptr} spr={spr} en={di.enabled} ray={di.raycastTarget} a={di.color.a:F2} " +
                       $"rect={rt.rect.width:F0}x{rt.rect.height:F0} pos=({rt.position.x:F0},{rt.position.y:F0}) " +
                       $"parent={parents} parentAct={rt.parent?.gameObject.activeInHierarchy}");
            // detailRoot 状态
            try
            {
                var root = Reflect.Get(inst, "detailRoot") as RectTransform;
                if (root != null)
                    Probe.Line($"{when} detailRoot: act={root.gameObject.activeInHierarchy} rect={root.rect.width:F0}x{root.rect.height:F0}");
            }
            catch { }
            // 详情文字（当前显示的）
            try
            {
                var nt = Reflect.Get(inst, "detailNameText") as Text;
                if (nt != null) Probe.Line($"{when} detailName='{nt.text}'");
            }
            catch { }
        }
        catch (Exception e) { Probe.Line($"{when} detailIcon dump 异常: {e.Message.Split('\n')[0]}"); }
    }
}