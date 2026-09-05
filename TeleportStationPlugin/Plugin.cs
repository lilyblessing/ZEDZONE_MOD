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
[BepInPlugin("com.zedzone.teleportstation", "TeleportStation", "0.9.95")]
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
                h.Patch(byId,
                    prefix: new HarmonyMethod(typeof(SourceInjector).GetMethod(
                        nameof(SourceInjector.AttrByIdPrefix), BindingFlags.Public | BindingFlags.Static)),
                    postfix: new HarmonyMethod(typeof(SourceInjector).GetMethod(
                        nameof(SourceInjector.ByIdPostfix), BindingFlags.Public | BindingFlags.Static)));
                Log.LogInfo("[TS] 已挂钩 GameController.GetTerrainObjectAttrById（查询短路）");
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
                h.Patch(prefabById,
                    prefix: new HarmonyMethod(typeof(SourceInjector).GetMethod(
                        nameof(SourceInjector.PrefabByIdPrefix), BindingFlags.Public | BindingFlags.Static)),
                    postfix: new HarmonyMethod(typeof(SourceInjector).GetMethod(
                        nameof(SourceInjector.PrefabByIdPostfix), BindingFlags.Public | BindingFlags.Static)));
                Log.LogInfo("[TS] 已挂钩 GameController.GetTerrainObjectPrefabById（prefab 短路）");
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
                    System.Reflection.MethodInfo tm;
                    string prefixName;
                    if (tn == "TryAddItem")
                    {
                        tm = AccessTools.Method(typeof(InventoryData), tn, new Type[] { typeof(ItemData), typeof(bool), typeof(bool) });
                        prefixName = nameof(BioGenFuel.WhitelistPrefix);
                    }
                    else
                    {
                        tm = AccessTools.Method(typeof(InventoryData), tn, new Type[] { typeof(ItemData), typeof(bool) });
                        prefixName = nameof(BioGenFuel.WhitelistPrefixInt);
                    }
                    if (tm == null) { Log.LogWarning($"[TS] InventoryData.{tn} 挂钩失败（方法未找到，跳过）"); continue; }
                    h.Patch(tm, prefix: new HarmonyMethod(typeof(BioGenFuel).GetMethod(
                        prefixName, BindingFlags.Public | BindingFlags.Static)));
                    Log.LogInfo($"[TS] 已挂钩 InventoryData.{tn}（BioGen 严格白名单）");
                }
                catch (Exception e9) { Log.LogWarning($"[TS] InventoryData.{tn} 挂钩异常: {e9.Message.Split('\n')[0]}"); }
            }
            // ═══ v0.9.2 P3：电池仓充电——时间增量源（TimeController.AddTime，PortableFridge 已验证模式）═══
            try
            {
                var ta = AccessTools.Method(typeof(TimeController), "AddTime", new Type[] { typeof(float) });
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
                var ct = AccessTools.Method(typeof(TimeController), "ChangeTimeTo", new Type[] { typeof(float) });
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
            // ═══ v0.9.93 读档去重守卫：ProductionManager.OnLoadGame prefix（09-05 更新后存档体自带重复
            // productionObjectId，原生循环 Add 无守卫读档崩；prefix 只剔除多余表项留首个，void 永不跳过原生）═══
            try
            {
                var olg = AccessTools.Method(typeof(ProductionManager), "OnLoadGame");
                if (olg != null)
                {
                    h.Patch(olg, prefix: new HarmonyMethod(typeof(LoadGuardFix).GetMethod(
                        nameof(LoadGuardFix.OnLoadGamePrefix), BindingFlags.Public | BindingFlags.Static)));
                    Log.LogInfo("[TS] 已挂钩 ProductionManager.OnLoadGame（读档去重守卫）");
                }
                else Log.LogWarning("[TS] OnLoadGame 挂钩失败（方法未找到）");
            }
            catch (Exception eo) { Log.LogWarning($"[TS] OnLoadGame 挂钩异常: {eo.Message.Split('\n')[0]}"); }
            // ═══ v0.9.73：状态字 16 槽上限屏蔽（8×8 下槽位号≥16 的 Set/Get 原生 throw → prefix 吞掉保充电）═══
            try
            {
                var sbs = AccessTools.Method(typeof(ProductionData), "SetBatteryState");
                if (sbs != null)
                {
                    h.Patch(sbs, prefix: new HarmonyMethod(typeof(ChargerPadFix).GetMethod(
                        nameof(ChargerPadFix.BatteryStateSetPrefix), BindingFlags.Public | BindingFlags.Static)));
                    Log.LogInfo("[TS] 已挂钩 ProductionData.SetBatteryState（16槽上限屏蔽）");
                }
                else Log.LogWarning("[TS] SetBatteryState 挂钩失败（方法未找到）");
                var gbs = AccessTools.Method(typeof(ProductionData), "GetBatteryState");
                if (gbs != null)
                {
                    h.Patch(gbs, prefix: new HarmonyMethod(typeof(ChargerPadFix).GetMethod(
                        nameof(ChargerPadFix.BatteryStateGetPrefix), BindingFlags.Public | BindingFlags.Static)));
                    Log.LogInfo("[TS] 已挂钩 ProductionData.GetBatteryState（16槽上限屏蔽）");
                }
                else Log.LogWarning("[TS] GetBatteryState 挂钩失败（方法未找到）");
            }
            catch (Exception es) { Log.LogWarning($"[TS] BatteryState 挂钩异常: {es.Message.Split('\n')[0]}"); }
            // ═══ v0.9.7：电网重扫轨迹探针（定位停机电线不重连）═══
            try
            {
                var mkd = AccessTools.Method(typeof(ProductionManager), "MarkElectricGridDirty");
                if (mkd != null)
                {
                    h.Patch(mkd, postfix: new HarmonyMethod(typeof(ChargerPadFix).GetMethod(
                        nameof(ChargerPadFix.GridDirtyPostfix), BindingFlags.Public | BindingFlags.Static)));
                    Log.LogInfo("[TS] 已挂钩 ProductionManager.MarkElectricGridDirty（电网脏标探针）");
                }
            }
            catch (Exception em) { Log.LogWarning($"[TS] MarkElectricGridDirty 挂钩异常: {em.Message.Split('\n')[0]}"); }
            try
            {
                var cgf = AccessTools.Method(typeof(ProductionManager), "ConsumeElectricGridDirtyFlag");
                if (cgf != null)
                {
                    h.Patch(cgf,
                        prefix: new HarmonyMethod(typeof(ChargerPadFix).GetMethod(
                            nameof(ChargerPadFix.GridConsumePrefix), BindingFlags.Public | BindingFlags.Static)),
                        postfix: new HarmonyMethod(typeof(ChargerPadFix).GetMethod(
                            nameof(ChargerPadFix.GridConsumePostfix), BindingFlags.Public | BindingFlags.Static)));
                    Log.LogInfo("[TS] 已挂钩 ProductionManager.ConsumeElectricGridDirtyFlag（电网重扫采样+表桶兜底）");
                }
            }
            catch (Exception en) { Log.LogWarning($"[TS] ConsumeElectricGridDirtyFlag 挂钩异常: {en.Message.Split('\n')[0]}"); }
            // ═══ v0.9.17 贴图重钉（治本）：GameController 双入口 postfix 用 prefab MOD 贴图覆盖模板贴图 ═══
            try
            {
                var bto = AccessTools.Method(typeof(GameController), "BuildTerrainObject");
                if (bto != null)
                {
                    h.Patch(bto,
                        prefix: new HarmonyMethod(typeof(ChargerPadFix).GetMethod(
                            nameof(ChargerPadFix.BuildTerrainObjectPrefix), BindingFlags.Public | BindingFlags.Static)),
                        postfix: new HarmonyMethod(typeof(ChargerPadFix).GetMethod(
                            nameof(ChargerPadFix.BuildTerrainObjectPostfix), BindingFlags.Public | BindingFlags.Static)));
                    Log.LogInfo("[TS] 已挂钩 GameController.BuildTerrainObject（克隆贴图重钉+探针）");
                }
                else Log.LogWarning("[TS] BuildTerrainObject 挂钩失败（方法未找到）");
            }
            catch (Exception eb) { Log.LogWarning($"[TS] BuildTerrainObject 挂钩异常: {eb.Message.Split('\n')[0]}"); }
            try
            {
                var ato = AccessTools.Method(typeof(GameController), "AddTerrainObject");
                if (ato != null)
                {
                    h.Patch(ato, postfix: new HarmonyMethod(typeof(ChargerPadFix).GetMethod(
                        nameof(ChargerPadFix.AddTerrainObjectPostfix), BindingFlags.Public | BindingFlags.Static)));
                    Log.LogInfo("[TS] 已挂钩 GameController.AddTerrainObject（克隆贴图重钉）");
                }
                else Log.LogWarning("[TS] AddTerrainObject 挂钩失败（方法未找到）");
            }
            catch (Exception ea) { Log.LogWarning($"[TS] AddTerrainObject 挂钩异常: {ea.Message.Split('\n')[0]}"); }
            // ═══ v0.9.21 R11-1 权威补键：GameController.InitTerrainObjectAttrs postfix 场景加载必走托管路径 ═══
            try
            {
                var ita = typeof(GameController).GetMethod("InitTerrainObjectAttrs", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (ita != null)
                {
                    h.Patch(ita, postfix: new HarmonyMethod(typeof(ChargerPadFix).GetMethod(nameof(ChargerPadFix.InitTerrainObjectAttrsPostfix), BindingFlags.Public | BindingFlags.Static)));
                    h.Patch(ita, postfix: new HarmonyMethod(typeof(SourceInjector).GetMethod(nameof(SourceInjector.AttrDictRebuiltPostfix), BindingFlags.Public | BindingFlags.Static))); // P1-10A：同方法复位标记（字典重建后重补）
                    Log.LogInfo("[TS] 已挂钩 GameController.InitTerrainObjectAttrs（权威补键）");
                }
                else Log.LogWarning("[TS] InitTerrainObjectAttrs 挂钩失败（方法未找到）");
            }
            catch (Exception ei) { Log.LogWarning($"[TS] InitTerrainObjectAttrs 挂钩异常: {ei.Message.Split('\n')[0]}"); }
            // ═══ v0.9.22 R12-2 克隆实例注册表（hideFlags=HideAndDontSave 下 FindObjectsOfType 不可见，OnEnable 注册不依赖可见性）═══
            try
            {
                var onP = AccessTools.Method(typeof(TerrainObject_Production), "OnEnable");
                if (onP != null)
                {
                    h.Patch(onP,
                        prefix: new HarmonyMethod(typeof(ChargerPadFix).GetMethod(nameof(ChargerPadFix.OnEnableBreaker_P), BindingFlags.Public | BindingFlags.Static)),
                        postfix: new HarmonyMethod(typeof(ChargerPadFix).GetMethod(nameof(ChargerPadFix.OnEnableRecorder_P), BindingFlags.Public | BindingFlags.Static)));
                    h.Patch(onP, postfix: new HarmonyMethod(typeof(ChargerPadFix).GetMethod(nameof(ChargerPadFix.OnEnableBreaker_X), BindingFlags.Public | BindingFlags.Static)));
                    Log.LogInfo("[TS] 已挂钩 TerrainObject_Production.OnEnable（克隆注册表）");
                }
                else Log.LogWarning("[TS] TerrainObject_Production.OnEnable 挂钩失败（方法未找到）");
            }
            catch (Exception er) { Log.LogWarning($"[TS] TerrainObject_Production.OnEnable 挂钩异常: {er.Message.Split('\n')[0]}"); }
            try
            {
                var onS = AccessTools.Method(typeof(TerrainObject_Production_StirlingGenerator), "OnEnable");
                if (onS != null)
                {
                    h.Patch(onS,
                        prefix: new HarmonyMethod(typeof(ChargerPadFix).GetMethod(nameof(ChargerPadFix.OnEnableBreaker_P), BindingFlags.Public | BindingFlags.Static)),
                        postfix: new HarmonyMethod(typeof(ChargerPadFix).GetMethod(nameof(ChargerPadFix.OnEnableRecorder_S), BindingFlags.Public | BindingFlags.Static)));
                    h.Patch(onS, postfix: new HarmonyMethod(typeof(ChargerPadFix).GetMethod(nameof(ChargerPadFix.OnEnableBreaker_X), BindingFlags.Public | BindingFlags.Static)));
                    Log.LogInfo("[TS] 已挂钩 TerrainObject_Production_StirlingGenerator.OnEnable（克隆注册表）");
                    Log.LogInfo("[TS] OnEnable 断路器已装（perInst>8/global>64 跳过）");
                }
                else Log.LogWarning("[TS] TerrainObject_Production_StirlingGenerator.OnEnable 挂钩失败（方法未找到）");
            }
            catch (Exception es) { Log.LogWarning($"[TS] TerrainObject_Production_StirlingGenerator.OnEnable 挂钩异常: {es.Message.Split('\n')[0]}"); }
            try
            {
                var onAll = AccessTools.Method(typeof(TerrainObject), "OnEnable");
                if (onAll != null)
                {
                    h.Patch(onAll, postfix: new HarmonyMethod(typeof(ChargerPadFix).GetMethod(nameof(ChargerPadFix.OnEnableRecorder_All), BindingFlags.Public | BindingFlags.Static)));
                    Log.LogInfo("[TS] 已挂钩 TerrainObject.OnEnable（通用克隆注册表 P6.1）");
                }
                else Log.LogWarning("[TS] TerrainObject.OnEnable 挂钩失败（方法未找到）");
            }
            catch (Exception ea) { Log.LogWarning($"[TS] TerrainObject.OnEnable 挂钩异常: {ea.Message.Split('\n')[0]}"); }
            // v0.9.23 Fix: 强制克隆缩放为1（根治虚影小+存量污染，Init 是唯一覆写点，postfix 最终写回 Vector3.one）
            try
            {
                var init = AccessTools.Method(typeof(TerrainObject), "Init");
                if (init != null)
                {
                    h.Patch(init, postfix: new HarmonyMethod(typeof(ChargerPadFix).GetMethod(nameof(ChargerPadFix.ScaleGuardPostfix), BindingFlags.Public | BindingFlags.Static)));
                    Log.LogInfo("[TS] 已挂钩 TerrainObject.Init（缩放强制为1）");
                }
                else Log.LogWarning("[TS] TerrainObject.Init 挂钩失败（方法未找到）");
            }
            catch (Exception eInit) { Log.LogWarning($"[TS] TerrainObject.Init 挂钩异常: {eInit.Message.Split('\n')[0]}"); }
            // P1-B（2026-08-31）：PadLayerGuard 已移除——10.30-10.33 实锤 detour 层拦不住实例（游戏实例化时重建/重置层），
            // 且为全游戏每次 SpriteRenderer 层写入的全局祖先链扫描（高频热路径）；层钉由 PadLayerPin 物理钉全权覆盖。
        }
        catch (Exception e) { Log.LogError($"[TS] 源头注入 hook 异常: {e}"); }

        // P6.3 控制台劫持：屏蔽 900101 雇佣交互，改由 TeleportConsoleMenuUI 按F接管
        try { var h61 = new Harmony("com.zedzone.teleportstation.p61"); TeleportConsoleInteractFix.EnsurePatch(h61); } catch (Exception e) { Log.LogWarning($"[TS] 控制台劫持挂钩异常: {e.Message.Split('\n')[0]}"); }
        // P6.2 控制台电脑菜单保留（兼容，但 900101 已不走 ComputerPanel，主路径为 MenuUI）
        try { var h62 = new Harmony("com.zedzone.teleportstation.p62"); TeleportConsoleComputerFix.EnsurePatch(h62); } catch (Exception e) { Log.LogWarning($"[TS] 控制台电脑菜单挂钩异常: {e.Message.Split('\n')[0]}"); }
        // P6.2 地图标记：仅传送模式显示
        try { var h63 = new Harmony("com.zedzone.teleportstation.p63"); TeleportMapManager.EnsurePatch(h63); } catch (Exception e) { Log.LogWarning($"[TS] 地图标记挂钩异常: {e.Message.Split('\n')[0]}"); }

        // v0.6.34：图标缓存提前到 Load（不等 20s 注册定时器），消除图标源头时序依赖
        try
        {
            foreach (var def in new[] { Buildings.ConsoleDef, Buildings.PadDef, Buildings.BioGenDef })
                SpriteInjector.CacheSprite(def);
        }
        catch (Exception e) { Log.LogWarning($"[TS] 提前图标缓存异常: {e.Message.Split('\n')[0]}"); }

        AddComponent<RegistrationProbe>();
        AddComponent<PadDeployMonitor>(); // v0.7.1：圆盘放置物渲染监控（尺寸/层/order 修正）
        AddComponent<TeleportBindingController>(); // P6.2：20m 自动互绑（1Hz）+ E/Q 原生保留
        // v0.9.70 存档隔离pin：真选择事件pin＋SaveGameData槽位快照回写＋存档对账（反编译实证：真读档裸写/自动存硬编码slot=5原地翻转）
        try { var h67 = new Harmony("com.zedzone.teleportstation.p67"); TeleportSaveIdentity.EnsurePatch(h67); } catch (Exception e) { Log.LogWarning($"[TS] 存档隔离挂钩异常: {e.Message.Split('\n')[0]}"); }
        try { TeleportSaveIdentity.Init(); } catch { }
        try { TeleportBindingManager.Load(); } catch { }
        try { TeleportConsoleSelection.Load(); } catch { }
        try { TeleportStationNameManager.Load(); } catch { }
        // P5
        try { Il2CppInterop.Runtime.Injection.ClassInjector.RegisterTypeInIl2Cpp<TeleportPadTrigger>(); Plugin.L.LogInfo("[TS] RegisterType TeleportPadTrigger OK"); } catch (Exception e) { Plugin.L.LogWarning($"[TS] RegisterType TeleportPadTrigger 失败: {e.Message}"); }
        try { Il2CppInterop.Runtime.Injection.ClassInjector.RegisterTypeInIl2Cpp<TeleportCountdownUI>(); Plugin.L.LogInfo("[TS] RegisterType TeleportCountdownUI OK"); } catch (Exception e) { Plugin.L.LogWarning($"[TS] RegisterType TeleportCountdownUI 失败: {e.Message}"); }
        try { Il2CppInterop.Runtime.Injection.ClassInjector.RegisterTypeInIl2Cpp<TeleportAnchorTicker>(); Plugin.L.LogInfo("[TS] RegisterType TeleportAnchorTicker OK"); } catch (Exception e) { Plugin.L.LogWarning($"[TS] RegisterType TeleportAnchorTicker 失败: {e.Message}"); }
        try { Il2CppInterop.Runtime.Injection.ClassInjector.RegisterTypeInIl2Cpp<TeleportConsoleUI>(); Plugin.L.LogInfo("[TS] RegisterType TeleportConsoleUI OK"); } catch (Exception e) { Plugin.L.LogWarning($"[TS] RegisterType TeleportConsoleUI 失败: {e.Message}"); }
        try { Il2CppInterop.Runtime.Injection.ClassInjector.RegisterTypeInIl2Cpp<TeleportStationRenameUI>(); Plugin.L.LogInfo("[TS] RegisterType TeleportStationRenameUI OK"); } catch (Exception e) { Plugin.L.LogWarning($"[TS] RegisterType TeleportStationRenameUI 失败: {e.Message}"); }
        try { Il2CppInterop.Runtime.Injection.ClassInjector.RegisterTypeInIl2Cpp<TeleportMapManager>(); Plugin.L.LogInfo("[TS] RegisterType TeleportMapManager OK"); } catch (Exception e) { Plugin.L.LogWarning($"[TS] RegisterType TeleportMapManager 失败: {e.Message}"); }
        // P6.4 起 TeleportConsoleMenuUI 自定义Canvas 已退役（复用原版F界面），保留类型注册以兼容旧存档但不再 AddComponent
        try { Il2CppInterop.Runtime.Injection.ClassInjector.RegisterTypeInIl2Cpp<TeleportConsoleMenuUI>(); Plugin.L.LogInfo("[TS] RegisterType TeleportConsoleMenuUI OK (dormant P6.4)"); } catch (Exception e) { Plugin.L.LogWarning($"[TS] RegisterType TeleportConsoleMenuUI 失败: {e.Message}"); }
        try { AddComponent<TeleportPadTrigger>(); Plugin.L.LogInfo("[TS] AddComponent TeleportPadTrigger OK"); } catch (Exception e) { Plugin.L.LogWarning($"[TS] AddComponent TeleportPadTrigger 失败: {e.GetType().Name} {e.Message}"); }
        try { AddComponent<TeleportCountdownUI>(); Plugin.L.LogInfo("[TS] AddComponent TeleportCountdownUI OK"); } catch (Exception e) { Plugin.L.LogWarning($"[TS] AddComponent TeleportCountdownUI 失败: {e.GetType().Name} {e.Message}"); }
        try { AddComponent<TeleportAnchorTicker>(); Plugin.L.LogInfo("[TS] AddComponent TeleportAnchorTicker OK"); } catch (Exception e) { Plugin.L.LogWarning($"[TS] AddComponent TeleportAnchorTicker 失败: {e.GetType().Name} {e.Message}"); }
        try { AddComponent<TeleportConsoleUI>(); Plugin.L.LogInfo("[TS] AddComponent TeleportConsoleUI OK"); } catch (Exception e) { Plugin.L.LogWarning($"[TS] AddComponent TeleportConsoleUI 失败: {e.GetType().Name} {e.Message}"); }
        try { AddComponent<TeleportStationRenameUI>(); Plugin.L.LogInfo("[TS] AddComponent TeleportStationRenameUI OK"); } catch (Exception e) { Plugin.L.LogWarning($"[TS] AddComponent TeleportStationRenameUI 失败: {e.GetType().Name} {e.Message}"); }
        try { AddComponent<TeleportMapManager>(); Plugin.L.LogInfo("[TS] AddComponent TeleportMapManager OK"); } catch (Exception e) { Plugin.L.LogWarning($"[TS] AddComponent TeleportMapManager 失败: {e.GetType().Name} {e.Message}"); }
        // TeleportConsoleMenuUI 不再 AddComponent（P6.4 复用原版InteractUI）
        try { TeleportExecutionManager.EnsurePatches(); Plugin.L.LogInfo("[TS] EnsurePatches P5 OK"); } catch (Exception e) { Plugin.L.LogWarning($"[TS] EnsurePatches 失败: {e.Message}"); }
        // P4 搬运放下主钩（探针实证：Build/Add 仅建/读档，搬运为已有实例位移 → HumanCharacterController.OnPlaceTerrainObject @0x18048A6F0 非虚 + TerrainObject.PlaceTerrainObject @0x18095A430 Slot27 双保险）
        try
        {
            var h2 = new Harmony("com.zedzone.teleportstation.p4");
            var onPlace = AccessTools.Method(AccessTools.TypeByName("HumanCharacterController"), "OnPlaceTerrainObject");
            if (onPlace != null) h2.Patch(onPlace, postfix: new HarmonyMethod(typeof(TeleportBindingManager).GetMethod(nameof(TeleportBindingManager.OnPlaceLifted), System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)));
            var place = AccessTools.Method(typeof(TerrainObject), "PlaceTerrainObject");
            if (place != null) h2.Patch(place, postfix: new HarmonyMethod(typeof(TeleportBindingManager).GetMethod(nameof(TeleportBindingManager.OnPlacedNoParam), System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)));
            var placeNoCheck = AccessTools.Method(typeof(TerrainObject), "PlaceTerrainObjectWithoutCheck");
            if (placeNoCheck != null) h2.Patch(placeNoCheck, postfix: new HarmonyMethod(typeof(TeleportBindingManager).GetMethod(nameof(TeleportBindingManager.OnPlacedNoParam), System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)));
            Log.LogInfo("[TS] 已挂钩 P4 搬运放下（OnPlaceTerrainObject/PlaceTerrainObject 双保险）");
        }
        catch (Exception ex) { Log.LogWarning($"[TS] P4 搬运钩异常: {ex.Message.Split('\n')[0]}"); }
        Log.LogInfo("[TeleportStation] v0.9.74 重扫前PD全表补齐含原生（读档杆-盘断线根治）");
    }
}

