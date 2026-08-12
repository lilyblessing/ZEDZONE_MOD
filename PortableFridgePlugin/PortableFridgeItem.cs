using System;
using System.IO;
using UnityEngine;
using Il2CppSystem.Collections.Generic;

namespace PortableFridgePlugin;

/// <summary>
/// 便携小冰箱物品注册（代码注入）：
/// - 物品：ItemType.Backpack（内置容器），占用 (5,4)，堆叠 1，重量 2.8
/// - 制作配方：工作台(workbench=2)：铁块(8)×6 + 铁管(10)×4 + 铜丝(13)×4 → 1
/// - 修复配方：物品维修工具包(56)×1
/// - 贴图：Portable_Fridge.png（插件目录）
/// </summary>
public static class PortableFridgeItem
{
    public const int BaseItemId = 900100;
    public const string MainSlot = "Main";
    public const string ItemName = "便携小冰箱";
    public const string ItemDescription = "便携式保鲜冰箱。放入电瓶(1200WH)供电后，" +
                                           "可让箱内食物保持新鲜，满电可运转 5 天。";
    public const string ItemName_EN = "Portable Mini Fridge";
    public const string ItemDescription_EN = "A portable refrigeration box. Powered by a vehicle battery " +
                                             "(1200WH), it keeps food inside fresh for up to 5 days on a full charge.";

    // 配方材料
    public const int MatIronBlock = 8;      // 铁块
    public const int MatIronPipe = 10;      // 铁管
    public const int MatCopperWire = 13;    // 铜丝
    public const int RepairKit = 56;        // 物品维修工具包
    public const int BatteryId = 85;        // 电瓶

    // 容器规格（同弹药箱 532：10×8）
    public const int ContainerWidth = 10;
    public const int ContainerHeight = 8;

    // 电瓶型号（ItemFeature_Battery.batteryModel=5 → 电池仓只接受型号 5 的电瓶）
    public const int BatteryModel = 5;
    // 耗电 wattage（游戏单位，供 BatteryConsuming 配置使用）：实测标定 1 wattage = 23.97 WH/游戏天。
    // 目标 240 WH/游戏天（1200WH 电瓶用 5 天）→ wattage ≈ 10。
    public const float Wattage = 10f;
    /// <summary>插件手动扣电速率（WH/游戏天）= 标定换算 23.97 WH/天/单位 × wattage 10。</summary>
    public const float WattagePerDayFromWattage = 23.97f * Wattage; // ≈ 239.7

    /// <summary>实际注册到的物品 ID。</summary>
    public static int ItemId = -1;
    public static bool Registered;

    /// <summary>已注册的物品定义（语言切换时重设文本用）。</summary>
    private static ItemAttr _attr;

    private static string _pluginDir;

    public static void Initialize(string pluginDir)
    {
        _pluginDir = pluginDir;
    }

    public static bool Register()
    {
        if (Registered) return true;
        try
        {
            var mgr = ItemManager.instance;
            if (mgr == null)
            {
                Plugin.L.LogWarning("[PFridge] ItemManager 未就绪，注册推迟");
                return false;
            }

            var dic = Reflect.Get(mgr, "itemAttrDic");
            if (dic == null)
            {
                Plugin.L.LogError("[PFridge] itemAttrDic 为 null，无法注册");
                return false;
            }

            // 分配无冲突 ID
            int id = BaseItemId;
            int guard = 0;
            while (DicContains(dic, id) && guard++ < 1000) id++;
            ItemId = id;

            var attr = CreateItemAttr(id);
            DicAdd(dic, id, attr);
            AddToCollection(Reflect.Get(mgr, "itemList"), attr);

            // 制作配方（工作台）
            var recipe = CreateRecipe(id);
            AddToCollection(Reflect.Get(mgr, "allRecipeList"), recipe);

            // 修复配方（直接设 repairData）
            attr.repairData = CreateRepairData(id);

            // 贴图
            RegisterSprite(id);

            Registered = true;
            Plugin.L.LogInfo($"[PFridge] 便携小冰箱注册成功: itemId={id} (Backpack 10x8 工作台配方 修复=维修包x1)");
            return true;
        }
        catch (Exception e)
        {
            Plugin.L.LogError($"[PFridge] 注册失败: {e}");
            return false;
        }
    }

    // ---------- 物品定义 ----------

