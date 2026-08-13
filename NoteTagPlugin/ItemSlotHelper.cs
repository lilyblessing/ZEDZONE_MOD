using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;

namespace NoteTagPlugin;

/// <summary>
/// 背包格子查找辅助：统一封装 BasicItemUI 遍历与「格子 → 面板 → InventoryData」三级归属获取。
/// 踩坑 15/16：拖放期 ItemData.inventoryData 被游戏置 null，须从格子所属面板侧取归属。
/// </summary>
public static class ItemSlotHelper
{
    /// <summary>遍历激活格子，找到持有该 ItemData 的格子。</summary>
    public static BasicItemUI FindByItem(ItemData item)
    {
        if (item == null) return null;
        try
        {
            var list = BasicItemUI.ActiveObjects;
            if (list == null) return null;
            for (int i = 0; i < list.Count; i++)
            {
                var ui = list[i];
                if (ui == null || ui.itemdata == null) continue;
                if (ui.itemdata == item) return ui;
            }
        }
        catch { }
        return null;
    }

    /// <summary>按目标 RectTransform 反查对应的物品格子（tooltip 定位用）。</summary>
    public static BasicItemUI FindByRect(RectTransform target)
    {
        var list = BasicItemUI.ActiveObjects;
        if (list == null) return null;
        try
        {
            for (int i = 0; i < list.Count; i++)
            {
                var ui = list[i];
                if (ui == null || ui.itemdata == null) continue;
                var rt = ui.rectTransform;
                if (rt != null && rt.Pointer == target.Pointer)
                    return ui;
            }
        }
        catch (Exception e)
        {
            Plugin.L.LogError($"ItemSlotHelper.FindByRect 异常: {e}");
        }
        return null;
    }

    /// <summary>从 PointerEventData.hovered 中找到目标物品格子（排除拖拽源自身）。</summary>
    public static BasicItemUI FindFromHovered(PointerEventData eventData, BasicItemUI exclude)
    {
        var hovered = eventData?.hovered;
        if (hovered == null) return null;
        try
        {
            for (int i = 0; i < hovered.Count; i++)
            {
                var go = hovered[i];
                if (go == null) continue;
                var ui = go.GetComponent<BasicItemUI>();
                if (ui != null && ui != exclude)
                    return ui;
            }
        }
        catch (Exception e)
        {
            Plugin.L.LogError($"ItemSlotHelper.FindFromHovered 异常: {e}");
        }
        return null;
    }

    /// <summary>
    /// 取格子所属 InventoryData：格子 → inventoryPanel → inventoryData。
    /// 移除物品前先定位格子并取归属（移除后 itemdata 被清空无法再定位）。
    /// </summary>
    public static object GetInventoryOf(BasicItemUI slot)
    {
        if (slot == null) return null;
        try
        {
            var panel = Reflect.Get(slot, "inventoryPanel");
            if (panel != null) return Reflect.Get(panel, "inventoryData");
        }
        catch { }
        return null;
    }
}
