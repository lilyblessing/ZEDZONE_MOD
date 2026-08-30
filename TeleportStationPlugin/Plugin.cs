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
/// 远距离传送站台 MOD v0.8.10（P2 BioGen 白名单终版：Food 全放行+炭6豁免；烧录链 Ghidra 定案四件套）。
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
///   - GameController.GetTerrainObjectIconSprite 唯一源头（P1-A：卡片注入系列已删，图标全由 IconSourceFix 负责）。
/// 经验教训：任何对 ConstructionPanel/detailIcon/statTime/ConstructionItemCardUI 的高频/实例级注入都会卡死，唯源头属性/字典安全。
/// 建筑 id：900101 控制台电脑 / 900102 传送台圆盘 / 900103 生物能发电站。
/// </summary>
[BepInPlugin("com.zedzone.teleportstation", "TeleportStation", "0.8.10")]
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
            // P0-A（2026-08-31）：BioGen 钩子链与图标钩子解耦——此前 getFuel 及 A-D 白名单链全嵌套在
            // 未闭合的 if(genStop) 块内（活动方法存在时无恙，一旦游戏更新删除该方法则白名单/半速/启动门静默失效）

            // v0.8.0 P2：生物能电站燃料——OnGeneratorStart/Stop postfix（900103 准入改造 + 消耗观察）
            var genStart = AccessTools.Method(typeof(TerrainObject_Production_StirlingGenerator), "OnGeneratorStart");
            if (genStart != null)
            {
                h.Patch(genStart, postfix: new HarmonyMethod(typeof(BioGenFuel).GetMethod(
                    nameof(BioGenFuel.OnGeneratorStartPostfix), BindingFlags.Public | BindingFlags.Static)));
                Log.LogInfo("[TS] 已挂钩 StirlingGenerator.OnGeneratorStart（BioGen 准入）");
            }
            var genStop = AccessTools.Method(typeof(TerrainObject_Production_StirlingGenerator), "OnGeneratorStop");
            if (genStop != null)
            {
                h.Patch(genStop, postfix: new HarmonyMethod(typeof(BioGenFuel).GetMethod(
                    nameof(BioGenFuel.OnGeneratorStopPostfix), BindingFlags.Public | BindingFlags.Static)));
                Log.LogInfo("[TS] 已挂钩 StirlingGenerator.OnGeneratorStop（BioGen 观察）");
            }

            // v0.8.1：燃料仓标记（get_fuelInventoryData postfix）+ 白名单准入（TryAddItem prefix）
            var getFuel = AccessTools.Method(typeof(TerrainObject_Production_StirlingGenerator), "get_fuelInventoryData");
            if (getFuel != null)
            {
                h.Patch(getFuel, postfix: new HarmonyMethod(typeof(BioGenFuel).GetMethod(
                    nameof(BioGenFuel.GetFuelInventoryPostfix), BindingFlags.Public | BindingFlags.Static)));
                Log.LogInfo("[TS] 已挂钩 StirlingGenerator.get_fuelInventoryData（BioGen 燃料仓标记）");
            }
            // ═══ v0.8.9 生物能电站烧录链（Ghidra 定案重做）═══
            // A. UpdateStirlingGenerator prefix/postfix：标记烧录容器 inventoryData1 + ref addedTime×0.5 半速 + 扫描窗
            try
            {
                var usg = AccessTools.Method(typeof(ProductionManager), "UpdateStirlingGenerator",
                    new Type[] { typeof(ProductionData), typeof(float) });
                if (usg != null)
                {
                    h.Patch(usg,
                        prefix: new HarmonyMethod(typeof(BioGenFuel).GetMethod(
                            nameof(BioGenFuel.StirlingUpdatePrefix), BindingFlags.Public | BindingFlags.Static)),
                        postfix: new HarmonyMethod(typeof(BioGenFuel).GetMethod(
                            nameof(BioGenFuel.StirlingUpdatePostfix), BindingFlags.Public | BindingFlags.Static)));
                    Log.LogInfo("[TS] 已挂钩 ProductionManager.UpdateStirlingGenerator（BioGen 烧录标记+半速+启动窗）");
                }
                else Log.LogWarning("[TS] UpdateStirlingGenerator 挂钩失败（方法未找到）");
            }
            catch (Exception eu) { Log.LogWarning($"[TS] UpdateStirlingGenerator 挂钩异常: {eu.Message.Split('\n')[0]}"); }
            // B. 启动门：GetItemAttrById 扫描窗内为白名单燃料伪造 Combustible（木头 attr 复用）
            try
            {
                var gia = AccessTools.Method(typeof(ItemManager), "GetItemAttrById");
                if (gia != null)
                {
                    h.Patch(gia, prefix: new HarmonyMethod(typeof(BioGenFuel).GetMethod(
                        nameof(BioGenFuel.GetAttrByIdPrefix), BindingFlags.Public | BindingFlags.Static)));
                    Log.LogInfo("[TS] 已挂钩 ItemManager.GetItemAttrById（BioGen 启动门伪造 Combustible）");
                }
                else Log.LogWarning("[TS] GetItemAttrById 挂钩失败（方法未找到）");
            }
            catch (Exception eg) { Log.LogWarning($"[TS] GetItemAttrById 挂钩异常: {eg.Message.Split('\n')[0]}"); }
            // C. 准入环1：PassesFeatureLimit prefix（attr 级粗筛：205/炭/Food 放行，木头/金属拒；interop 直读不反射）
            try
            {
                var pfl = AccessTools.Method(typeof(InventoryData), "PassesFeatureLimit");
                if (pfl != null)
                {
                    h.Patch(pfl, prefix: new HarmonyMethod(typeof(BioGenFuel).GetMethod(
                        nameof(BioGenFuel.PassesFeatureLimitPrefix), BindingFlags.Public | BindingFlags.Static)));
                    Log.LogInfo("[TS] 已挂钩 InventoryData.PassesFeatureLimit（BioGen 准入粗筛，ItemAttr 版）");
                }
                else Log.LogWarning("[TS] PassesFeatureLimit 挂钩失败（方法未找到）");
            }
            catch (Exception ec) { Log.LogWarning($"[TS] PassesFeatureLimit 挂钩异常: {ec.Message.Split('\n')[0]}"); }
            // D. 准入环2：TryAddItem/AddItem prefix（item 级严格白名单：黑名单 205/炭6/过期食品）——灰烬注入也走 TryAddItem，6 号必须豁免
            foreach (var tn in new[] { "TryAddItem", "AddItem" })
            {
                try
                {
                    var tm = AccessTools.Method(typeof(InventoryData), tn);
                    if (tm == null) { Log.LogWarning($"[TS] InventoryData.{tn} 挂钩失败（方法未找到，跳过）"); continue; }
                    h.Patch(tm, prefix: new HarmonyMethod(typeof(BioGenFuel).GetMethod(
                        nameof(BioGenFuel.WhitelistPrefix), BindingFlags.Public | BindingFlags.Static)));
                    Log.LogInfo($"[TS] 已挂钩 InventoryData.{tn}（BioGen 严格白名单）");
                }
                catch (Exception e9) { Log.LogWarning($"[TS] InventoryData.{tn} 挂钩异常: {e9.Message.Split('\n')[0]}"); }
            }
            // ═══ v0.9.2 P3：电池仓充电——时间增量源（TimeController.AddTime，PortableFridge 已验证模式）═══
            try
            {
                var ta = AccessTools.Method(typeof(TimeController), "AddTime");
                if (ta != null)
                {
                    h.Patch(ta, postfix: new HarmonyMethod(typeof(BatteryChargeFix).GetMethod(
                        nameof(BatteryChargeFix.OnGameTimeAdded), BindingFlags.Public | BindingFlags.Static)));
                    Log.LogInfo("[TS] 已挂钩 TimeController.AddTime（电池仓充电时间源）");
                }
                else Log.LogWarning("[TS] TimeController.AddTime 挂钩失败（方法未找到）");
            }
            catch (Exception eh) { Log.LogWarning($"[TS] TimeController.AddTime 挂钩异常: {eh.Message.Split('\n')[0]}"); }
            try // 睡觉=ChangeTimeTo 绝对跳变（不 hook 则睡觉不充电，PortableFridge 同款协同）
            {
                var ct = AccessTools.Method(typeof(TimeController), "ChangeTimeTo");
                if (ct != null)
                {
                    h.Patch(ct, postfix: new HarmonyMethod(typeof(BatteryChargeFix).GetMethod(
                        nameof(BatteryChargeFix.OnGameTimeChangedTo), BindingFlags.Public | BindingFlags.Static)));
                    Log.LogInfo("[TS] 已挂钩 TimeController.ChangeTimeTo（睡觉时间跳变）");
                }
                else Log.LogWarning("[TS] TimeController.ChangeTimeTo 挂钩失败（方法未找到）");
            }
            catch (Exception ej) { Log.LogWarning($"[TS] TimeController.ChangeTimeTo 挂钩异常: {ej.Message.Split('\n')[0]}"); }
            // ═══ v0.9.4 P3 二期：充电台克隆盘的 ×4 倍率（UpdDateBatteryCharger 前后放大 sufficient）═══
            try
            {
                var ubc = AccessTools.Method(typeof(ProductionManager), "UpdateBatteryCharger");
                if (ubc != null)
                {
                    h.Patch(ubc,
                        prefix: new HarmonyMethod(typeof(ChargerPadFix).GetMethod(
                            nameof(ChargerPadFix.ChargerUpdatePrefix), BindingFlags.Public | BindingFlags.Static)),
                        postfix: new HarmonyMethod(typeof(ChargerPadFix).GetMethod(
                            nameof(ChargerPadFix.ChargerUpdatePostfix), BindingFlags.Public | BindingFlags.Static)));
                    Log.LogInfo("[TS] 已挂钩 ProductionManager.UpdateBatteryCharger（充电台盘 ×4 倍率）");
                }
                else Log.LogWarning("[TS] UpdateBatteryCharger 挂钩失败（方法未找到）");
            }
            catch (Exception ek) { Log.LogWarning($"[TS] UpdateBatteryCharger 挂钩异常: {ek.Message.Split('\n')[0]}"); }
            // P1-B（2026-08-31）：PadLayerGuard 已移除——10.30-10.33 实锤 detour 层拦不住实例（游戏实例化时重建/重置层），
            // 且为全游戏每次 SpriteRenderer 层写入的全局祖先链扫描（高频热路径）；层钉由 PadLayerPin 物理钉全权覆盖。
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
        Log.LogInfo("[TeleportStation] P1 v0.9.4 圆盘克隆源切充电台126（消费端：真实电网路由+原生充电；×4 倍率 hook+层钉 Production 列表）");
    }
}