    private static ItemAttr CreateItemAttr(int id)
    {
        // Backpack 类型必须用 ItemAttr_Backpack 实例（游戏生成时会强转，
        // 用 ItemAttr 基类会 InvalidCastException 导致物品无法生成）
        var attr = new ItemAttr_Backpack();
        _attr = attr;
        Reflect.Set(attr, "itemId", id);
        // 四个语言字段都按当前语言填：英文模式下游戏物品名走「英文本地化表查不到 → 回退 itemName」路径，
        // 若 itemName 恒为中文则英文名不生效；itemName_Runtime/itemDescription_WithLanguage 是游戏直接读取的运行时文本。
        Reflect.Set(attr, "itemName", Locale.T(ItemName, ItemName_EN));
        Reflect.Set(attr, "itemName_Runtime", Locale.T(ItemName, ItemName_EN));
        Reflect.Set(attr, "itemDescription", Locale.T(ItemDescription, ItemDescription_EN));
        Reflect.Set(attr, "itemDescription_WithLanguage", Locale.T(ItemDescription, ItemDescription_EN));
        Reflect.Set(attr, "itemSize", new Vector2Int(5, 4));
        Reflect.Set(attr, "stackNumber", 1);
        Reflect.Set(attr, "weight", 2.8f);
        Reflect.Set(attr, "itemPrice", 280f);
        Reflect.Set(attr, "itemType", ItemType.Backpack);
        Reflect.Set(attr, "toolType", ToolType.None);
        Reflect.Set(attr, "unlockByDefault", true);
        Reflect.Set(attr, "hiddenItem", false);
        // 容器容量（同弹药箱 10×8）
        Reflect.Set(attr, "inventorySize", new Vector2Int(ContainerWidth, ContainerHeight));

        // 电池槽特性：BatteryBox（电池仓，接受型号5电瓶）+ BatteryConsuming（耗电）
        RegisterBatteryFeatures(attr);

        return attr;
    }

    /// <summary>游戏语言切换后重设小冰箱物品名/描述（游戏不覆盖 mod 物品，须自管）。</summary>
    public static void ReapplyLanguage()
    {
        if (!Registered || _attr == null) return;
        try
        {
            Reflect.Set(_attr, "itemName", Locale.T(ItemName, ItemName_EN));
            Reflect.Set(_attr, "itemName_Runtime", Locale.T(ItemName, ItemName_EN));
            Reflect.Set(_attr, "itemDescription", Locale.T(ItemDescription, ItemDescription_EN));
            Reflect.Set(_attr, "itemDescription_WithLanguage", Locale.T(ItemDescription, ItemDescription_EN));
            Plugin.L.LogInfo($"[PFridge] 语言切换重设物品文本: {Locale.T(ItemName, ItemName_EN)}");
        }
        catch (Exception e) { Plugin.L.LogError($"[PFridge] 重设语言文本失败: {e.Message}"); }
    }

    // ---------- 电池槽特性注册（参照手电筒91：BatteryBox + BatteryConsuming）----------

    private static void RegisterBatteryFeatures(ItemAttr attr)
    {
        // 电池仓菜单（安装/取出电池）由 BatteryConsuming 特性提供，必须注册。
        // 游戏会对 IsSwitchOn=true 的设备自动扣电——本插件每 tick 强制 IsSwitchOn=false，
        // 并由插件手动管理 BatterySlot0 电量（见 FridgeMonitor），扣电速率可控（240 WH/游戏天）。
        var feats = new List<ItemFeatureType>();
        feats.Add(ItemFeatureType.BatteryBox);
        feats.Add(ItemFeatureType.BatteryConsuming);
        Reflect.Set(attr, "itemFeatures", feats);

        // 2) itemFeatureDataDic：ItemFeatureType → ItemFeature 实例
        var dic = new Il2CppSystem.Collections.Generic.Dictionary<ItemFeatureType, ItemFeature>();

        var bb = new ItemFeature_BatteryBox();
        Reflect.Set(bb, "batteryModel", BatteryModel);
        Reflect.Set(bb, "batteryNumber", 1);
        dic.Add(ItemFeatureType.BatteryBox, bb);

        var bc = new ItemFeature_BatteryConsuming();
        Reflect.Set(bc, "wattage", Wattage);
        dic.Add(ItemFeatureType.BatteryConsuming, bc);

        Reflect.Set(attr, "itemFeatureDataDic", dic);
        var temp = new List<ItemFeature>();
        temp.Add(bb);
        temp.Add(bc);
        Reflect.Set(attr, "itemFeatureTemp", temp);

        // 3) itemFeatureConfigDatas：配置数据（含字段级配置）
        var cfgs = new List<ItemFeatureConfigData>();
        cfgs.Add(MakeFeatureConfig(ItemFeatureType.BatteryBox, "ItemFeature_BatteryBox",
            ("batteryModel", BatteryModel), ("batteryNumber", 1)));
        cfgs.Add(MakeFeatureConfig(ItemFeatureType.BatteryConsuming, "ItemFeature_BatteryConsuming",
            ("wattage", Wattage)));
        Reflect.Set(attr, "itemFeatureConfigDatas", cfgs);
    }

