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

namespace TeleportStationPlugin;

/// <summary>
/// 远距离传送站台 MOD v0.6.29（纯源头计费，移除轮询覆盖）。
/// 源表定位（2026-08-27 离线侦察）：GameController 为建造源表宿主——
///   GetAvailableTerrainObjectAttrsByTechGenre(TechGenre) → List<TerrainObjectAttr>（建造菜单卡片数据源）、
///   GetTerrainObjectAttrById(int)（详情/建造查询）、terrainObjectAttrDic（按 id 字典）。
/// v0.5.0 源头注入（不再触碰 BuildPanel/卡片层）：
///   - 克隆模板 attr（108/120）注册 900101-3（id/名称/描述/配方，techGenre 显式=Electricity，unlockByDefault）；
///   - hook GetAvailableTerrainObjectAttrsByTechGenre postfix：Electricity 列表追加三建筑（游戏原版建卡流程）；
///   - hook GetTerrainObjectAttrById postfix：900101-3 查询兜底；
///   - 原版建筑完全不动；卡片 UI（名字/图标/点击/详情）由游戏原生渲染。
/// v0.6.29：
///   - 保留 BuildInfoPanel.GetTotalMaterialNumber(RecipeData) postfix 强制 6（=3s），纯源头不碰 UI；
///   - 移除 RegistrationProbe.OverrideStatTime 轮询覆盖（500ms 也会干扰主线程角色控制，已实测卡死）；
///   - 图标仅保留 TickCardIconFix 零高频保障（已验证无卡死）。
/// 经验教训：任何对 ConstructionPanel/detailIcon/statTime 的高频动态注入都会抢占主线程输入，卡死角色控制，回归源头。
/// 建筑 id：900101 控制台电脑 / 900102 传送台圆盘 / 900103 生物能发电站。
/// </summary>
[BepInPlugin("com.zedzone.teleportstation", "TeleportStation", "0.6.29")]
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

        // v0.5.0：源头注入 hook（GameController 建造源表方法）
        try
        {
            var h = new Harmony("com.zedzone.teleportstation");
            var avail = AccessTools.Method(typeof(GameController), "GetAvailableTerrainObjectAttrsByTechGenre");
            if (avail != null)
            {
                h.Patch(avail, postfix: new HarmonyMethod(typeof(SourceInjector).GetMethod(
                    nameof(SourceInjector.AvailPostfix), BindingFlags.Public | BindingFlags.Static)));
                Log.LogInfo("[TS] 已挂钩 GameController.GetAvailableTerrainObjectAttrsByTechGenre（源头注入）");
            }
            else Log.LogWarning("[TS] GetAvailableTerrainObjectAttrsByTechGenre 挂钩失败");
            var byId = AccessTools.Method(typeof(GameController), "GetTerrainObjectAttrById");
            if (byId != null)
            {
                h.Patch(byId, postfix: new HarmonyMethod(typeof(SourceInjector).GetMethod(
                    nameof(SourceInjector.ByIdPostfix), BindingFlags.Public | BindingFlags.Static)));
                Log.LogInfo("[TS] 已挂钩 GameController.GetTerrainObjectAttrById（查询兜底）");
            }
            else Log.LogWarning("[TS] GetTerrainObjectAttrById 挂钩失败");

            // v0.5.8：LoadConstructionMenu 后置图标一次性注入（无轮询；sprite+enabled/alpha 修复）
            var loadCm = AccessTools.Method(typeof(ConstructionPanel), "LoadConstructionMenu");
            if (loadCm != null)
            {
                h.Patch(loadCm, postfix: new HarmonyMethod(typeof(IconPostfix).GetMethod(
                    nameof(IconPostfix.Postfix), BindingFlags.Public | BindingFlags.Static)));
                Log.LogInfo("[TS] 已挂钩 ConstructionPanel.LoadConstructionMenu（卡片图标后置）");
            }
            // v0.6.3：详情相关 patch 全部移除（未命中渲染路径；SelectItem 破坏点击流程）
            // v0.6.7：保持干净——仅源头注入 + 卡片图标后置；点击/详情 hooks 全部不挂（v0.6.3 为唯一稳定可点击基线）
            // v0.6.1：OnCardClicked 后置——详情图标注入
// 已移除 v0.6.3：OnCardClicked/ShowTerrainObjectDetails/SelectItem 的 patch 均未命中详情渲染路径，
//   SelectItem(private) patch 甚至破坏了点击流程（回滚）；详情大图标单独再议
        // v0.6.22：GetTerrainObjectPrefabById 兜底——BuildSelected→BuildTerrainObject 按 id 查 prefab 字典（KeyNotFoundException 源头）
            var prefabById = AccessTools.Method(typeof(GameController), "GetTerrainObjectPrefabById");
            if (prefabById != null)
            {
                h.Patch(prefabById, postfix: new HarmonyMethod(typeof(SourceInjector).GetMethod(
                    nameof(SourceInjector.PrefabByIdPostfix), BindingFlags.Public | BindingFlags.Static)));
                Log.LogInfo("[TS] 已挂钩 GameController.GetTerrainObjectPrefabById（prefab 兜底）");
            }
            else Log.LogWarning("[TS] GetTerrainObjectPrefabById 挂钩失败");

            // v0.6.28：建造时间源头——BuildInfoPanel.GetTotalMaterialNumber(RecipeData) → 强制 6（=3s，Σ/2 公式实测）
            var getTotalMat = AccessTools.Method(typeof(BuildInfoPanel), "GetTotalMaterialNumber");
            if (getTotalMat != null)
            {
                h.Patch(getTotalMat, postfix: new HarmonyMethod(typeof(BuildTimeSourceFix).GetMethod(
                    nameof(BuildTimeSourceFix.Postfix), BindingFlags.Public | BindingFlags.Static)));
                Log.LogInfo("[TS] 已挂钩 BuildInfoPanel.GetTotalMaterialNumber（建造时间 3s 源头）");
            }
            else Log.LogWarning("[TS] GetTotalMaterialNumber 挂钩失败（跳过）");
        }
        catch (Exception e) { Log.LogError($"[TS] 源头注入 hook 异常: {e}"); }

        AddComponent<RegistrationProbe>();
        Log.LogInfo("[TeleportStation] P1 v0.6.29 纯源头 3s（GetTotalMaterialNumber=6），已移除轮询覆盖");
    }
}

