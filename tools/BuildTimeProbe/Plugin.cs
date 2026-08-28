using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using ZedZoneShared;

namespace TeleportAttrProbe;

/// <summary>
/// v0.6.27 建造时间诊断探针 —— 运行时验证 Σ/2 公式 + 定位 statTimeValueText 写入路径。
/// 独立 DLL，不入库。用法：放入 BepInEx/plugins/ 运行一次，查看 LogInfo 输出。
/// </summary>
[BepInPlugin("com.zedzone.teleport.attrprobe", "TeleportAttrProbe", "0.0.1")]
public class Plugin : BasePlugin
{
    internal static ManualLogSource L;
    public override void Load()
    {
        L = Log;
        Log.LogInfo("[Probe] TeleportAttrProbe v0.0.1 已加载");
        AddComponent<ProbeRunner>();
    }
}

public class ProbeRunner : MonoBehaviour
{
    private float _timer = 25f;
    private bool _done;
    private int _frameCount;

    private void Update()
    {
        if (_done) return;
        _timer -= Time.unscaledDeltaTime;
        if (_timer > 0f) return;
        _done = true;
        RunProbe();
    }

    private void RunProbe()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[Probe] ===== 建造时间诊断 =====");

        // ── 1. 遍历所有 TerrainObjectAttr ──
        var attrs = Resources.FindObjectsOfTypeAll<TerrainObjectAttr>();
        var targets = new HashSet<int> { 120, 125, 150, 900101, 900102, 900103 };
        // 也扫描电池充电台和冰箱（需要运行时确认 id）
        foreach (var a in attrs)
        {
            if (a == null) continue;
            var idObj = Reflect.Get(a, "id");
            if (idObj == null) continue;
            int id = Convert.ToInt32(idObj);
            if (!targets.Contains(id) && id != 40 && id != 41)
            {
                // 补充：扫描所有建筑，寻找"电池充电台"和"冰箱"的中文名
                var cn = Reflect.Get(a, "chineseName") as string;
                if (cn != null && (cn.Contains("电池充电") || cn.Contains("冰箱") || cn.Contains("充电台")))
                    targets.Add(id);
            }
        }

        sb.AppendLine($"[Probe] 目标 id 集合: [{string.Join(",", targets)}]");