    private static ItemFeatureConfigData MakeFeatureConfig(ItemFeatureType type, string className, params (string, object)[] fields)
    {
        var cfg = new ItemFeatureConfigData();
        Reflect.Set(cfg, "featureType", type);
        Reflect.Set(cfg, "itemFeatureClassName", className);
        var fieldList = new List<ItemFeatureConfigData.ItemFeatureFieldConfigData>();
        foreach (var (name, val) in fields)
        {
            var fc = new ItemFeatureConfigData.ItemFeatureFieldConfigData();
            Reflect.Set(fc, "itemFeatureFieldName", name);
            Reflect.Set(fc, "itemFeatureFieldValue", val);
            fieldList.Add(fc);
        }
        Reflect.Set(cfg, "ItemFeatureFieldConfigDatas", fieldList);
        return cfg;
    }

    // ---------- 制作配方（工作台 workbench = 2）----------

    private static RecipeData CreateRecipe(int id)
    {
        var recipe = new RecipeData();
        Reflect.Set(recipe, "itemId", id);
        Reflect.Set(recipe, "outputItemNumber", 1);

        var list = new List<RecipeItemData>();
        list.Add(MakeMat(MatIronBlock, 6f));
        list.Add(MakeMat(MatIronPipe, 4f));
        list.Add(MakeMat(MatCopperWire, 4f));

        Reflect.Set(recipe, "recipeItems", list);
        Reflect.Set(recipe, "craftPlatform", CraftPlatform.workbench);   // 2
        Reflect.Set(recipe, "toolType", ToolType.None);
        Reflect.Set(recipe, "iqReqiared", 0f);
        Reflect.Set(recipe, "craftReqiared", 0f);
        // craftTime 单位 = 游戏天（1f = 24h = 1440 游戏分钟）；10 分钟
        Reflect.Set(recipe, "craftTime", 10f / 1440f);
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

    // ---------- 修复配方（物品维修工具包 ×1）----------

    private static RecipeData CreateRepairData(int id)
    {
        var rd = new RecipeData();
        Reflect.Set(rd, "itemId", id);
        Reflect.Set(rd, "outputItemNumber", 1);

        var list = new List<RecipeItemData>();
        list.Add(MakeMat(RepairKit, 1f));

        Reflect.Set(rd, "recipeItems", list);
        Reflect.Set(rd, "craftPlatform", CraftPlatform.byhand);
        Reflect.Set(rd, "toolType", ToolType.None);
        Reflect.Set(rd, "iqReqiared", 0f);
        Reflect.Set(rd, "craftReqiared", 0f);
        Reflect.Set(rd, "craftTime", 1f / 1440f);
        Reflect.Set(rd, "fatigueCost", 0f);
        Reflect.Set(rd, "craftAudioClipType", 0);
        return rd;
    }

    // ---------- 贴图 ----------

    private static void RegisterSprite(int id)
    {
        string path = Path.Combine(_pluginDir, "Portable_Fridge.png");
        if (!File.Exists(path))
        {
            Plugin.L.LogWarning($"[PFridge] 贴图不存在: {path}");
            return;
        }
        var bytes = File.ReadAllBytes(path);
        var tex = new Texture2D(128, 100, TextureFormat.RGBA32, false);
        if (!ImageConversion.LoadImage(tex, bytes))
        {
            Plugin.L.LogWarning("[PFridge] LoadImage 失败");
            return;
        }
        var sprite = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
        ModSpriteRegistry.Register(id, MainSlot, sprite);
        Plugin.L.LogInfo($"[PFridge] 贴图注册完成: {tex.width}x{tex.height}");
    }

    // ---------- 反射辅助 ----------

    private static bool DicContains(object dic, int key)
    {
        try
        {
            var m = dic.GetType().GetMethod("ContainsKey");
            return m != null && (bool)m.Invoke(dic, new object[] { key });
        }
        catch { return false; }
    }

    private static void DicAdd(object dic, int key, object value)
    {
        var m = dic.GetType().GetMethod("Add");
        if (m != null) m.Invoke(dic, new[] { key, value });
    }

    private static void AddToCollection(object collection, object item)
    {
        if (collection == null) return;
        var m = collection.GetType().GetMethod("Add");
        if (m != null) m.Invoke(collection, new[] { item });
    }
}