/// <summary>v0.5.0：源头注入——hook GameController 源表方法，把我们的建筑追加进建造列表/查询。</summary>
public static class SourceInjector
{
    private static bool _snapshotDone; // v0.9.4 临时取证：电力卡源表快照（拿充电台原版 id，切换克隆源用）
    internal static bool _attrsPatched; // P1-10A：attr/prefab 双字典补键完成标记（字典重建后由 AttrDictRebuiltPostfix 复位）

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

    /// <summary>P1-10A：双字典三键齐复核（缺任一键则 prefix 下次重补）。</summary>
    private static bool AttrPrefabDictsComplete(GameController gc)
    {
        try
        {
            var d = gc?.terrainObjectAttrDic;
            var p = gc?.terrainObjectPrefabDic;
            if (d == null || p == null) return false;
            return d.ContainsKey(900101) && d.ContainsKey(900102) && d.ContainsKey(900103)
                && p.ContainsKey(900101) && p.ContainsKey(900102) && p.ContainsKey(900103);
        }
        catch { return false; }
    }

    /// <summary>P1-10A：字典重建标记复位——InitTerrainObjectAttrs 会 Clear+重建字典（见 ChargerPadFix.cs），
    /// 本文件只改 Plugin.cs，故在此另挂一个同方法 postfix 做复位（Harmony 允许多 postfix 并存），原有补丁签名不动。</summary>
    public static void AttrDictRebuiltPostfix()
    {
        try { _attrsPatched = false; }
        catch { }
    }

