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
/// 远距离传送站台 MOD v0.7.3（每帧开销优化：一次性分类缓存，非目标放置物 O(1) 跳过，多站点无压力）。
/// 源表定位（2026-08-27 离线侦察）：GameController 为建造源表宿主——
///   GetAvailableTerrainObjectAttrsByTechGenre(TechGenre) → List<TerrainObjectAttr>（建造菜单卡片数据源）、
///   GetTerrainObjectAttrById(int)（详情/建造查询）、terrainObjectAttrDic（按 id 字典）。
/// v0.5.0 源头注入（不再触碰 BuildPanel/卡片层）：
///   - 克隆模板 attr（108/120）注册 900101-3（id/名称/描述/配方，techGenre 显式=Electricity，unlockByDefault）；
///   - hook GetAvailableTerrainObjectAttrsByTechGenre postfix：Electricity 列表追加三建筑（游戏原版建卡流程）；
///   - hook GetTerrainObjectAttrById postfix：900101-3 查询兜底；
///   - 原版建筑完全不动；卡片 UI（名字/图标/点击/详情）由游戏原生渲染。
/// v0.6.31：
///   - RegisterBuilding 阶段 Reflect.Set(attr,"spriteName", Cache[id].name) + ModSpriteRegistry.Register 源头字典双保险；
///   - BuildInfoPanel.GetTotalMaterialNumber(RecipeData) postfix →6（=3s），纯源头不碰 UI；
///   - GameController.GetTerrainObjectIconSprite 保留为世界渲染源头，TickCardIconFix 仅作幂等兜底。
/// 经验教训：任何对 ConstructionPanel/detailIcon/statTime/ConstructionItemCardUI 的高频/实例级注入都会卡死，唯源头属性/字典安全。
/// 建筑 id：900101 控制台电脑 / 900102 传送台圆盘 / 900103 生物能发电站。
/// </summary>
[BepInPlugin("com.zedzone.teleportstation", "TeleportStation", "0.7.3")]
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

            // v0.6.33：LoadConstructionMenu 后置图标注入已移除——无条件写卡片 sprite/enabled/alpha，疑与建造流程 UI 状态机冲突（卡住：放下虚影/建造完成），纯源头实验
            // v0.5.8 旧逻辑：LoadConstructionMenu postfix → IconPostfix 一次性注入（回退时从这里恢复）
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

            // v0.6.30：图标源头——GameController.GetTerrainObjectIconSprite(int) 纯源头替换（public，非 MonoBehaviour，零卡死）
            var getIcon = AccessTools.Method(typeof(GameController), "GetTerrainObjectIconSprite");
            if (getIcon != null)
            {
                h.Patch(getIcon, postfix: new HarmonyMethod(typeof(IconSourceFix).GetMethod(
                    nameof(IconSourceFix.Postfix), BindingFlags.Public | BindingFlags.Static)));
                Log.LogInfo("[TS] 已挂钩 GameController.GetTerrainObjectIconSprite（图标源头）");
            }
            else Log.LogWarning("[TS] GetTerrainObjectIconSprite 挂钩失败（跳过）");
            // v0.6.39：圆盘渲染层守卫——拦截游戏「建筑 Y 排序」对圆盘实例 sortingLayer 的改写（恒守 FX_BG，防圆盘盖玩家/车）
            var setLayerName = AccessTools.Method(typeof(SpriteRenderer), "set_sortingLayerName");
            if (setLayerName != null)
            {
                h.Patch(setLayerName, prefix: new HarmonyMethod(typeof(PadLayerGuard).GetMethod(
                    nameof(PadLayerGuard.LayerNamePrefix), BindingFlags.Public | BindingFlags.Static)));
                Log.LogInfo("[TS] 已挂钩 SpriteRenderer.set_sortingLayerName（圆盘层守卫）");
            }
            var setLayerId = AccessTools.Method(typeof(SpriteRenderer), "set_sortingLayerID");
            if (setLayerId != null)
            {
                h.Patch(setLayerId, prefix: new HarmonyMethod(typeof(PadLayerGuard).GetMethod(
                    nameof(PadLayerGuard.LayerIdPrefix), BindingFlags.Public | BindingFlags.Static)));
                Log.LogInfo("[TS] 已挂钩 SpriteRenderer.set_sortingLayerID（圆盘层守卫）");
            }
        }
        catch (Exception e) { Log.LogError($"[TS] 源头注入 hook 异常: {e}"); }

        // v0.6.34：图标缓存提前到 Load（不等 20s 注册定时器），消除图标源头时序依赖
        try
        {
            foreach (var def in new[] { Buildings.ConsoleDef, Buildings.PadDef, Buildings.BioGenDef })
                SpriteInjector.CacheSprite(def);
        }
        catch (Exception e) { Log.LogWarning($"[TS] 提前图标缓存异常: {e.Message.Split('\n')[0]}"); }

        AddComponent<RegistrationProbe>();
        AddComponent<PadDeployMonitor>(); // v0.7.1：圆盘放置物渲染监控（尺寸/层/order 修正）
        Log.LogInfo("[TeleportStation] P1 v0.7.3 放置物开销优化（分类缓存）");
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
            // v0.6.33：卡片图标修复调度已移除（纯源头实验——spriteName+ModSpriteRegistry+IconSourceFix 已双保险）
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
                // v0.6.33：卡片图标修复移除（纯源头实验）；v0.6.15 旧逻辑 InjectCardIconOnce(id) 回退时恢复
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
    internal static void CacheSprite(BuildingDef def, bool force = false)
    {
        try
        {
            if (!force && Cache.ContainsKey(def.Id)) return; // v0.6.34 幂等：提前缓存与注册期重复调用安全
            if (Cache.ContainsKey(def.Id)) Cache.Remove(def.Id); // v0.6.35 force 重载先清旧键
            string p = Path.Combine(Path.Combine(Plugin.PluginDir, "textures"), def.IconFile);
            if (!File.Exists(p)) { Plugin.L.LogWarning($"[TS] 贴图不存在: {p}"); return; }
            var bytes = File.ReadAllBytes(p);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!ImageConversion.LoadImage(tex, bytes)) { Plugin.L.LogWarning($"[TS] LoadImage 失败: {def.IconFile}"); return; }
            var sp = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            sp.name = Path.GetFileNameWithoutExtension(def.IconFile) + "_" + def.Id;
            Cache[def.Id] = sp;
            Plugin.L.LogInfo($"[TS] 图标缓存: {def.Id} {def.IconFile} {tex.width}x{tex.height} name={sp.name}");
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

/// <summary>v0.6.40：圆盘层钉——物理随实例复制的组件（挂在克隆 prefab 根，游戏 Instantiate 自动带出）。
/// v0.6.41：改为 0.5s 动态收集 SR——游戏实例化时会重建/重设 SpriteRenderer（层被重置回建筑默认），
/// Awake 缓存旧列表会失效；动态收集 + LateUpdate/OnWillRenderObject 双时点写 FX_BG，并重申零件禁用。</summary>
public class PadLayerPin : MonoBehaviour
{
    private float _nextCollect = -1f;
    private SpriteRenderer[] _srs = new SpriteRenderer[0];

    private void Collect()
    {
        try { _srs = GetComponentsInChildren<SpriteRenderer>(true); } catch { _srs = new SpriteRenderer[0]; }
    }

    private void LateUpdate() { Pin(); }
    private void OnWillRenderObject() { Pin(); }

    private void Pin()
    {
        try
        {
            if (Time.unscaledTime >= _nextCollect)
            {
                _nextCollect = Time.unscaledTime + 0.5f;
                Collect();
            }
            for (int i = 0; i < _srs.Length; i++)
            {
                if (_srs[i] == null) continue;
                _srs[i].sortingLayerName = "FX_BG";
                string n = _srs[i].name ?? "";
                if (n.Contains("Cylinder") || n.Contains("Parts") || n.Contains("Fire"))
                    _srs[i].enabled = false; // 重申零件禁用（游戏重建 SR 时可能恢复）
            }
        }
        catch { }
    }
}
/// <summary>v0.6.39：圆盘层守卫（detour 层）——拦截游戏对 TS_TeleportPad 实例 sortingLayer 的改写，恒守 FX_BG（v0.6.40 起与 PadLayerPin 物理钉双保险）。</summary>
public static class PadLayerGuard
{
    private static bool IsPad(Transform t)
    {
        int d = 0;
        while (t != null && d++ < 16)
        {
            string n = t.name;
            if (n != null && n.Contains("TS_TeleportPad")) return true;
            t = t.parent;
        }
        return false;
    }

    public static bool LayerNamePrefix(SpriteRenderer __instance, string value)
    {
        try
        {
            if (value != "FX_BG" && __instance != null && IsPad(__instance.transform))
                return false; // 拒绝非 FX_BG 写入（建筑 Y 排序），圆盘层恒守地板之上/角色之下
        }
        catch { }
        return true;
    }

    public static bool LayerIdPrefix(SpriteRenderer __instance, int value)
    {
        try
        {
            int fxbg = SortingLayer.NameToID("FX_BG");
            if (value != fxbg && __instance != null && IsPad(__instance.transform))
                return false;
        }
        catch { }
        return true;
    }
}
/// <summary>v0.6.30：图标源头——GameController.GetTerrainObjectIconSprite(int) → Cache[id]，卡片+详情一次解决（public 源头，零轮询）。
/// v0.6.35：Cache miss 时实时强制重载（源头自愈，防模板图标 fallback）。</summary>
public static class IconSourceFix
{
    public static void Postfix(int __0, ref Sprite __result)
    {
        try
        {
            if (__0 < 900101 || __0 > 900103) return;
            SpriteInjector.Cache.TryGetValue(__0, out var sp);
            if (sp == null || string.IsNullOrEmpty(sp.name))
            {
                // v0.6.35 源头兜底：Cache 异常时实时重载（每次菜单/详情调用都自愈）
                var def = Buildings.ById(__0);
                if (def != null)
                {
                    Plugin.L.LogWarning($"[TS] 图标源头兜底重载: id={__0}");
                    SpriteInjector.CacheSprite(def, force: true);
                    SpriteInjector.Cache.TryGetValue(__0, out sp);
                }
            }
            if (sp != null && !string.IsNullOrEmpty(sp.name))
            {
                __result = sp;
                Plugin.L.LogInfo($"[TS] 图标源头: id={__0} → {sp.name}");
            }
            else Plugin.L.LogWarning($"[TS] 图标源头兜底失败: id={__0}");
        }
        catch (Exception e) { Plugin.L.LogWarning($"[TS] 图标源头异常: {e.Message.Split('\n')[0]}"); }
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
        // v0.6.33：周期图标修复移除（纯源头实验）；v0.6.16 旧逻辑 TickCardIconFix 回退时恢复
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
    public float EntityWorldH;      // v0.6.38 目标实体世界高度（>0 覆盖模板高；用于放大贴图像素匹配世界尺寸）
    public bool NoCollision;        // v0.6.38 无碰撞（圆盘：禁全部 Collider2D，容纳玩家站立/车辆停泊）
    public string LayerOverride;    // v0.6.38 排序层覆盖（圆盘：FX_BG——比地板高、比玩家/车辆低）
    public (int id, float num)[] Recipe;
}

internal static class Buildings
{
    public static readonly BuildingDef ConsoleDef = new()
    { Id = 900101, NameZh = "传送站控制台", NameEn = "Teleport Console", SpriteKey = "TeleportConsole", IconFile = "console.png",
      EntityWorldH = 2.5f, // v0.6.38：比通讯终端模板(2.0)稍大，匹配 64x83 贴图
      DescZh = "传送站主控台，负责远程传送站台的启动控制与目标设定。",
      Recipe = new[] { (28, 12f), (34, 48f), (61, 8f), (29, 24f), (1070, 1f), (84, 2f) } };

    public static readonly BuildingDef PadDef = new()
    { Id = 900102, NameZh = "传送台圆盘", NameEn = "Teleport Pad", SpriteKey = "TeleportPad", IconFile = "pad.png",
      EntityWorldH = 7.0f, NoCollision = true, LayerOverride = "FX_BG", // v0.6.39：2 倍盘（4.9→约 9.8 单位宽）；无碰撞可站立、层守卫 FX_BG
      DescZh = "传送台圆盘，配合控制台实现人员与物资的远距离定点传送。",
      Recipe = new[] { (66, 24f), (64, 20f), (61, 24f), (29, 12f), (86, 1f), (1082, 1f) } };

    public static readonly BuildingDef BioGenDef = new()
    { Id = 900103, NameZh = "生物质发电机", NameEn = "Biomass Generator", SpriteKey = "BiomassGenerator", IconFile = "biogen.png",
      EntityWorldH = 3.0f, // v0.6.38：比斯特林模板(2.67)略大，匹配 64x74 贴图
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
                // ── 5. v0.6.36 字典镜像 + P2 实体 prefab 源头化：扫描含参照键(108/120)的字典，补入 9001xx；
                //    prefab 字典 → 克隆模板 + 换我们的实体贴图（源头一次性，零轮询，原版不动）；其它字典 → 引用复制 ──
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
                            var itemProp = mem.GetType().GetProperty("Item");
                            var gim = mem.GetType().GetMethod("get_Item");
                            if (itemProp == null && gim == null) continue;
                            object GetVal(int id) => itemProp != null
                                ? itemProp.GetValue(mem, new object[] { id })
                                : gim.Invoke(mem, new object[] { id });
                            var add = mem.GetType().GetMethod("Add");
                            if (add == null) continue;
                            bool has108 = (bool)contains.Invoke(mem, new object[] { 108 });
                            bool has120 = (bool)contains.Invoke(mem, new object[] { 120 });
                            object src108 = has108 ? GetVal(108) : null;
                            object src120 = has120 ? GetVal(120) : null;
                            bool isPrefabDic = (src108 is GameObject) || (src120 is GameObject);
                            if (isPrefabDic)
                            {
                                if (has108 && src108 is GameObject g108 && !(bool)contains.Invoke(mem, new object[] { 900101 }))
                                {
                                    var clone = BuildPrefabClone(g108, Buildings.ConsoleDef);
                                    if (clone != null) { add.Invoke(mem, new object[] { 900101, clone }); mirrored++; sb.AppendLine($"  字典镜像+克隆: {tn} 900101←108(克隆贴图)"); }
                                }
                                if (has120 && src120 is GameObject g120)
                                {
                                    if (!(bool)contains.Invoke(mem, new object[] { 900102 }))
                                    {
                                        var clone = BuildPrefabClone(g120, Buildings.PadDef);
                                        if (clone != null) { add.Invoke(mem, new object[] { 900102, clone }); mirrored++; sb.AppendLine($"  字典镜像+克隆: {tn} 900102←120(克隆贴图)"); }
                                    }
                                    if (!(bool)contains.Invoke(mem, new object[] { 900103 }))
                                    {
                                        var clone = BuildPrefabClone(g120, Buildings.BioGenDef);
                                        if (clone != null) { add.Invoke(mem, new object[] { 900103, clone }); mirrored++; sb.AppendLine($"  字典镜像+克隆: {tn} 900103←120(克隆贴图)"); }
                                    }
                                }
                            }
                            else
                            {
                                // 非 prefab 字典：引用复制（与旧 900103←120 行为一致，泛化到全部模板键）
                                if (has108 && src108 != null && !(bool)contains.Invoke(mem, new object[] { 900101 }))
                                {
                                    add.Invoke(mem, new object[] { 900101, src108 }); mirrored++; sb.AppendLine($"  字典镜像: {tn} 900101←108");
                                }
                                if (has120 && src120 != null)
                                {
                                    if (!(bool)contains.Invoke(mem, new object[] { 900102 }))
                                    { add.Invoke(mem, new object[] { 900102, src120 }); mirrored++; sb.AppendLine($"  字典镜像: {tn} 900102←120"); }
                                    if (!(bool)contains.Invoke(mem, new object[] { 900103 }))
                                    { add.Invoke(mem, new object[] { 900103, src120 }); mirrored++; sb.AppendLine($"  字典镜像: {tn} 900103←120"); }
                                }
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

        // ── 7. v0.7.0：圆盘放置物注册（DeployableItem 体系，替代建筑圆盘——根治建筑 y-sort 盖玩家）──
        try
        {
            if (!PadDeployable.Register())
            {
                sb.AppendLine("[TS] 圆盘放置物注册推迟（下次重试）");
                RegistrarState.RetryIn(30);
            }
        }
        catch (Exception e7) { sb.AppendLine($"  圆盘放置物异常: {e7.Message.Split('\n')[0]}"); }

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
        SpriteInjector.CacheSprite(def); // v0.5.3：图标缓存（幂等）
        // v0.6.31 源头图标：让 BuildGridArea 天然拿到正确图（不碰 ConstructionPanel/Build 实例层）
        if (SpriteInjector.Cache.TryGetValue(def.Id, out var spCached) && spCached != null && !string.IsNullOrEmpty(spCached.name))
        {
            try { Reflect.Set(attr, "spriteName", spCached.name); Plugin.L.LogInfo($"[TS] spriteName 源头: id={def.Id} → {spCached.name}"); }
            catch (Exception e) { Plugin.L.LogWarning($"[TS] spriteName 设置异常: {e.Message.Split('\n')[0]}"); }
        }
        else
        {
            // v0.6.35 诊断 + 兜底：Cache miss/异常时打印状态并强制重载一次
            var diag = new System.Text.StringBuilder($"[TS] spriteName 源头跳过: id={def.Id} Cache.Count={SpriteInjector.Cache.Count}");
            foreach (var kv in SpriteInjector.Cache) diag.Append($" [{kv.Key}={(kv.Value == null ? "null" : (kv.Value.name ?? "<unnamed>"))}]");
            Plugin.L.LogWarning(diag.ToString());
            Plugin.L.LogWarning($"[TS] spriteName 兜底重载: id={def.Id}");
            SpriteInjector.CacheSprite(def, force: true);
            if (SpriteInjector.Cache.TryGetValue(def.Id, out var sp2) && sp2 != null && !string.IsNullOrEmpty(sp2.name))
            {
                try { Reflect.Set(attr, "spriteName", sp2.name); Plugin.L.LogInfo($"[TS] spriteName 兜底成功: id={def.Id} → {sp2.name}"); }
                catch (Exception e) { Plugin.L.LogWarning($"[TS] spriteName 设置异常2: {e.Message.Split('\n')[0]}"); }
            }
            else Plugin.L.LogWarning($"[TS] spriteName 兜底失败: id={def.Id}");
        }
        // v0.5.2：ModSpriteRegistry 注册建筑贴图（贴图在 textures/ 子目录，官方字典源头，双保险）
        try
        {
            ItemRegistryHelper.RegisterSprite(Plugin.PluginDir, "textures/" + def.IconFile, def.Id, "Main", 128, 96);
            // 反向验证是否已进入官方字典
            try
            {
                var isMod = ModSpriteRegistry.IsModItem(def.Id);
                var got = ModSpriteRegistry.GetMain(def.Id);
                string gName = got != null ? got.name : "null";
                string texInfo = got != null && got.texture != null ? $"{got.texture.width}x{got.texture.height}" : "no-tex";
                Plugin.L.LogInfo($"[TS] ModSpriteRegistry 校验: id={def.Id} IsModItem={isMod} GetMain={gName} tex={texInfo}");
            }
            catch (Exception e2) { Plugin.L.LogInfo($"[TS] ModSpriteRegistry 校验异常: {e2.Message.Split('\n')[0]}"); }
        }
        catch (Exception e) { Plugin.L.LogInfo($"  贴图注册异常: {e.Message.Split('\n')[0]}"); }
        Plugin.L.LogInfo($"[TS] ✅ 建筑克隆注册: id={def.Id} '{def.NameZh}' sprite={def.SpriteKey} genre=Electricity 配方{def.Recipe.Length}项");
    }

    /// <summary>v0.6.36 P2：克隆模板 prefab → 主 SpriteRenderer 换为我们的实体贴图（ppu=256 → 512px≈2 世界单位）。
    /// 零件贴图（Cylinder/Parts/Fire）禁用防叠加；Cache 异常时强制重载（与图标同源自愈）；任何失败退回模板不阻塞建造。</summary>
    private static GameObject BuildPrefabClone(GameObject template, BuildingDef def)
    {
        try
        {
            if (!SpriteInjector.Cache.TryGetValue(def.Id, out var iconSp) || iconSp == null || iconSp.texture == null)
            {
                SpriteInjector.CacheSprite(def, force: true);
                SpriteInjector.Cache.TryGetValue(def.Id, out iconSp);
            }
            if (iconSp == null || iconSp.texture == null) { Plugin.L.LogWarning($"[TS] prefab 克隆无贴图: id={def.Id} 退回模板"); return template; }
            var tex = iconSp.texture;
            // v0.6.37 ppu 自适应：贴图世界尺寸对齐模板主 SR 世界高度（像素密度自动匹配原版风格，换任意尺寸贴图无需改代码）
            float worldH = def.EntityWorldH > 0f ? def.EntityWorldH : 2f;
            if (def.EntityWorldH <= 0f)
            {
                try
                {
                    var tSrs = template.GetComponentsInChildren<SpriteRenderer>(true);
                    foreach (var s in tSrs)
                    {
                        if (s != null && s.sprite != null && s.sprite.texture != null)
                        {
                            var t = s.sprite;
                            float ppuT = t.pixelsPerUnit > 0f ? t.pixelsPerUnit : 24f;
                            worldH = t.rect.height / ppuT;
                            break;
                        }
                    }
                }
                catch { }
                if (worldH <= 0f) worldH = 2f;
            }
            float ppu = tex.height / worldH;
            var sp = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), ppu);
            sp.name = def.SpriteKey + "_Body";
            var clone = UnityEngine.Object.Instantiate(template);
            clone.name = "TS_" + def.SpriteKey;
            try { clone.hideFlags = HideFlags.HideAndDontSave; } catch { }
            bool mainDone = false;
            var srs = clone.GetComponentsInChildren<SpriteRenderer>(true);
            foreach (var sr in srs)
            {
                if (sr == null) continue;
                string sn = sr.name ?? "";
                if (!mainDone && (sn == "Sprite" || sn.StartsWith("Sprite") || sr.sprite == null))
                {
                    sr.sprite = sp; // 主贴图替换
                    mainDone = true;
                    continue;
                }
                if (sn.Contains("Cylinder") || sn.Contains("Parts") || sn.Contains("Fire"))
                    sr.enabled = false; // 零件贴图禁用（整机贴图已含细节）
            }
            // v0.6.38 圆盘特殊化：无碰撞（禁全部 Collider2D）+ 排序层覆盖（FX_BG：地板之上、玩家/车辆之下）
            if (def.NoCollision)
            {
                var cols = clone.GetComponentsInChildren<Collider2D>(true);
                foreach (var col in cols)
                {
                    if (col == null) continue;
                    col.enabled = false;
                }
            }
            if (!string.IsNullOrEmpty(def.LayerOverride))
            {
                foreach (var sr in srs)
                {
                    if (sr == null) continue;
                    try { sr.sortingLayerName = def.LayerOverride; } catch { }
                }
                // v0.6.40：挂载 PadLayerPin——游戏 Instantiate 克隆时组件随实例复制，每帧 LateUpdate+OnWillRenderObject 双时点钉死层
                try { clone.AddComponent<PadLayerPin>(); } catch (Exception e5) { Plugin.L.LogWarning($"[TS] PadLayerPin 挂载异常: {e5.Message.Split('\n')[0]}"); }
            }
            Plugin.L.LogInfo($"[TS] prefab 克隆: {def.SpriteKey} ← {template.name} sprite={sp.name} tex={tex.width}x{tex.height} ppu={ppu:F1} 世界≈{tex.width / ppu:F2}x{worldH:F2} 圆盘={(def.NoCollision ? "无碰撞" : "-")} 层={def.LayerOverride ?? "-"}");
            return clone;
        }
        catch (Exception e)
        {
            Plugin.L.LogWarning($"[TS] prefab 克隆异常: {e.Message.Split('\n')[0]}");
            return template;
        }
    }

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