/// <summary>v0.5.0：源头注入——hook GameController 源表方法，把我们的建筑追加进建造列表/查询。</summary>
public static class SourceInjector
{
    public static void AvailPostfix(object __0, ref Il2CppSystem.Collections.Generic.List<TerrainObjectAttr> __result)
    {
        try
        {
            if (RegistrationStore.Attrs.Count == 0) return;
            if (__result == null) return;
            if (Convert.ToInt32(__0) != Convert.ToInt32(TechGenre.Electricity)) return; // 三建筑均电力
            // 幂等：已有则跳过
            for (int i = 0; i < __result.Count; i++)
            {
                var a = __result[i];
                if (a == null) continue;
                var idObj = Reflect.Get(a, "id");
                if (idObj != null && Convert.ToInt32(idObj) == 900101) return;
            }
            foreach (var kv in RegistrationStore.Attrs)
            {
                __result.Add(kv.Value);
            }
            Plugin.L.LogInfo($"[TS] 源头注入: Electricity 建造列表追加 {RegistrationStore.Attrs.Count} 建筑");
            // v0.6.16：图标修复调度挂在源头注入链（唯一 100% 可靠 hook）——一次性延迟修复，不依赖 GetById/Load postfix
            SpriteInjector.ScheduleCardIconFix();
        }
        catch (Exception e) { Plugin.L.LogWarning($"[TS] Avail postfix 异常: {e.Message.Split('\n')[0]}"); }
    }

    public static void ByIdPostfix(object __0, ref TerrainObjectAttr __result)
    {
        try
        {
            if (RegistrationStore.Attrs.Count == 0) return;
            int id = Convert.ToInt32(__0);
            if (id < 900101 || id > 900103) return;
            if (RegistrationStore.Attrs.TryGetValue(id, out var attr))
            {
                __result = attr;
                // v0.6.15：仅卡片图标一次性修复（幂等）——详情大图标方案已放弃（写入 detailIcon 触发游戏状态异常）
                try { SpriteInjector.InjectCardIconOnce(id); }
                catch (Exception e) { Plugin.L.LogWarning($"[TS] 卡片图标修复异常: {e.Message.Split('\n')[0]}"); }
            }
        }
        catch { }
    }

    /// <summary>v0.6.22：GetTerrainObjectPrefabById 兜底——我们的 id 用参照建筑（120 斯特林）prefab 过渡。</summary>
    public static void PrefabByIdPostfix(object __0, ref GameObject __result)
    {
        try
        {
            if (RegistrationStore.Attrs.Count == 0) return;
            int id = Convert.ToInt32(__0);
            if (id < 900101 || id > 900103) return;
            if (__result != null) return;
            var gc = typeof(GameController).GetProperty("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
            if (gc == null) return;
            var m = typeof(GameController).GetMethod("GetTerrainObjectPrefabById");
            if (m == null) return;
            var prefab = m.Invoke(gc, new object[] { 120 });
            __result = prefab as GameObject;
            if (__result != null) Plugin.L.LogInfo($"[TS] Prefab 兜底: id={id} → 参照120（斯特林模型过渡）");
        }
        catch { }
    }
}

/// <summary>v0.5.8：LoadConstructionMenu 后置——我们的卡图标一次性注入（sprite + enabled/alpha 修复）。</summary>
public static class IconPostfix
{
    public static void Postfix()
    {
        try { SpriteInjector.InjectCardIcons(); }
        catch (Exception e) { Plugin.L.LogWarning($"[TS] 图标后置异常: {e.Message.Split('\n')[0]}"); }
    }
}

/// <summary>v0.5.3：建筑图标缓存与卡片图标注入（v0.6.7 起仅保留卡片路径；详情相关已全部移除）。</summary>
public class SpriteInjector
{
    internal static readonly Dictionary<int, Sprite> Cache = new();

