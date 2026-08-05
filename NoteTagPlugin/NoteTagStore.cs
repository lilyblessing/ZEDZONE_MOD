using System;

namespace NoteTagPlugin;

/// <summary>
/// 备注存储：写入 ItemData 的属性表（SetProperty/GetProperty）。
/// ItemData 的属性随游戏存档序列化 → 重载存档自动恢复，天然满足：
/// 1) 持久化（存档重载/重进游戏不丢失）
/// 2) 按物品实例单独绑定（每个 ItemData 实例的属性独立，同类型物品互不影响）
/// </summary>
public static class NoteTagStore
{
    /// <summary>属性键名（带独特前缀避免与游戏内部属性冲突）。</summary>
    public const string PropertyKey = "notetag_v1";

    public static string Get(ItemData item)
    {
        if (item == null) return "";
        try
        {
            var v = item.GetProperty(PropertyKey);
            return v ?? "";
        }
        catch
        {
            return "";
        }
    }

    public static void Set(ItemData item, string text)
    {
        if (item == null) return;
        try
        {
            item.SetProperty(PropertyKey, text ?? "");
        }
        catch (Exception e)
        {
            Plugin.L.LogError($"[NoteTag] SetProperty 失败: {e.Message}");
        }
    }

    public static bool Has(ItemData item)
    {
        if (item == null) return false;
        try
        {
            var v = item.GetProperty(PropertyKey);
            return !string.IsNullOrEmpty(v);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>当前内存中备注数（诊断用；实际存储走 ItemData 属性）。</summary>
    public static int Count => 0;
}
