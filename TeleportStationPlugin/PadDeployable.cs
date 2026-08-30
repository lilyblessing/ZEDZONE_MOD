using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace TeleportStationPlugin;

/// <summary>
/// v0.7.0：传送站圆盘放置物注册（DeployableItem 体系——替代圆盘建筑，根治建筑 y-sort 盖玩家）。
/// 机制（参照睡袋先例 DeployableItem_Sleepingbag：Character 层 + 固定负 order，不参与建筑 y-sort）：
///   - ItemAttr_Deployable 实例注册（物品体系：制作 → 背包 → 放置到地面 → 可回收）；
///   - directionSprites[0] = pad 贴图（ppu 自适应 → 世界尺寸 ≈ 9.8×7，与建筑版一致）；
///   - recyclable=true（可拆下回收）；deployHP=200；
/// 电池仓（4×4）+ 慢充 + 供电检测 = 后续模块（P3 前），本版本先落地「放置物形态 + 渲染 + 放置/回收」。
/// itemId 自动分配（BaseItemId 起去冲突）；与旧建筑圆盘（900102）并存（旧档可继续拆除老盘迁移）。
/// </summary>
internal static class PadDeployable
{
    public const int BaseItemId = 900110;
    public static int ItemId = -1;
    public static bool Registered;

    public static bool Register()
    {
        if (Registered) return true;
        try
        {
            var mgr = ItemManager.instance;
            if (mgr == null)
            {
                Plugin.L.LogWarning("[TS] 圆盘放置物注册推迟（ItemManager 未就绪）");
                return false;
            }
            var dic = Reflect.Get(mgr, "itemAttrDic");
            if (dic == null)
            {
                Plugin.L.LogWarning("[TS] 圆盘放置物注册失败（itemAttrDic null）");
                return false;
            }

            int id = BaseItemId;
            int guard = 0;
            while (ItemRegistryHelper.DicContains(dic, id) && guard++ < 1000) id++;
            ItemId = id;

            var attr = CreateItemAttr(id);
            ItemRegistryHelper.DicAdd(dic, id, attr);
            ItemRegistryHelper.AddToCollection(Reflect.Get(mgr, "itemList"), attr);
            ItemRegistryHelper.AddToCollection(Reflect.Get(mgr, "allRecipeList"), CreateRecipe(id));
            // 物品栏图标（ModSpriteRegistry slot=Main）
            ItemRegistryHelper.RegisterSprite(Plugin.PluginDir, "textures/pad.png", id, "Main", 120, 100);

            Registered = true;
            Plugin.L.LogInfo($"[TS] 圆盘放置物注册成功: itemId={id}（Deployable 物品，方向贴图=pad，可回收）");
            return true;
        }
        catch (Exception e)
        {
            Plugin.L.LogWarning($"[TS] 圆盘放置物注册异常: {e.Message.Split('\n')[0]}");
            return false;
        }
    }

    private static ItemAttr CreateItemAttr(int id)
    {
        // directionSprites 需要实体贴图 sprite（与建筑版同源：textures/pad.png，ppu 自适应 → 世界 ≈9.8×7）
        if (!SpriteInjector.Cache.TryGetValue(900102, out var icon) || icon == null || icon.texture == null)
        {
            SpriteInjector.CacheSprite(Buildings.PadDef, force: true);
            SpriteInjector.Cache.TryGetValue(900102, out icon);
        }
        var dirSp = new Il2CppReferenceArray<Sprite>(1);
        if (icon != null && icon.texture != null)
        {
            var tex = icon.texture;
            var sp = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), tex.height / 7.0f);
            sp.name = "TeleportPad_Deploy_0";
            dirSp[0] = sp;
        }

        var attr = new ItemAttr_Deployable();
        Reflect.Set(attr, "itemId", id);
        string zh = "传送站圆盘";
        string en = "Teleport Pad";
        Reflect.Set(attr, "itemName", GameLocale.T(zh, en));
        Reflect.Set(attr, "itemName_Runtime", GameLocale.T(zh, en));
        string desc = "传送站圆盘：可停放车辆与人员，配合控制台实现远距离定点传送。";
        Reflect.Set(attr, "itemDescription", GameLocale.T(desc, desc));
        Reflect.Set(attr, "itemDescription_WithLanguage", GameLocale.T(desc, desc));
        Reflect.Set(attr, "itemSize", new Vector2Int(3, 2));
        Reflect.Set(attr, "stackNumber", 1);
        Reflect.Set(attr, "weight", 20f);
        Reflect.Set(attr, "itemPrice", 580f);
        Reflect.Set(attr, "itemType", ItemType.Deployable);
        Reflect.Set(attr, "toolType", ToolType.None);
        Reflect.Set(attr, "unlockByDefault", true);
        Reflect.Set(attr, "hiddenItem", false);
        // 放置物属性（睡袋同款配置基座；trigger 默认 None——纯静态放置物）
        Reflect.Set(attr, "recyclable", true);
        Reflect.Set(attr, "deployHP", 200f);
        Reflect.Set(attr, "directionSprites", dirSp);
        return attr;
    }

    /// <summary>制作配方（工作台；材料沿用原建筑方案）。
    /// L11 修复（2026-08-31）：recipeItems 必须用 Il2Cpp List——BCL List 经 Reflect.Set 类型不匹配会被静默丢弃（配方从未生效，即"材料未校验"根因）。</summary>
    private static RecipeData CreateRecipe(int id)
    {
        var recipe = new RecipeData();
        Reflect.Set(recipe, "itemId", id);
        Reflect.Set(recipe, "outputItemNumber", 1);
        var list = new Il2CppSystem.Collections.Generic.List<RecipeItemData>();
        list.Add(MakeMat(66, 24f));   // 合金板材
        list.Add(MakeMat(64, 20f));   // 合金零件
        list.Add(MakeMat(61, 24f));   // 机械元件
        list.Add(MakeMat(29, 12f));   // 精密零件
        list.Add(MakeMat(86, 1f));    // 高功率供电模块（电池）
        list.Add(MakeMat(1082, 1f));  // 工业电机
        Reflect.Set(recipe, "recipeItems", list);
        Reflect.Set(recipe, "craftPlatform", CraftPlatform.workbench);
        Reflect.Set(recipe, "toolType", ToolType.None);
        Reflect.Set(recipe, "iqReqiared", 0f);
        Reflect.Set(recipe, "craftReqiared", 0f);
        Reflect.Set(recipe, "craftTime", 0.05f); // 0.05 游戏天 ≈ 72 分钟
        Reflect.Set(recipe, "fatigueCost", 0f);
        Reflect.Set(recipe, "craftAudioClipType", 0);
        return recipe;
    }

    private static RecipeItemData MakeMat(int itemId, float number)
    {
        var m = new RecipeItemData();
        Reflect.Set(m, "itemId", itemId);
        Reflect.Set(m, "itemNumber", number);
        return m;
    }
}