    // v0.6.17：延迟修复（挂源头注入链）——pending 保持直到全好；1s 节流；全好即停
    private static bool _iconFixPending;
    private static float _iconFixAt;
    private static float _lastTick = -1f;

    internal static void ScheduleCardIconFix()
    {
        _iconFixPending = true;
        _iconFixAt = Time.unscaledTime + 1.5f;
        Plugin.L.LogInfo($"[TS] 图标修复已调度 at={_iconFixAt:F1}");
    }

    /// <summary>由 RegistrationProbe.Update 每帧调用；未到时/节流内/菜单关闭零动作。</summary>
    internal static void TickCardIconFix()
    {
        if (!_iconFixPending) return;
        float now = Time.unscaledTime;
        if (now < _iconFixAt) return;
        if (now - _lastTick < 1f) return;
        _lastTick = now;
        try
        {
            var inst = typeof(ConstructionPanel).GetProperty("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as Component;
            if (inst == null || inst.gameObject == null || !inst.gameObject.activeInHierarchy) return; // 关着→保持 pending
            var genre = Reflect.Get(inst, "currentGenre");
            if (genre == null || genre.ToString() != "Electricity") return;
            int hit = InjectCardIcons();
            if (hit == 0)
            {
                // v0.6.20 深度诊断：单卡组件与字段
                try
                {
                    Plugin.L.LogInfo($"[TS] Tick: Cache={Cache.Count} Attrs={RegistrationStore.Attrs.Count}");
                    var gc = Reflect.Get(inst, "gridContent") as RectTransform;
                    if (gc != null)
                    {
                        for (int i = 0; i < gc.childCount; i++)
                        {
                            var c = gc.GetChild(i);
                            if (c == null || !c.name.StartsWith("Card_9001")) continue;
                            var comps = new System.Text.StringBuilder();
                            try
                            {
                                var cs = c.GetComponents<Component>();
                                foreach (var comp in cs) { if (comp != null) comps.Append(comp.GetType().Name).Append(','); }
                            }
                            catch { }
                            var ui = c.GetComponent<ConstructionItemCardUI>();
                            string ic = "?";
                            try { ic = ui == null ? "<UI=null>" : (ui.iconImage == null ? "<icon=null>" : "OK"); }
                            catch (Exception e3) { ic = "<异常:" + e3.Message.Split('\n')[0] + ">"; }
                            Plugin.L.LogInfo($"[TS] 诊断 '{c.name}': comps=[{comps}] uiIcon={ic} CacheHas900101={Cache.ContainsKey(900101)}");
                        }
                    }
                }
                catch (Exception e2) { Plugin.L.LogWarning($"[TS] Tick dump 异常: {e2.Message.Split('\n')[0]}"); }
            }
            Plugin.L.LogInfo($"[TS] Tick修复: hit={hit}");
            if (IconsAllOk(inst)) { _iconFixPending = false; Plugin.L.LogInfo("[TS] 图标全好，修复停止"); }
        }
        catch (Exception e) { Plugin.L.LogWarning($"[TS] 延迟图标修复异常: {e.Message.Split('\n')[0]}"); }
    }

    private static bool IconsAllOk(Component inst)
    {
        try
        {
            var gc = Reflect.Get(inst, "gridContent") as RectTransform;
            if (gc == null) return false;
            for (int i = 0; i < gc.childCount; i++)
            {
                var c = gc.GetChild(i);
                if (c == null) continue;
                int cid = CardIdFromName(c.name);
                if (cid <= 0) continue;
                var ui = c.GetComponent<ConstructionItemCardUI>();
                if (ui == null || ui.iconImage == null) return false;
                if (ui.iconImage.sprite == null || !ui.iconImage.enabled) return false;
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>v0.6.10：单卡图标修复（GetById 稳定通道；幂等——已注入跳过）。</summary>
    internal static void InjectCardIconOnce(int id)
    {
        if (!Cache.TryGetValue(id, out var sp) || sp == null) return;
        var inst = typeof(ConstructionPanel).GetProperty("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as Component;
        if (inst == null) return;
        var gc = Reflect.Get(inst, "gridContent") as RectTransform;
        if (gc == null) return;
        for (int i = 0; i < gc.childCount; i++)
        {
            var c = gc.GetChild(i);
            if (c == null || CardIdFromName(c.name) != id) continue;
            var ui = c.GetComponent<ConstructionItemCardUI>();
            if (ui == null || ui.iconImage == null) continue;
            bool broken = ui.iconImage.sprite == null || !ui.iconImage.enabled || ui.iconImage.color.a < 0.99f;
            if (!broken) return; // 已正常
            ui.iconImage.sprite = sp;
            if (!ui.iconImage.enabled) ui.iconImage.enabled = true;
            try
            {
                var col = ui.iconImage.color;
                if (col.a < 0.99f) { col.a = 1f; ui.iconImage.color = col; }
            }
            catch { }
            Plugin.L.LogInfo($"[TS] 卡片图标修复(GetById): {id}");
            return;
        }
    }

    /// <summary>注册时缓存贴图 Sprite（从 textures/ 目录加载）。</summary>
    internal static void CacheSprite(BuildingDef def)
    {
        try
        {
            string p = Path.Combine(Path.Combine(Plugin.PluginDir, "textures"), def.IconFile);
            if (!File.Exists(p)) { Plugin.L.LogWarning($"[TS] 贴图不存在: {p}"); return; }
            var bytes = File.ReadAllBytes(p);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!ImageConversion.LoadImage(tex, bytes)) { Plugin.L.LogWarning($"[TS] LoadImage 失败: {def.IconFile}"); return; }
            var sp = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            Cache[def.Id] = sp;
            Plugin.L.LogInfo($"[TS] 图标缓存: {def.Id} {def.IconFile} {tex.width}x{tex.height}");
        }
        catch (Exception e) { Plugin.L.LogWarning($"[TS] 图标缓存异常: {e.Message.Split('\n')[0]}"); }
    }

    public static bool Prefix(Image __instance, Sprite value)
    {
        try
        {
            if (value != null || __instance == null) return true;
            if (Cache.Count == 0) return true;
            // 卡片图标：Image 祖先链上有 Card_9001xx
            int id = MatchByAncestor(__instance.transform);
            if (id > 0)
            {
                if (Cache.TryGetValue(id, out var sp) && sp != null)
                {
                    __instance.sprite = sp; // value 非 null → 不会再进本 prefix
                    return false; // 跳过原 set(null)
                }
            }
        }
        catch { }
        return true;
    }

    // ── v0.5.4：直接赋值方案（游戏不 set sprite=null 时也生效）──

    // ── v0.5.9：修复即停——只在图片缺失/禁用时修复一次（避免重复赋值触发 UI 反复 dirty）──
    internal static void FixIconsOnce()
    {
        try
        {
            if (RegistrationStore.Attrs.Count == 0 || Cache.Count == 0) return;
            var inst = typeof(ConstructionPanel).GetProperty("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as Component;
            if (inst == null) return;
            var gc = Reflect.Get(inst, "gridContent") as RectTransform;
            if (gc == null) return;
            int fixedCnt = 0;
            for (int i = 0; i < gc.childCount; i++)
            {
                var c = gc.GetChild(i);
                if (c == null) continue;
                int id = CardIdFromName(c.name);
                if (id <= 0 || !Cache.TryGetValue(id, out var sp) || sp == null) continue;
                var ui = c.GetComponent<ConstructionItemCardUI>();
                if (ui == null || ui.iconImage == null) continue;
                bool broken = (ui.iconImage.sprite == null) || !ui.iconImage.enabled || ui.iconImage.color.a < 0.99f;
                if (!broken) continue;
                ui.iconImage.sprite = sp;
                try
                {
                    if (!ui.iconImage.enabled) ui.iconImage.enabled = true;
                    var col = ui.iconImage.color;
                    if (col.a < 0.99f) { col.a = 1f; ui.iconImage.color = col; }
                }
                catch { }
                fixedCnt++;
            }
            if (fixedCnt > 0) Plugin.L.LogInfo($"[TS] 图标修复: {fixedCnt} 张（修好即停）");
        }
        catch (Exception e) { Plugin.L.LogWarning($"[TS] FixIconsOnce 异常: {e.Message.Split('\n')[0]}"); }
    }

    /// <summary>LoadConstructionMenu 后置 / 延迟修复：对电力栏我们的卡片直接设置图标；返回命中数。</summary>
    internal static int InjectCardIcons()
    {
        try
        {
            if (RegistrationStore.Attrs.Count == 0 || Cache.Count == 0) return 0;
            var inst = typeof(ConstructionPanel).GetProperty("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as Component;
            if (inst == null || inst.gameObject == null || !inst.gameObject.activeInHierarchy) return 0;
            var gc = Reflect.Get(inst, "gridContent") as RectTransform;
            if (gc == null) return 0;
            int hit = 0;
            for (int i = 0; i < gc.childCount; i++)
            {
                var c = gc.GetChild(i);
                if (c == null) continue;
                int id = CardIdFromName(c.name);
                if (id <= 0 || !Cache.TryGetValue(id, out var sp) || sp == null) continue;
                var ui = c.GetComponent<ConstructionItemCardUI>();
                if (ui == null) continue;
                if (ui.iconImage == null) continue;
                ui.iconImage.sprite = sp;
                // v0.5.8：修复隐藏状态（游戏可能对无图标卡禁用 Icon）
                try
                {
                    if (!ui.iconImage.enabled) ui.iconImage.enabled = true;
                    var col = ui.iconImage.color;
                    if (col.a < 0.99f) { col.a = 1f; ui.iconImage.color = col; }
                }
                catch { }
                hit++;
            }
            if (hit > 0) Plugin.L.LogInfo($"[TS] 卡片图标直接注入: {hit} 张");
            return hit;
        }
        catch (Exception e) { Plugin.L.LogWarning($"[TS] 卡片图标注入异常: {e.Message.Split('\n')[0]}"); return 0; }
    }

    private static int CardIdFromName(string n)
    {
        if (n == null) return -1;
        if (n.StartsWith("Card_900101")) return 900101;
        if (n.StartsWith("Card_900102")) return 900102;
        if (n.StartsWith("Card_900103")) return 900103;
        return -1;
    }

    private static int MatchByAncestor(Transform t)
    {
        while (t != null)
        {
            string n = t.name;
            if (n != null)
            {
                if (n.StartsWith("Card_900101")) return 900101;
                if (n.StartsWith("Card_900102")) return 900102;
                if (n.StartsWith("Card_900103")) return 900103;
                if (n.Contains("ConstructionPanel")) return 0; // 到 panel 根
            }
            t = t.parent;
        }
        return -1;
    }

}

/// <summary>v0.6.28：建造时间源头——BuildInfoPanel.GetTotalMaterialNumber(RecipeData) 强制 6（= ceil(6/2)=3s）。</summary>
public static class BuildTimeSourceFix
{
    public static void Postfix(RecipeData __0, ref int __result)
    {
        try
        {
            if (__0 == null) return;
            var idObj = Reflect.Get(__0, "itemId");
            if (idObj == null) return;
            int id = Convert.ToInt32(idObj);
            if (id >= 900101 && id <= 900103)
            {
                __result = 6;
                Plugin.L.LogInfo($"[TS] 建造时间源头: id={id} Σ→6 (3s)");
            }
        }
        catch { }
    }
}

/// <summary>注入器状态。</summary>
internal static class RegistrarState
{
    internal static bool Done;
    internal static bool RetryPending;

    internal static void RetryIn(float seconds) { RetryPending = true; }
}

/// <summary>P1 注册探测与注入（触发器）。</summary>
public class RegistrationProbe : MonoBehaviour
{
    private float _timer = 20f; // 等 ItemManager/场景就绪

    private void Update()
    {
        // v0.6.16：延迟图标修复（一次性，仅浮点比较）—— 1.5s 延迟 + 1s 节流 + 全好即停，零高频污染
        try { SpriteInjector.TickCardIconFix(); }
        catch { }
        // v0.6.15：无周期检查（修复窗口/常驻检查全部移除——周期反射与写入会引发游戏异常）
        if (RegistrarState.Done && !RegistrarState.RetryPending) return;
        _timer -= Time.unscaledDeltaTime; // 建造菜单打开时游戏暂停（timeScale=0），必须用 unscaled
        if (_timer > 0f) return;
        if (RegistrarState.RetryPending) { _timer = 30f; RegistrarState.RetryPending = false; }
        RegistrarState.Done = true;
        try { RegistrarLogic.Run(); }
        catch (Exception e) { Plugin.L.LogError($"[TS] 探测顶层异常: {e}"); }
    }

}

/// <summary>已注册建筑 attr 表。</summary>
internal static class RegistrationStore
{
    internal static readonly System.Collections.Generic.Dictionary<int, TerrainObjectAttr> Attrs = new();
}

/// <summary>建筑定义。</summary>
internal sealed class BuildingDef
{
    public int Id;
    public string NameZh, NameEn, SpriteKey, IconFile, DescZh;
    public (int id, float num)[] Recipe;
}

internal static class Buildings
{
    public static readonly BuildingDef ConsoleDef = new()
    { Id = 900101, NameZh = "传送站控制台", NameEn = "Teleport Console", SpriteKey = "TeleportConsole", IconFile = "console.png",
      DescZh = "传送站主控台，负责远程传送站台的启动控制与目标设定。",
      Recipe = new[] { (28, 12f), (34, 48f), (61, 8f), (29, 24f), (1070, 1f), (84, 2f) } };

    public static readonly BuildingDef PadDef = new()
    { Id = 900102, NameZh = "传送台圆盘", NameEn = "Teleport Pad", SpriteKey = "TeleportPad", IconFile = "pad.png",
      DescZh = "传送台圆盘，配合控制台实现人员与物资的远距离定点传送。",
      Recipe = new[] { (66, 24f), (64, 20f), (61, 24f), (29, 12f), (86, 1f), (1082, 1f) } };

    public static readonly BuildingDef BioGenDef = new()
    { Id = 900103, NameZh = "生物质发电机", NameEn = "Biomass Generator", SpriteKey = "BiomassGenerator", IconFile = "biogen.png",
      DescZh = "生物质发电机，焚烧腐肉与过期食品为基地供给电力。",
      Recipe = new[] { (8, 20f), (61, 16f), (29, 12f), (13, 30f), (41, 1f) } };

    public static BuildingDef ById(int id) => id switch { 900101 => ConsoleDef, 900102 => PadDef, 900103 => BioGenDef, _ => null };
}


/// <summary>v0.1.2 注入逻辑（静态类，避免 Il2CppInterop 扫描 MonoBehaviour 方法）。</summary>
internal static class RegistrarLogic
{
    internal static void Run()
    {
        var sb = new StringBuilder();
        sb.AppendLine("[TS] ===== P1 建筑注册注入 =====");

        // ── 1. 找克隆模板 attr（108 通讯终端 / 120 斯特林）──
        TerrainObjectAttr commuAttr = null, stirlingAttr = null;
        try
        {
            var attrs = Resources.FindObjectsOfTypeAll<TerrainObjectAttr>();
            foreach (var a in attrs)
            {
                if (a == null) continue;
                var idObj = Reflect.Get(a, "id");
                if (idObj == null) continue;
                int id = Convert.ToInt32(idObj);
                if (id == 108 && commuAttr == null) commuAttr = a;
                if (id == 120 && stirlingAttr == null) stirlingAttr = a;
            }
            sb.AppendLine($"  模板: 通讯终端108={(commuAttr != null ? "OK" : "NULL")} 斯特林120={(stirlingAttr != null ? "OK" : "NULL")}");
        }
        catch (Exception e) { sb.AppendLine($"  模板查找异常: {e.Message.Split('\n')[0]}"); }

        if (commuAttr == null || stirlingAttr == null)
        {
            sb.AppendLine("[TS] ⚠ 模板 attr 未找齐，注入中止（可能进主菜单过早，需在存档内触发）");
            Plugin.L.LogInfo(sb.ToString());
            RegistrarState.RetryIn(30);
            return;
        }

        // ── 2. v0.5.0 克隆注册三建筑（克隆模板 → 改 id/名称/配方/techGenre=Electricity；原版不动）──
        try
        {
            RegisterBuilding(Buildings.ConsoleDef, commuAttr);
            RegisterBuilding(Buildings.PadDef, stirlingAttr);
            RegisterBuilding(Buildings.BioGenDef, stirlingAttr);
        }
        catch (Exception e) { sb.AppendLine($"  注册异常: {e}"); }

        // ── 3. 源头注入由 GameController hook 完成（GetAvailableTerrainObjectAttrsByTechGenre 追加）──

        // ── 4. v0.6.21 注册进 GameController.terrainObjectAttrDic（点击/建造用 dic[id] 索引——不注册会 KeyNotFoundException）──
        try
        {
            var gc = typeof(GameController).GetProperty("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
            if (gc != null)
            {
                var dic = Reflect.Get(gc, "terrainObjectAttrDic");
                if (dic != null)
                {
                    int added = 0;
                    foreach (var kv in RegistrationStore.Attrs)
                    {
                        if (!ItemRegistryHelper.DicContains(dic, kv.Key))
                        {
                            ItemRegistryHelper.DicAdd(dic, kv.Key, kv.Value);
                            added++;
                        }
                    }
                    sb.AppendLine($"  建筑字典注册: +{added}");
                }
                else sb.AppendLine("  terrainObjectAttrDic=null");
                var gd = Reflect.Get(gc, "terrainObjectAttrTechGenreDic");
                string gdInfo = "<null>";
                if (gd != null)
                {
                    try
                    {
                        var cp = gd.GetType().GetProperty("Count");
                        int cnt = cp == null ? -1 : Convert.ToInt32(cp.GetValue(gd));
                        gdInfo = $"{gd.GetType().FullName} count={cnt}";
                    }
                    catch (Exception e2) { gdInfo = "读取异常:" + e2.Message.Split('\n')[0]; }
                }
                sb.AppendLine($"  techGenreDic: {gdInfo}");
                // ── 5. v0.6.23 字典镜像：扫描含参照 id(120) 的字典成员，把我们的 id 镜像补入（prefab 字典等，绕开不稳定 hook）──
                try
                {
                    int mirrored = 0;
                    var members = new List<object>();
                    try
                    {
                        foreach (var p in gc.GetType().GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                        {
                            try { var v = p.GetValue(gc); if (v != null) members.Add(v); } catch { }
                        }
                    }
                    catch { }
                    try
                    {
                        foreach (var f in gc.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                        {
                            if (f.Name.StartsWith("Native") || f.Name is "isWrapped" or "pooledPtr") continue;
                            try { var v = f.GetValue(gc); if (v != null) members.Add(v); } catch { }
                        }
                    }
                    catch { }
                    foreach (var mem in members)
                    {
                        string tn = mem.GetType().Name;
                        if (!tn.Contains("Dictionary")) continue;
                        try
                        {
                            var contains = mem.GetType().GetMethod("ContainsKey");
                            if (contains == null) continue;
                            bool has120 = (bool)contains.Invoke(mem, new object[] { 120 });
                            if (!has120) continue;
                            bool has900 = (bool)contains.Invoke(mem, new object[] { 900103 });
                            if (has900) continue;
                            var itemProp = mem.GetType().GetProperty("Item");
                            object srcVal = null;
                            if (itemProp != null) srcVal = itemProp.GetValue(mem, new object[] { 120 });
                            else
                            {
                                var gim = mem.GetType().GetMethod("get_Item");
                                if (gim != null) srcVal = gim.Invoke(mem, new object[] { 120 });
                            }
                            var add = mem.GetType().GetMethod("Add");
                            if (srcVal != null && add != null)
                            {
                                add.Invoke(mem, new object[] { 900103, srcVal });
                                mirrored++;
                                sb.AppendLine($"  字典镜像: {tn} 900103←120");
                            }
                        }
                        catch { }
                    }
                    sb.AppendLine($"  字典镜像完成: {mirrored} 个");
                // ── 6. v0.6.24 源头列表注册：terrainObjectAttrTechGenreDic[Electricity] 的 List 追加（源头长出卡片，GetAvailable 只需幂等）──
                try
                {
                    var gd2 = Reflect.Get(gc, "terrainObjectAttrTechGenreDic");
                    int addedToList = 0;
                    if (gd2 != null)
                    {
                        var itemProp = gd2.GetType().GetProperty("Item");
                        object electrList = null;
                        if (itemProp != null) electrList = itemProp.GetValue(gd, new object[] { TechGenre.Electricity });
                        else
                        {
                            var gim = gd.GetType().GetMethod("get_Item");
                            if (gim != null) electrList = gim.Invoke(gd, new object[] { TechGenre.Electricity });
                        }
                        if (electrList != null)
                        {
                            var cntProp = electrList.GetType().GetProperty("Count");
                            int lstCnt = cntProp == null ? -1 : Convert.ToInt32(cntProp.GetValue(electrList));
                            foreach (var kv in RegistrationStore.Attrs)
                            {
                                bool exists = false;
                                for (int i = 0; i < lstCnt; i++)
                                {
                                    var existing = ListItemReflect(electrList, i) as TerrainObjectAttr;
                                    if (existing != null && AttrIdOf(existing) == kv.Key) { exists = true; break; }
                                }
                                if (!exists)
                                {
                                    var addM = electrList.GetType().GetMethod("Add");
                                    if (addM != null)
                                    {
                                        addM.Invoke(electrList, new object[] { kv.Value });
                                        addedToList++;
                                    }
                                }
                            }
                            var cntProp2 = electrList.GetType().GetProperty("Count");
                            sb.AppendLine($"  techGenreDic[Electricity] 列表: +{addedToList}（现 {cntProp2?.GetValue(electrList)} 项）");
                        }
                        else sb.AppendLine("  techGenreDic[Electricity] 获取失败");
                    }
                    else sb.AppendLine("  techGenreDic=null");
                }
                catch (Exception e4) { sb.AppendLine($"  源头列表注册异常: {e4.Message.Split('\n')[0]}"); }
                }
                catch (Exception e3) { sb.AppendLine($"  字典镜像异常: {e3.Message.Split('\n')[0]}"); }
            }
            else sb.AppendLine("  GameController.instance=null");
        }
        catch (Exception e) { sb.AppendLine($"  字典注册异常: {e.Message.Split('\n')[0]}"); }

        sb.AppendLine("[TS] ===== 注入结束 =====");
        Plugin.L.LogInfo(sb.ToString());
    }

    /// <summary>v0.5.0：克隆注册——新 attr 实例(id=900101-3)，techGenre 显式=Electricity（源头注入用）。</summary>
    private static void RegisterBuilding(BuildingDef def, TerrainObjectAttr template)
    {
        var attr = ScriptableObject.Instantiate(template);
        Reflect.Set(attr, "id", def.Id);
        Reflect.Set(attr, "chineseName", def.NameZh);
        Reflect.Set(attr, "englishName", def.NameEn);
        // v0.5.7 试验：spriteName 保持模板原值（验证「spriteName→贴图」查找路径是否通行；图标=参照建筑图）
        if (!string.IsNullOrEmpty(def.DescZh)) Reflect.Set(attr, "chineseDescription", def.DescZh);
        Reflect.Set(attr, "unlockByDefault", true);
        Reflect.Set(attr, "techGenre", TechGenre.Electricity);
        // 配方
        var recipe = new RecipeData();
        Reflect.Set(recipe, "itemId", def.Id);
        Reflect.Set(recipe, "outputItemNumber", 1f);
        // v0.6.25：建造时间——按模板 craftTime 等比缩到 3 秒（原 40-48 秒）
        try
        {
            var tplRt = Reflect.Get(template, "recipeData") as RecipeData;
            float tplT = -1f;
            if (tplRt != null) { var o = Reflect.Get(tplRt, "craftTime"); if (o != null) tplT = Convert.ToSingle(o); }
            Plugin.L.LogInfo($"[TS] 模板 craftTime={tplT}");
            if (tplT > 0f)
            {
                float target = tplT * (3f / 45f); // 45s-ish → 3s
                Reflect.Set(recipe, "craftTime", target);
                // v0.6.26：写入验证
                var verifyObj = Reflect.Get(recipe, "craftTime");
                float verify = verifyObj != null ? Convert.ToSingle(verifyObj) : -1f;
                Plugin.L.LogInfo($"[TS] craftTime 写入验证: target={target:F2} verify={verify:F2}");
            }
        }
        catch { }
        var mats = new Il2CppSystem.Collections.Generic.List<RecipeItemData>();
        foreach (var (mid, mnum) in def.Recipe)
        {
            var mi = new RecipeItemData();
            Reflect.Set(mi, "itemId", mid);
            Reflect.Set(mi, "itemNumber", mnum);
            mats.Add(mi);
        }
        Reflect.Set(recipe, "recipeItems", mats);
        Reflect.Set(attr, "recipeData", recipe);

        RegistrationStore.Attrs[def.Id] = attr;
        SpriteInjector.CacheSprite(def); // v0.5.3：图标缓存（Image.set_sprite 兜底用）
        // v0.5.2：ModSpriteRegistry 注册建筑贴图（贴图在 textures/ 子目录）
        try
        {
            ItemRegistryHelper.RegisterSprite(Plugin.PluginDir, "textures/" + def.IconFile, def.Id, "Main", 128, 96);
        }
        catch (Exception e) { Plugin.L.LogInfo($"  贴图注册异常: {e.Message.Split('\n')[0]}"); }
        Plugin.L.LogInfo($"[TS] ✅ 建筑克隆注册: id={def.Id} '{def.NameZh}' sprite={def.SpriteKey} genre=Electricity 配方{def.Recipe.Length}项");
    }

    // ── v0.5.0：卡片层不再操作（源头注入后游戏原版建卡流程，引用/名字/交互全由游戏处理）。
    //    图标先沿用模板 spriteName（建筑贴图表注册排后）。

    // ── v0.6.24 辅助：il2cpp List 反射读元素 / attr id ──
    private static object ListItemReflect(object list, int i)
    {
        if (list == null) return null;
        try
        {
            var p = list.GetType().GetProperty("Item");
            if (p != null) return p.GetValue(list, new object[] { i });
            var m = list.GetType().GetMethod("get_Item");
            return m == null ? null : m.Invoke(list, new object[] { i });
        }
        catch { return null; }
    }

    private static int AttrIdOf(TerrainObjectAttr a)
    {
        try { var o = Reflect.Get(a, "id"); return o == null ? -1 : Convert.ToInt32(o); }
        catch { return -1; }
    }

    internal static Texture2D LoadTex(string file)
    {
        string p = Path.Combine(Plugin.PluginDir, Path.Combine("textures", file));
        if (!File.Exists(p)) { Plugin.L.LogWarning($"[TS] 贴图不存在: {p}"); return null; }
        var bytes = File.ReadAllBytes(p);
        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (!ImageConversion.LoadImage(tex, bytes)) return null;
        return tex;
    }
}