        foreach (var a in attrs)
        {
            if (a == null) continue;
            var idObj = Reflect.Get(a, "id");
            if (idObj == null) continue;
            int id = Convert.ToInt32(idObj);
            if (!targets.Contains(id)) continue;

            string cn = Reflect.Get(a, "chineseName") as string ?? "?";
            string en = Reflect.Get(a, "englishName") as string ?? "?";
            string sp = Reflect.Get(a, "spriteName") as string ?? "?";

            sb.AppendLine($"\n--- id={id} '{cn}' ({en}) sprite={sp} ---");

            // recipeData
            var rd = Reflect.Get(a, "recipeData") as object;
            if (rd == null) { sb.AppendLine("  recipeData=null"); continue; }

            // craftTime
            var ctObj = Reflect.Get(rd, "craftTime");
            float craftTime = ctObj != null ? Convert.ToSingle(ctObj) : -1f;
            sb.AppendLine($"  recipeData.craftTime = {craftTime}");

            // recipeItems
            var items = Reflect.Get(rd, "recipeItems");
            if (items == null) { sb.AppendLine("  recipeItems=null"); continue; }

            float sum = 0f;
            int count = 0;
            try
            {
                var countProp = items.GetType().GetProperty("Count");
                count = countProp != null ? Convert.ToInt32(countProp.GetValue(items)) : -1;
                sb.AppendLine($"  recipeItems.Count = {count}");

                for (int i = 0; i < count; i++)
                {
                    var item = GetListItem(items, i);
                    if (item == null) continue;
                    int itemId = Convert.ToInt32(Reflect.Get(item, "itemId") ?? -1);
                    float itemNum = Convert.ToSingle(Reflect.Get(item, "itemNumber") ?? 0f);
                    sb.AppendLine($"    [{i}] itemId={itemId} itemNumber={itemNum}");
                    sum += itemNum;
                }
            }
            catch (Exception e) { sb.AppendLine($"  recipeItems 遍历异常: {e.Message}"); }

            sb.AppendLine($"  Σ itemNumber = {sum}");
            sb.AppendLine($"  ceil(Σ/2) = {Math.Ceiling(sum / 2f)}");

            // ── 2. 调用 GetTotalMaterialNumber ──
            try
            {
                var method = typeof(BuildInfoPanel).GetMethod("GetTotalMaterialNumber",
                    BindingFlags.Public | BindingFlags.Static);
                if (method != null)
                {
                    object recipeData = rd;
                    var result = method.Invoke(null, new[] { recipeData });
                    sb.AppendLine($"  GetTotalMaterialNumber() = {result}");
                }
                else sb.AppendLine("  GetTotalMaterialNumber 方法未找到");
            }
            catch (Exception e) { sb.AppendLine($"  GetTotalMaterialNumber 异常: {e.Message.Split('\n')[0]}"); }

            // ── 3. 调用 GetBuildRealSeconds ──
            try
            {
                var method = typeof(BuildInfoPanel).GetMethod("GetBuildRealSeconds",
                    BindingFlags.Public | BindingFlags.Static);
                if (method != null)
                {
                    object recipeData = rd;
                    var result = method.Invoke(null, new[] { recipeData });
                    sb.AppendLine($"  GetBuildRealSeconds() = {result}");
                }
                else sb.AppendLine("  GetBuildRealSeconds 方法未找到");
            }
            catch (Exception e) { sb.AppendLine($"  GetBuildRealSeconds 异常: {e.Message.Split('\n')[0]}"); }

            // ── 4. 调用 BuildTimeFormat(3f) ──
            try
            {
                var method = typeof(BuildInfoPanel).GetMethod("BuildTimeFormat",
                    BindingFlags.Public | BindingFlags.Static);
                if (method != null)
                {
                    var result = method.Invoke(null, new object[] { 3f });
                    sb.AppendLine($"  BuildTimeFormat(3f) = \"{result}\"");
                }
            }
            catch (Exception e) { sb.AppendLine($"  BuildTimeFormat 异常: {e.Message.Split('\n')[0]}"); }
        }