/// <summary>v0.5.0：源头注入——hook GameController 源表方法，把我们的建筑追加进建造列表/查询。</summary>
public static class SourceInjector
{
    private static bool _snapshotDone; // v0.9.4 临时取证：电力卡源表快照（拿充电台原版 id，切换克隆源用）

    public static void AvailPostfix(object __0, ref Il2CppSystem.Collections.Generic.List<TerrainObjectAttr> __result)
    {
        try
        {
            if (RegistrationStore.Attrs.Count == 0) return;
            if (__result == null) return;
            if (Convert.ToInt32(__0) != Convert.ToInt32(TechGenre.Electricity)) return; // 三建筑均电力
            // v0.9.4 临时取证：首次进入电力栏打源表快照（id + 名称）
            if (!_snapshotDone)
            {
                _snapshotDone = true;
                var sb = new System.Text.StringBuilder("[TS] 电力卡源表快照: ");
                for (int i = 0; i < __result.Count; i++)
                {
                    var a = __result[i];
                    if (a == null) continue;
                    var idObj = Reflect.Get(a, "id");
                    object name = null;
                    try { name = Reflect.Get(a, "itemName_Runtime"); } catch { }
                    if (name == null) { try { name = Reflect.Get(a, "itemName"); } catch { } }
                    sb.Append($" [{idObj}={name}]");
                }
                Plugin.L.LogInfo(sb.ToString());
            }
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
            }
        }
        catch (Exception e) { Plugin.L.LogWarning($"[TS] AtbyIdPostfix 异常: {e.Message.Split('\n')[0]}"); }
    }

    /// <summary>v0.6.22：GetTerrainObjectPrefabById 兜底——我们的 id 用参照建筑 prefab 过渡（v0.9.4：按 id 选参照：900102←126充电台 / 900103←120斯特林）。</summary>
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
            int refId = id == 900102 ? 126 : 120;
            var prefab = m.Invoke(gc, new object[] { refId });
            __result = prefab as GameObject;
            if (__result != null) Plugin.L.LogInfo($"[TS] Prefab 兜底: id={id} → 参照{refId}（{(id == 900102 ? "充电台" : "斯特林")}模型过渡）");
        }
        catch (Exception e) { Plugin.L.LogWarning($"[TS] PrefabByIdPostfix 异常: {e.Message.Split('\n')[0]}"); }
    }
}