    /// <summary>v0.9.18：GetTerrainObjectAttrById prefix 短路——克隆 id 直接返回注册 attr，跳过游戏原方法（字典补键不可靠时的根治）。
    /// P1-10A：补键循环仅 !_attrsPatched 时跑一次（普通原版 id 查询零字典写）。</summary>
    public static bool AttrByIdPrefix(object __0, ref TerrainObjectAttr __result, GameController __instance)
    {
        if (!_attrsPatched && __instance != null) { try { var d = __instance.terrainObjectAttrDic; if (d != null) { int[] ids = { 900101, 900102, 900103 }; foreach (var id in ids) { if (RegistrationStore.Attrs.TryGetValue(id, out var attr) && attr != null && !d.ContainsKey(id)) d.Add(id, attr); } if (AttrPrefabDictsComplete(__instance)) _attrsPatched = true; } } catch { } }
        try
        {
            int id = Convert.ToInt32(__0);
            if (id >= 900101 && id <= 900103)
            {
                if (RegistrationStore.Attrs.TryGetValue(id, out var attr) && attr != null)
                {
                    __result = attr;
                    return false;
                }
            }
        }
        catch { }
        return true;
    }

    /// <summary>v0.9.18：GetTerrainObjectPrefabById prefix 短路——克隆 id 直接返回注册 prefab，跳过游戏原方法。
    /// P1-10A：补键循环仅 !_attrsPatched 时跑一次（普通原版 id 查询零字典写）。</summary>
    public static bool PrefabByIdPrefix(object __0, ref GameObject __result, GameController __instance)
    {
        if (!_attrsPatched && __instance != null) { try { var d = __instance.terrainObjectPrefabDic; if (d != null) { int[] ids = { 900101, 900102, 900103 }; foreach (var id in ids) { if (RegistrationStore.Prefabs.TryGetValue(id, out var clone) && clone != null && !d.ContainsKey(id)) d.Add(id, clone); } if (AttrPrefabDictsComplete(__instance)) _attrsPatched = true; } } catch { } }
        try
        {
            int id = Convert.ToInt32(__0);
            if (id >= 900101 && id <= 900103)
            {
                if (RegistrationStore.Prefabs.TryGetValue(id, out var p) && p != null)
                {
                    __result = p;
                    return false;
                }
            }
        }
        catch { }
        return true;
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
            if (!(RegistrarState.Done && !RegistrarState.GaveUp)) return;
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
    internal static readonly Dictionary<int, Sprite> BodyCache = new();

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
    internal static readonly HashSet<int> PendingIcons = new(); // P1-10B：待节流重载的图标 id（去重；渲染热路径只入队不做 IO）
    internal static readonly HashSet<int> IconMissLogged = new(); // P1-10B：miss 日志去重（每 id 一条）
    internal static float NextIconRetry; // P1-10B：下次节流处理时间（Time.unscaledTime）

    public static void Postfix(int __0, ref Sprite __result)
    {
        try
        {
            if (__0 < 900101 || __0 > 900103) return;
            SpriteInjector.Cache.TryGetValue(__0, out var sp);
            if (sp != null && !string.IsNullOrEmpty(sp.name))
            {
                __result = sp;
                return; // P1-10B：命中零日志（原 LogInfo 每调用一次打一条，已删）
            }
            // P1-10B：miss 不做同步文件 IO（原 CacheSprite(force:true) 已搬入 DrainOnePendingIcon），只记 pending
            try
            {
                if (Buildings.ById(__0) == null) { Plugin.L.LogWarning($"[TS] 图标源头兜底失败: id={__0}"); return; }
                PendingIcons.Add(__0);
                if (IconMissLogged.Add(__0)) Plugin.L.LogWarning($"[TS] 图标待加载: id={__0}（节流后台重载）");
            }
            catch { }
        }
        catch (Exception e) { Plugin.L.LogWarning($"[TS] 图标源头异常: {e.Message.Split('\n')[0]}"); }
    }

    /// <summary>P1-10B：节流处理一个 pending 图标（原 Postfix 内 CacheSprite(force:true) 同步 IO 逻辑搬入，
    /// 由 RegistrationProbe.Update 每 0.5s 调一次、每次一个，全程 try/catch）。</summary>
    internal static void DrainOnePendingIcon()
    {
        try
        {
            if (PendingIcons.Count == 0) return;
            if (Time.unscaledTime < NextIconRetry) return;
            NextIconRetry = Time.unscaledTime + 0.5f;
            int pid = -1;
            foreach (var id in PendingIcons) { pid = id; break; }
            if (pid < 0) return;
            try
            {
                var def = Buildings.ById(pid);
                if (def != null) SpriteInjector.CacheSprite(def, force: true);
            }
            catch (Exception e) { Plugin.L.LogWarning($"[TS] 图标节流重载异常: {e.Message.Split('\n')[0]}"); }
            try { PendingIcons.Remove(pid); } catch { }
        }
        catch { }
    }
}

/// <summary>注入器状态。</summary>
internal static class RegistrarState
{
    internal static bool Done;
    internal static bool RetryPending;
    internal static bool GaveUp = false;