        // ── 5. 诊断 ConstructionPanel 当前状态 ──
        sb.AppendLine("\n=== ConstructionPanel 诊断 ===");
        try
        {
            var cp = typeof(ConstructionPanel).GetProperty("instance",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
            if (cp == null)
            {
                sb.AppendLine("  ConstructionPanel.instance = null（菜单未打开）");
            }
            else
            {
                sb.AppendLine($"  ConstructionPanel.instance = {cp.GetType().FullName}");
                var selObj = Reflect.Get(cp, "selectedId");
                sb.AppendLine($"  selectedId = {selObj}");
                var stt = Reflect.Get(cp, "statTimeValueText");
                sb.AppendLine($"  statTimeValueText = {(stt != null ? ((Text)stt).text : "null")}");
                var dt = Reflect.Get(cp, "detailRoot");
                sb.AppendLine($"  detailRoot active = {(dt != null ? ((RectTransform)dt).gameObject.activeInHierarchy : "null")}");
            }
        }
        catch (Exception e) { sb.AppendLine($"  ConstructionPanel 诊断异常: {e.Message.Split('\n')[0]}"); }

        // ── 6. 诊断 BuildInfoPanel ──
        sb.AppendLine("\n=== BuildInfoPanel 诊断 ===");
        try
        {
            var bip = typeof(BuildInfoPanel).GetProperty("instance",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
            if (bip == null)
            {
                sb.AppendLine("  BuildInfoPanel.instance = null（未打开）");
            }
            else
            {
                sb.AppendLine($"  BuildInfoPanel.instance = {bip.GetType().FullName}");
                var cto = Reflect.Get(bip, "currentTerrainObject");
                sb.AppendLine($"  currentTerrainObject = {cto}");
            }
        }
        catch (Exception e) { sb.AppendLine($"  BuildInfoPanel 诊断异常: {e.Message.Split('\n')[0]}"); }

        // ── 7. 挂 Harmony 诊断：监控 statTimeValueText.text setter ──
        sb.AppendLine("\n=== Harmony 诊断 hook ===");
        try
        {
            var h = new Harmony("com.zedzone.teleport.attrprobe.diag");

            // Hook: ConstructionPanel.ShowTerrainObjectDetails
            var showMethod = AccessTools.Method(typeof(ConstructionPanel), "ShowTerrainObjectDetails");
            if (showMethod != null)
            {
                h.Patch(showMethod,
                    prefix: new HarmonyMethod(typeof(DiagHooks).GetMethod(nameof(DiagHooks.ShowDetailsPrefix))),
                    postfix: new HarmonyMethod(typeof(DiagHooks).GetMethod(nameof(DiagHooks.ShowDetailsPostfix))));
                sb.AppendLine("  [OK] ShowTerrainObjectDetails 已挂钩");
            }
            else sb.AppendLine("  [FAIL] ShowTerrainObjectDetails 未找到");

            // Hook: ConstructionPanel.SelectItem (private)
            var selectMethod = AccessTools.Method(typeof(ConstructionPanel), "SelectItem",
                new[] { typeof(int), typeof(bool) });
            if (selectMethod != null)
            {
                h.Patch(selectMethod,
                    postfix: new HarmonyMethod(typeof(DiagHooks).GetMethod(nameof(DiagHooks.SelectItemPostfix))));
                sb.AppendLine("  [OK] SelectItem 已挂钩");
            }
            else sb.AppendLine("  [FAIL] SelectItem 未找到");

            // Hook: ConstructionPanel.OnCardClicked
            var cardClickMethod = AccessTools.Method(typeof(ConstructionPanel), "OnCardClicked");
            if (cardClickMethod != null)
            {
                h.Patch(cardClickMethod,
                    postfix: new HarmonyMethod(typeof(DiagHooks).GetMethod(nameof(DiagHooks.CardClickedPostfix))));
                sb.AppendLine("  [OK] OnCardClicked 已挂钩");
            }
            else sb.AppendLine("  [FAIL] OnCardClicked 未找到");

            // Hook: ConstructionPanel.BuildDetailPane (private)
            var buildDetailMethod = AccessTools.Method(typeof(ConstructionPanel), "BuildDetailPane");
            if (buildDetailMethod != null)
            {
                h.Patch(buildDetailMethod,
                    postfix: new HarmonyMethod(typeof(DiagHooks).GetMethod(nameof(DiagHooks.BuildDetailPanePostfix))));
                sb.AppendLine("  [OK] BuildDetailPane 已挂钩");
            }
            else sb.AppendLine("  [FAIL] BuildDetailPane 未找到");

            // Hook: BuildInfoPanel.GetBuildRealSeconds
            var gbrs = AccessTools.Method(typeof(BuildInfoPanel), "GetBuildRealSeconds");
            if (gbrs != null)
            {
                h.Patch(gbrs,
                    postfix: new HarmonyMethod(typeof(DiagHooks).GetMethod(nameof(DiagHooks.GetBuildRealSecondsPostfix))));
                sb.AppendLine("  [OK] GetBuildRealSeconds 已挂钩");
            }
            else sb.AppendLine("  [FAIL] GetBuildRealSeconds 未找到");

            // Hook: BuildInfoPanel.GetTotalMaterialNumber
            var gtmn = AccessTools.Method(typeof(BuildInfoPanel), "GetTotalMaterialNumber");
            if (gtmn != null)
            {
                h.Patch(gtmn,
                    postfix: new HarmonyMethod(typeof(DiagHooks).GetMethod(nameof(DiagHooks.GetTotalMaterialNumberPostfix))));
                sb.AppendLine("  [OK] GetTotalMaterialNumber 已挂钩");
            }
            else sb.AppendLine("  [FAIL] GetTotalMaterialNumber 未找到");

            // Hook: GameController.GetTerrainObjectIconSprite
            var gis = AccessTools.Method(typeof(GameController), "GetTerrainObjectIconSprite");
            if (gis != null)
            {
                h.Patch(gis,
                    postfix: new HarmonyMethod(typeof(DiagHooks).GetMethod(nameof(DiagHooks.GetIconSpritePostfix))));
                sb.AppendLine("  [OK] GetTerrainObjectIconSprite 已挂钩");
            }
            else sb.AppendLine("  [FAIL] GetTerrainObjectIconSprite 未找到");

            sb.AppendLine("  提示：打开建造菜单 → 电力分类 → 点击 900101-103 → 观察 LogInfo");
        }
        catch (Exception e) { sb.AppendLine($"  Harmony 诊断 hook 异常: {e}"); }

        Plugin.L.LogInfo(sb.ToString());
        Plugin.L.LogInfo("[Probe] ===== 诊断完成，请打开建造菜单电力分类并点击建筑卡片触发诊断 hook =====");
    }

    private static object GetListItem(object list, int i)
    {
        try
        {
            var p = list.GetType().GetProperty("Item");
            if (p != null) return p.GetValue(list, new object[] { i });
            var m = list.GetType().GetMethod("get_Item");
            return m == null ? null : m.Invoke(list, new object[] { i });
        }
        catch { return null; }
    }
}

/// <summary>诊断 Harmony hooks —— 所有 postfix 只打印日志，不做任何修改。</summary>
public static class DiagHooks
{
    // ShowTerrainObjectDetails 诊断
    public static void ShowDetailsPrefix(ConstructionPanel __instance, int __0)
    {
        Plugin.L.LogInfo($"[Diag] >>> ShowTerrainObjectDetails 前置: id={__0}");
    }
    public static void ShowDetailsPostfix(ConstructionPanel __instance, int __0)
    {
        try
        {
            var stt = __instance != null ? Reflect.Get(__instance, "statTimeValueText") as Text : null;
            string val = stt?.text ?? "null";
            Plugin.L.LogInfo($"[Diag] <<< ShowTerrainObjectDetails 后置: id={__0} statTimeValueText=\"{val}\"");
        }
        catch { }
    }