/// <summary>v0.5.3：建筑图标缓存（P1-A 2026-08-31：移除全部卡片注入死路径——Schedule/Tick/InjectCardIconOnce/
/// FixIconsOnce/InjectCardIcons/Prefix(Image) 系列；图标现由 IconSourceFix 源头 hook + 本缓存全权负责）。</summary>
public class SpriteInjector
{
    internal static readonly Dictionary<int, Sprite> Cache = new();

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
        catch (Exception e) { Plugin.L.LogWarning($"[TS] 建造时间源头异常: {e.Message.Split('\n')[0]}"); }
    }
}

/// <summary>v0.6.40：圆盘层钉——物理随实例复制的组件（挂在克隆 prefab 根，游戏 Instantiate 自动带出）。
/// v0.6.41：改为 0.5s 动态收集 SR——游戏实例化时会重建/重设 SpriteRenderer（层被重置回建筑默认），
/// Awake 缓存旧列表会失效；动态收集 + LateUpdate/OnWillRenderObject 双时点写 FX_BG，并重申零件禁用。
/// P2-C（2026-08-31）：每帧改写改 sortingLayerID（int，缓存一次 NameToID）替代字符串赋值；零件禁用降频到收集时（0.5s）。</summary>
public class PadLayerPin : MonoBehaviour
{
    private float _nextCollect = -1f;
    private SpriteRenderer[] _srs = new SpriteRenderer[0];
    private static int _fxBgId = -1;