    internal static void RetryIn(float seconds) { RetryPending = true; }
}

/// <summary>P1 注册探测与注入（触发器）。</summary>
public class RegistrationProbe : MonoBehaviour
{
    private float _timer = 5f; // 等 ItemManager/场景就绪（5s抢在用户读档前注册；模板未齐/世界已活跃时靠 RetryPending 30s重试兜底）

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
        // P1-10B：图标 pending 节流重载（0.5s 一次、每次一个；须在 Done 早退之前——注册完成后仍要处理）
        try { IconSourceFix.DrainOnePendingIcon(); } catch { }
        if (RegistrarState.Done && !RegistrarState.RetryPending) return;
        // P2-B（2026-08-31）：BioGenFuel.Tick 观察采样已随 P2 验收退役（Done 后本就不执行），移除调用
        _timer -= Time.unscaledDeltaTime; // 建造菜单打开时游戏暂停（timeScale=0），必须用 unscaled
        if (_timer > 0f) return;
        if (RegistrarState.RetryPending) { _timer = 30f; RegistrarState.RetryPending = false; }
        if (RegistrarState.GaveUp) return;
        bool worldActive = false;
        try { var gcW = GameController.instance; if (gcW != null && gcW.playerCharacter != null) worldActive = true; } catch { }
        if (worldActive || TeleportSaveIdentity.LoadInitiated) { Plugin.L.LogInfo("[TS] 注册推迟：世界已活跃，仅主菜单期执行，30s后重试"); RegistrarState.RetryIn(30); return; }
        Plugin.L.LogInfo("[TS] 启动期注册…");
        try { RegistrarLogic.Run(); RegistrarState.Done = true; }
        catch (Exception e) { Plugin.L.LogError($"[TS] 探测顶层异常: {e}"); RegistrarState.RetryIn(30); }
    }

}

