using System;
using System.IO;
using UnityEngine;

namespace ZedZoneShared;

/// <summary>
/// 物品注册反射辅助（Il2Cpp 集合的 Add/ContainsKey 反射调用）与贴图注册（png → Sprite → ModSpriteRegistry）。
/// 日志经 SharedLog 注入。
/// </summary>
public static class ItemRegistryHelper
{
    public static bool DicContains(object dic, int key)
    {
        try
        {
            var m = dic.GetType().GetMethod("ContainsKey");
            return m != null && (bool)m.Invoke(dic, new object[] { key });
        }
        catch { return false; }
    }

    public static void DicAdd(object dic, int key, object value)
    {
        var m = dic.GetType().GetMethod("Add");
        if (m != null) m.Invoke(dic, new[] { key, value });
    }

    public static void AddToCollection(object collection, object item)
    {
        if (collection == null) return;
        var m = collection.GetType().GetMethod("Add");
        if (m != null) m.Invoke(collection, new[] { item });
    }

    /// <summary>加载插件目录下的贴图并注册到 ModSpriteRegistry（Main slot）。</summary>
    public static void RegisterSprite(string pluginDir, string fileName, int itemId, string slot, int texWidth, int texHeight)
    {
        string path = Path.Combine(pluginDir, fileName);
        if (!File.Exists(path))
        {
            SharedLog.Warning($"贴图不存在: {path}");
            return;
        }
        var bytes = File.ReadAllBytes(path);
        var tex = new Texture2D(texWidth, texHeight, TextureFormat.RGBA32, false);
        if (!ImageConversion.LoadImage(tex, bytes))
        {
            SharedLog.Warning("LoadImage 失败");
            return;
        }
        var sprite = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
        ModSpriteRegistry.Register(itemId, slot, sprite);
        SharedLog.Info($"贴图注册完成: {tex.width}x{tex.height}");
    }
}