    private void Collect()
    {
        try
        {
            if (_fxBgId < 0) _fxBgId = SortingLayer.NameToID("FX_BG");
            _srs = GetComponentsInChildren<SpriteRenderer>(true);
            for (int i = 0; i < _srs.Length; i++) // 零件禁用移入收集（0.5s 一次；游戏重建 SR 时最多半秒恢复后重申）
            {
                if (_srs[i] == null) continue;
                string n = _srs[i].name ?? "";
                if (n.Contains("Cylinder") || n.Contains("Parts") || n.Contains("Fire"))
                    _srs[i].enabled = false;
            }
        }
        catch (Exception e) { Plugin.L.LogWarning($"[TS] PadLayerPin 收集异常: {e.Message.Split('\n')[0]}"); _srs = new SpriteRenderer[0]; }
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
            int id = _fxBgId > 0 ? _fxBgId : SortingLayer.NameToID("FX_BG");
            if (id <= 0) return; // FX_BG 未注册时放弃（不写垃圾值）
            for (int i = 0; i < _srs.Length; i++)
            {
                if (_srs[i] == null) continue;
                _srs[i].sortingLayerID = id; // int 直写，避免每帧字符串→ID 转换
            }
        }
        catch (Exception e) { Plugin.L.LogWarning($"[TS] PadLayerPin 钉层异常: {e.Message.Split('\n')[0]}"); }
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
        // v0.9.0：建筑盘层钉 v2（无条件每帧调用，内部 0.5s 节流；须在 Done 早退之前——注册完成后仍要钉）
        try { BuildingPadFix.Tick(); } catch { }
        // v0.9.2 P3：电池仓充电（同前——注册完成后仍要充）
        try { BatteryChargeFix.Tick(); } catch { }
        // v0.9.4 P3 二期：充电台克隆盘容器微调（4×4/标题/槽数）
        try { ChargerPadFix.Tick(); } catch { }
        if (RegistrarState.Done && !RegistrarState.RetryPending) return;
        // P2-B（2026-08-31）：BioGenFuel.Tick 观察采样已随 P2 验收退役（Done 后本就不执行），移除调用
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