/// <summary>已注册建筑 attr 表。</summary>
internal static class RegistrationStore
{
    internal static readonly System.Collections.Generic.Dictionary<int, TerrainObjectAttr> Attrs = new();
    internal static readonly System.Collections.Generic.Dictionary<int, GameObject> Prefabs = new();
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
    // SO兼容：克隆期抑制旗标——Run 全程置位、finally 复位；BuildPrefabClone 内 Object.Instantiate 触发的
    // OnEnable/Init 系 postfix 见旗标直接退（只跳过我方注册/钉层逻辑，不跳过游戏原生）；Run 结束后三克隆显式登记补表。
    internal static void Run()
    {
        ChargerPadFix.IsCloning = true;
        try
        {
            RunInner();
            try
            {
                foreach (int id in new[] { 900101, 900102, 900103 })
                { try { if (RegistrationStore.Prefabs.TryGetValue(id, out var c) && c != null) ChargerPadFix.NoteClone(c); } catch { } }
            }
            catch { }
        }
        finally { try { ChargerPadFix.IsCloning = false; } catch { } }
    }

    private static void RunInner()
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
        // v0.9.92：单建筑独立 try/catch——一栋失败不连累另两栋，重试循环下幂等（Attrs 索引赋值覆盖）──
        try { RegisterBuilding(Buildings.ConsoleDef, commuAttr); }
        catch (Exception e) { sb.AppendLine($"  注册异常 900101: {e.Message.Split('\n')[0]}"); }
        try { RegisterBuilding(Buildings.PadDef, chargerAttr); }   // v0.9.4：圆盘克隆源 斯特林120→充电台126（消费端：原生电网路由+原生充电逻辑）
        catch (Exception e) { sb.AppendLine($"  注册异常 900102: {e.Message.Split('\n')[0]}"); }
        try { RegisterBuilding(Buildings.BioGenDef, stirlingAttr); }
        catch (Exception e) { sb.AppendLine($"  注册异常 900103: {e.Message.Split('\n')[0]}"); }

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
                                    if (clone != null) { add.Invoke(mem, new object[] { 900101, clone }); mirrored++; sb.AppendLine($"  字典镜像+克隆: {tn} 900101←108(克隆贴图)"); try { RegistrationStore.Prefabs[900101] = clone; } catch { } }
                                }
                                if (has126 && src126 is GameObject g126 && !(bool)contains.Invoke(mem, new object[] { 900102 }))
                                {
                                    var clone = BuildPrefabClone(g126, Buildings.PadDef); // v0.9.4：圆盘源 120→126
                                    if (clone != null) { add.Invoke(mem, new object[] { 900102, clone }); mirrored++; sb.AppendLine($"  字典镜像+克隆: {tn} 900102←126充电台(克隆贴图)"); try { RegistrationStore.Prefabs[900102] = clone; } catch { } }
                                }
                                if (has120 && src120 is GameObject g120)
                                {
                                    if (!(bool)contains.Invoke(mem, new object[] { 900103 }))
                                    {
                                        var clone = BuildPrefabClone(g120, Buildings.BioGenDef);
                                        if (clone != null) { add.Invoke(mem, new object[] { 900103, clone }); mirrored++; sb.AppendLine($"  字典镜像+克隆: {tn} 900103←120(克隆贴图)"); try { RegistrationStore.Prefabs[900103] = clone; } catch { } }
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
        // v0.9.8：耗电标志显式化——电网重扫(ElectricPole.RefreshElectricConnection)遍历耗电器表时过滤
        // attr.electricConsuming(0xA1)==0 跳过；克隆 Instantiate 理论已带模板值，此处双保险（充电台模板=消耗端必须 true）
        if (def.Id == 900102)
        {
            try { Reflect.Set(attr, "electricConsuming", true); } catch (Exception ez) { Plugin.L.LogWarning($"[TS] electricConsuming 设置异常: {ez.Message.Split('\n')[0]}"); }
        }
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
            // v0.9.24 Fix: Body 贴图单独缓存（ppu=worldH 正确），供 FixCloneSprites/EnsurePadSprites 巡检回钉（Icon 缓存 ppu100 勿用）
            try { SpriteInjector.BodyCache[def.Id] = sp; } catch { }
            var clone = UnityEngine.Object.Instantiate(template);
            clone.name = "TS_" + def.SpriteKey;
            try { clone.hideFlags = HideFlags.HideAndDontSave; } catch { }
            // v0.9.23 诊断：记录克隆 prefab 缩放（不强制，定位真凶）
            try
            {
                var tr = clone.transform;
                Plugin.L.LogInfo($"[TS][ScaleDiag][Clone] id={def.Id} prefab={clone.name} cloneScale={tr.localScale.x:F3},{tr.localScale.y:F3},{tr.localScale.z:F3} template={template.name} tplScale={template.transform.localScale.x:F3},{template.transform.localScale.y:F3},{template.transform.localScale.z:F3}");
            }
            catch { }
            bool mainDone = false;
            var srs = clone.GetComponentsInChildren<SpriteRenderer>(true);
            // 诊断：记录克隆前全部 SR（定位绿叠加层归属）
            try
            {
                var sb0 = new System.Text.StringBuilder($"[TS][CloneSR] {def.SpriteKey} n={srs.Length} | ");
                for (int ci = 0; ci < srs.Length && ci < 12; ci++) { var sr = srs[ci]; if (sr==null) continue; sb0.Append($"[{ci}:{sr.name} sp={(sr.sprite!=null?sr.sprite.name:"null")} col={sr.color.r:F2},{sr.color.g:F2},{sr.color.b:F2} en={sr.enabled}] "); }
                Plugin.L.LogInfo(sb0.ToString());
            }
            catch { }
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
                if (sn.Contains("Cylinder") || sn.Contains("Parts") || sn.Contains("Fire") || sn.Contains("ChargingState"))
                    sr.enabled = false; // 零件/充电指示禁用（整机贴图已含细节；充电台模板自带 11 个 ChargingStateSprite 通电后绿覆盖，需屏蔽）
            }
            // 诊断：克隆后 SR 状态
            try
            {
                var sb1 = new System.Text.StringBuilder($"[TS][CloneSRAfter] {def.SpriteKey} | ");
                for (int ci = 0; ci < srs.Length && ci < 12; ci++) { var sr = srs[ci]; if (sr==null) continue; sb1.Append($"[{sr.name} sp={(sr.sprite!=null?sr.sprite.name:"null")} en={sr.enabled} col={sr.color.r:F2},{sr.color.g:F2},{sr.color.b:F2} mat={(sr.sharedMaterial!=null?sr.sharedMaterial.name:"null")}] "); }
                Plugin.L.LogInfo(sb1.ToString());
            }
            catch { }
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
