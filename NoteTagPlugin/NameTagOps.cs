using System;
using System.Collections.Generic;
using System.Reflection;

namespace NoteTagPlugin;

/// <summary>命名牌消耗业务：扣减数量/整体移除/刷新面板（与 UI 渲染解耦）。</summary>
public static class NameTagOps
{
    // P1-2: 反射缓存（一次性查找，避免每次消耗/刷新重复 GetMethod）
    private static readonly MethodInfo RemoveItemMethod =
        typeof(InventoryData).GetMethod("RemoveItem",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            null, new[] { typeof(ItemData), typeof(bool) }, null);
    private static readonly Dictionary<Type, MethodInfo> PanelRefreshCache = new Dictionary<Type, MethodInfo>();

    /// <summary>消耗 1 个命名牌；数量耗尽时用游戏原生移除逻辑清空格子。</summary>
    public static void ConsumeNameTag(ItemData item, object inv)
    {
        try
        {
            if (item == null)
            {
                Plugin.L.LogWarning("消耗失败: 源物品为空");
                return;
            }

            // 移除/刷新前先定位持有该物品的格子与所属面板（移除后 itemdata 被清空无法再定位）
            var slotUI = ItemSlotHelper.FindByItem(item);
            object panel = slotUI != null ? Reflect.Get(slotUI, "inventoryPanel") : null;

            if (item.itemNumberFloat > 1f)
            {
                // 数量 >1：减 1 并刷新数量显示（FindSlotOf 已定位格子，直接刷新）
                item.itemNumberFloat -= 1f;
                if (slotUI != null) { try { slotUI.RefreshItemNumber(); } catch { } }
            }
            else
            {
                // 只剩 1 个：整体移除（数据 + UI 刷新）
                // 拖放后 item.inventoryData 为 null（游戏拖拽期间清空归属）：
                // 从 格子 → 所属面板 → 面板的 inventoryData 拿正确归属
                object panelInv = ItemSlotHelper.GetInventoryOf(slotUI);
                var effectiveInv = panelInv ?? item.inventoryData ?? inv;

                bool removed = false;
                if (effectiveInv != null && RemoveItemMethod != null)
                {
                    try { removed = (bool)RemoveItemMethod.Invoke(effectiveInv, new object[] { item, true }); }
                    catch (Exception e) { Plugin.L.LogError($"RemoveItem(true) 异常: {e.Message}"); }
                    if (!removed)
                    {
                        try { removed = (bool)RemoveItemMethod.Invoke(effectiveInv, new object[] { item, false }); }
                        catch (Exception e) { Plugin.L.LogError($"RemoveItem(false) 异常: {e.Message}"); }
                    }
                }

                // 无论移除结果，刷新所属面板清除残留图标
                RefreshPanel(panel);
            }
            Plugin.L.LogInfo("已消耗 1 个命名牌");
        }
        catch (Exception e)
        {
            Plugin.L.LogError($"消耗命名牌失败: {e}");
        }
    }

    /// <summary>刷新背包面板（反射调 Refresh，按类型缓存 MethodInfo）。</summary>
    private static void RefreshPanel(object panel)
    {
        if (panel == null) return;
        try
        {
            var t = panel.GetType();
            if (PanelRefreshCache.TryGetValue(t, out var m) && m != null)
            {
                m.Invoke(panel, null);
                return;
            }
            var m2 = t.GetMethod("Refresh",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (m2 != null)
            {
                PanelRefreshCache[t] = m2;
                m2.Invoke(panel, null);
            }
        }
        catch (Exception e)
        {
            Plugin.L.LogError($"背包面板刷新失败: {e.Message}");
        }
    }

    /// <summary>取物品显示名（容错）。</summary>
    public static string GetItemName(ItemData d)
    {
        try { return d.GetItemName(); }
        catch { return "?"; }
    }
}