        // ── 1. 找克隆模板 attr（108 通讯终端 / 120 斯特林 / 126 电池充电台）──
        TerrainObjectAttr commuAttr = null, stirlingAttr = null, chargerAttr = null;
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
                if (id == 126 && chargerAttr == null) chargerAttr = a; // v0.9.4：电池充电台模板（圆盘克隆源切换）
            }
            sb.AppendLine($"  模板: 通讯终端108={(commuAttr != null ? "OK" : "NULL")} 斯特林120={(stirlingAttr != null ? "OK" : "NULL")} 充电台126={(chargerAttr != null ? "OK" : "NULL")}");
        }
        catch (Exception e) { sb.AppendLine($"  模板查找异常: {e.Message.Split('\n')[0]}"); }

        if (commuAttr == null || stirlingAttr == null || chargerAttr == null)
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
            RegisterBuilding(Buildings.PadDef, chargerAttr);   // v0.9.4：圆盘克隆源 斯特林120→充电台126（消费端：原生电网路由+原生充电逻辑）
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
                            bool has126 = (bool)contains.Invoke(mem, new object[] { 126 }); // v0.9.4：充电台模板（圆盘源）
                            object src108 = has108 ? GetVal(108) : null;
                            object src120 = has120 ? GetVal(120) : null;
                            object src126 = has126 ? GetVal(126) : null;
                            bool isPrefabDic = (src108 is GameObject) || (src120 is GameObject) || (src126 is GameObject);
                            if (isPrefabDic)
                            {
                                if (has108 && src108 is GameObject g108 && !(bool)contains.Invoke(mem, new object[] { 900101 }))
                                {
                                    var clone = BuildPrefabClone(g108, Buildings.ConsoleDef);
                                    if (clone != null) { add.Invoke(mem, new object[] { 900101, clone }); mirrored++; sb.AppendLine($"  字典镜像+克隆: {tn} 900101←108(克隆贴图)"); }
                                }
                                if (has126 && src126 is GameObject g126 && !(bool)contains.Invoke(mem, new object[] { 900102 }))
                                {
                                    var clone = BuildPrefabClone(g126, Buildings.PadDef); // v0.9.4：圆盘源 120→126
                                    if (clone != null) { add.Invoke(mem, new object[] { 900102, clone }); mirrored++; sb.AppendLine($"  字典镜像+克隆: {tn} 900102←126充电台(克隆贴图)"); }
                                }
                                if (has120 && src120 is GameObject g120)
                                {
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
                                if (has126 && src126 != null && !(bool)contains.Invoke(mem, new object[] { 900102 }))
                                { add.Invoke(mem, new object[] { 900102, src126 }); mirrored++; sb.AppendLine($"  字典镜像: {tn} 900102←126充电台"); }
                                if (has120 && src120 != null && !(bool)contains.Invoke(mem, new object[] { 900103 }))
                                { add.Invoke(mem, new object[] { 900103, src120 }); mirrored++; sb.AppendLine($"  字典镜像: {tn} 900103←120"); }
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
                // v0.6.40：挂载 PadLayerPin——泛型 AddComponent<T>() 对 mod MonoBehaviour 走 Il2Cpp 泛型 methodinfo 缓存抛
                // MethodInfoStoreGeneric 异常（日志"PadLayerPin 挂载异常"自 v0.8.x 起）；非泛型 AddComponent(Type) 参数又必须是
                // Il2CppSystem.Type（mod 类型转换不了）→ 组件路线废弃（2026-08-31 定论）。
                // 建筑盘层钉改走「排序写点 hook」方案（静态取证中：SortingGroup/factory 链+Ghidra 写点定位）
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
