using System;
using System.IO;
using System.Reflection;
using UnityEngine;
using Il2CppSystem.Collections.Generic;

namespace NoteTagPlugin;

/// <summary>
/// 命名牌物品注册（代码注入，绕过官方 mod 限制）：
/// - ItemAttr：物品定义（堆叠 32 / 重量 0.01 / 价格 1 / Material 类型 / 徒手工具）
/// - RecipeData：配方（木头×1 + 炭×1 = 命名牌×2，byhand 徒手，0 级）
/// - ModSpriteRegistry：贴图注册（Main slot）
/// 物品 ID 使用 mod 物品区间 100000+，启动时动态避开冲突。
/// </summary>
public static class NameTagItem
{
    public const int BaseItemId = 900000;
    public const string MainSlot = "Main";
    public const string ItemName = "命名牌";
    public const string ItemDescription = "为任意物品添加备注";
    public const string ItemName_EN = "Name Tag";
    public const string ItemDescription_EN = "Attach a note to any item";

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
                Plugin.L.LogWarning("[NoteTag] ItemManager 未就绪，命名牌注册推迟");
                return false;
            }

            var t = typeof(ItemManager);
            var dic = Reflect.Get(mgr, "itemAttrDic");
            if (dic == null)
            {
                Plugin.L.LogError("[NoteTag] itemAttrDic 为 null，无法注册命名牌");
                return false;
            }

            // 分配无冲突 ID（mod 物品区间 100000–999999）
            int id = BaseItemId;
            int guard = 0;
            while (DicContains(dic, id) && guard++ < 1000) id++;
            ItemId = id;

            // 物品定义
            var attr = CreateItemAttr(id);
            DicAdd(dic, id, attr);
            AddToCollection(Reflect.Get(mgr, "itemList"), attr);
            AddToCollection(Reflect.Get(mgr, "materialList"), attr);

            // 配方
            var recipe = CreateRecipe(id);
            AddToCollection(Reflect.Get(mgr, "allRecipeList"), recipe);

            // 贴图
            RegisterSprite(id);

            Registered = true;
            Plugin.L.LogInfo($"[NoteTag] 命名牌注册成功: itemId={id} (堆叠32 重0.01 价1 材料类 徒手)");
            return true;
        }
        catch (Exception e)
        {
            Plugin.L.LogError($"[NoteTag] 命名牌注册失败: {e}");
            return false;
        }
    }

    // ---------- 物品定义 ----------

    private static ItemAttr CreateItemAttr(int id)
    {
        var attr = new ItemAttr();
        _attr = attr;
        Reflect.Set(attr, "itemId", id);
        Reflect.Set(attr, "itemName", ItemName);
        Reflect.Set(attr, "itemName_Runtime", Locale.T(ItemName, ItemName_EN));
        Reflect.Set(attr, "itemDescription", ItemDescription);
        Reflect.Set(attr, "itemDescription_WithLanguage", Locale.T(ItemDescription, ItemDescription_EN));
        Reflect.Set(attr, "itemSize", new Vector2Int(1, 1));
        Reflect.Set(attr, "stackNumber", 32);
        Reflect.Set(attr, "weight", 0.01f);
        Reflect.Set(attr, "itemPrice", 1f);
        Reflect.Set(attr, "itemType", ItemType.Material);
        Reflect.Set(attr, "toolType", ToolType.None);
        Reflect.Set(attr, "unlockByDefault", true);
        Reflect.Set(attr, "hiddenItem", false);
        return attr;
    }

    /// <summary>游戏语言切换后重设命名牌物品名/描述（游戏不覆盖 mod 物品，须自管）。</summary>
    public static void ReapplyLanguage()
    {
        if (!Registered || _attr == null) return;
        try
        {
            Reflect.Set(_attr, "itemName_Runtime", Locale.T(ItemName, ItemName_EN));
            Reflect.Set(_attr, "itemDescription_WithLanguage", Locale.T(ItemDescription, ItemDescription_EN));
            Plugin.L.LogInfo($"[NoteTag] 语言切换重设物品文本: {Locale.T(ItemName, ItemName_EN)}");
        }
        catch (Exception e) { Plugin.L.LogError($"[NoteTag] 重设语言文本失败: {e.Message}"); }
    }

    // ---------- 配方 ----------

    private static RecipeData CreateRecipe(int id)
    {
        var recipe = new RecipeData();
        Reflect.Set(recipe, "itemId", id);
        Reflect.Set(recipe, "outputItemNumber", 2);

        var list = new List<RecipeItemData>();
        var mat1 = new RecipeItemData();
        Reflect.Set(mat1, "itemId", 0);   // 木头 wood
        Reflect.Set(mat1, "itemNumber", 1f);
        var mat2 = new RecipeItemData();
        Reflect.Set(mat2, "itemId", 6);   // 炭 charcoal
        Reflect.Set(mat2, "itemNumber", 1f);
        list.Add(mat1);
        list.Add(mat2);

        Reflect.Set(recipe, "recipeItems", list);
        Reflect.Set(recipe, "craftPlatform", CraftPlatform.byhand);
        Reflect.Set(recipe, "toolType", ToolType.None);
        Reflect.Set(recipe, "iqReqiared", 0f);
        Reflect.Set(recipe, "craftReqiared", 0f);
        // craftTime 单位 = 游戏天（1f = 24 小时；曾误设为 1f 显示一整天）
        // 3 游戏分钟 = 3 / 1440 天
        Reflect.Set(recipe, "craftTime", 3f / 1440f);
        Reflect.Set(recipe, "fatigueCost", 0f);
        Reflect.Set(recipe, "craftAudioClipType", 0);
        return recipe;
    }

    // ---------- 贴图 ----------

    private static void RegisterSprite(int id)
    {
        string path = Path.Combine(_pluginDir, "Name_Tag.png");
        if (!File.Exists(path))
        {
            Plugin.L.LogWarning($"[NoteTag] 贴图不存在: {path}");
            return;
        }
        var bytes = File.ReadAllBytes(path);
        var tex = new Texture2D(160, 160, TextureFormat.RGBA32, false);
        if (!ImageConversion.LoadImage(tex, bytes))
        {
            Plugin.L.LogWarning("[NoteTag] LoadImage 失败");
            return;
        }
        var sprite = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
        ModSpriteRegistry.Register(id, MainSlot, sprite);
        Plugin.L.LogInfo($"[NoteTag] 贴图注册完成: {tex.width}x{tex.height}");
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