    // SelectItem 诊断
    public static void SelectItemPostfix(ConstructionPanel __instance, int __0, bool __1)
    {
        Plugin.L.LogInfo($"[Diag] <<< SelectItem 后置: id={__0} animate={__1}");
    }

    // OnCardClicked 诊断
    public static void CardClickedPostfix(ConstructionPanel __instance, ConstructionItemCardUI __0, int __1)
    {
        Plugin.L.LogInfo($"[Diag] <<< OnCardClicked 后置: id={__1}");
    }

    // BuildDetailPane 诊断
    public static void BuildDetailPanePostfix(ConstructionPanel __instance)
    {
        try
        {
            var stt = __instance != null ? Reflect.Get(__instance, "statTimeValueText") as Text : null;
            var selObj = __instance != null ? Reflect.Get(__instance, "selectedId") : null;
            string val = stt?.text ?? "null";
            string sid = selObj?.ToString() ?? "null";
            Plugin.L.LogInfo($"[Diag] <<< BuildDetailPane 后置: selectedId={sid} statTimeValueText=\"{val}\"");
        }
        catch { }
    }

    // GetBuildRealSeconds 诊断
    public static void GetBuildRealSecondsPostfix(RecipeData __0, ref float __result)
    {
        try
        {
            int id = __0 != null ? Convert.ToInt32(Reflect.Get(__0, "itemId") ?? -1) : -1;
            Plugin.L.LogInfo($"[Diag] <<< GetBuildRealSeconds 后置: recipeItemId={id} result={__result}");
        }
        catch (Exception e) { Plugin.L.LogInfo($"[Diag] <<< GetBuildRealSeconds 异常: {e.Message}"); }
    }

    // GetTotalMaterialNumber 诊断
    public static void GetTotalMaterialNumberPostfix(RecipeData __0, ref int __result)
    {
        try
        {
            int id = __0 != null ? Convert.ToInt32(Reflect.Get(__0, "itemId") ?? -1) : -1;
            Plugin.L.LogInfo($"[Diag] <<< GetTotalMaterialNumber 后置: recipeItemId={id} result={__result}");
        }
        catch (Exception e) { Plugin.L.LogInfo($"[Diag] <<< GetTotalMaterialNumber 异常: {e.Message}"); }
    }

    // GetTerrainObjectIconSprite 诊断
    public static void GetIconSpritePostfix(int __0, ref Sprite __result)
    {
        Plugin.L.LogInfo($"[Diag] <<< GetTerrainObjectIconSprite 后置: id={__0} sprite={__result?.name ?? "null"}");
    }
}
