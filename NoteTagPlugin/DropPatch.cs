using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine.EventSystems;

namespace NoteTagPlugin;

/// <summary>
/// 命名牌拖放交互（v2，规避 virtual 方法 patch 崩溃）：
/// HarmonyX 在 IL2CPP 下 patch virtual/final 方法（OnDrop/OnBeginDrag）会崩溃，
/// 因此改为 patch 非 virtual 的 DropOn(PointerEventData)（OnDrop 内部调用的放置方法）。
/// 拖拽源通过 PointerEventData.pointerDrag 获取（EventSystem 记录拖拽起始对象）。
/// </summary>
public static class DropPatch
{
    private const BindingFlags InstFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private const BindingFlags StaticFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    public static void Apply(Harmony harmony)
    {
        var t = typeof(BasicItemUI);

        var dropOn = t.GetMethod("DropOn", InstFlags);
        if (dropOn != null)
        {
            harmony.Patch(dropOn, prefix: new HarmonyMethod(typeof(DropPatch).GetMethod(nameof(Prefix_DropOn), StaticFlags)));
            Plugin.L.LogInfo("[NoteTag] 已挂钩 BasicItemUI.DropOn (非virtual, 安全)");
        }
        else
        {
            Plugin.L.LogError("[NoteTag] DropOn 反射失败，拖放功能不可用");
        }
    }

    private static bool Prefix_DropOn(BasicItemUI __instance, object[] __args)
    {
        try
        {
            if (__args == null || __args.Length < 1) return true;
            var eventData = __args[0] as PointerEventData;
            if (eventData == null) return true;

            // 拖拽源：EventSystem 记录的拖拽起始对象（DropOn 的 this 就是源格子）
            var srcGo = eventData.pointerDrag;
            if (srcGo == null) return true;
            var src = srcGo.GetComponent<BasicItemUI>();
            if (src == null || src.itemdata == null || src.itemdata.itemId != NameTagItem.ItemId)
                return true; // 非命名牌拖放，走游戏正常流程

            // 目标格子：从指针悬停对象中找（排除源自身）
            var targetUI = ItemSlotHelper.FindFromHovered(eventData, src);
            if (targetUI == null)
                return true; // 拖到格子外/自身：交给游戏
            var target = targetUI.itemdata;
            if (target == null)
                return true; // 空格子：交给游戏正常放置

            string targetName = "?";
            try { targetName = target.GetItemName(); } catch { }
            Plugin.L.LogInfo($"[NoteTag] 命名牌拖放到物品上: {targetName}");
            NoteTagUI.OpenForItem(target, src);
            // 拦截 DropOn 后必须恢复游戏拖拽状态：OnBeginDrag 把命名牌暂存进 itemdataTemp，
            // 若不清空，关闭背包时游戏会清理"未放置的拖拽物品"→ 命名牌整组消失。
            RestoreDrag(src);
            return false; // 拦截游戏默认放置/交换
        }
        catch (Exception e)
        {
            Plugin.L.LogError($"[NoteTag] DropOn Prefix 异常: {e}");
            return true;
        }
    }

    /// <summary>恢复游戏拖拽状态：调用 RestoreDraggedItemToSource 把 itemdataTemp 放回源格子。</summary>
    private static void RestoreDrag(BasicItemUI src)
    {
        try
        {
            if (RestoreDragMethod == null)
            {
                Plugin.L.LogWarning("[NoteTag] RestoreDraggedItemToSource 反射失败");
                return;
            }
            bool ok = (bool)RestoreDragMethod.Invoke(src, null);
            if (!ok) Plugin.L.LogWarning("[NoteTag] RestoreDraggedItemToSource 返回 false");
            Plugin.L.LogInfo($"[NoteTag] 拖拽状态恢复: {ok}");
        }
        catch (Exception e)
        {
            Plugin.L.LogError($"[NoteTag] 恢复拖拽状态失败: {e.Message}");
        }
    }

    private static readonly MethodInfo RestoreDragMethod =
        typeof(BasicItemUI).GetMethod("RestoreDraggedItemToSource", InstFlags);
}
