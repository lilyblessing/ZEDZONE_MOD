using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NoteTagPlugin;

/// <summary>
/// 在游戏物品信息框（DescriptionTipPanel）展示时，按目标物品查备注并插入到信息文本中
/// （亮黄色，位于物品名/描述之后、其他信息之前）。
/// 双保险：patch ShowDescription（即时插入）+ patch Update（兜底，防游戏刷新重置文本）。
/// </summary>
public static class TooltipPatcher
{
    /// <summary>备注行标记前缀（按语言缓存，避免每帧字符串拼接），用于幂等判断。</summary>
    private static string _markerCache;
    private static bool _markerEnglish;

    private static string Marker()
    {
        bool en = Locale.IsEnglish();
        if (_markerCache == null || en != _markerEnglish)
        {
            _markerEnglish = en;
            _markerCache = "<color=#FFFF00>" + (en ? "Note: " : "备注：");
        }
        return _markerCache;
    }

    /// <summary>语言切换后调用：清 marker 缓存与 tooltip 目标缓存（下次访问重建）。</summary>
    public static void InvalidateLanguage()
    {
        _markerCache = null;
        InvalidateCache();
    }

    private static bool _explored;

    // ---- P0-1 热路径缓存：当前 tooltip 目标（目标指针 → 物品 → 备注）----
    // tooltip 面板激活期间 Update 每帧触发；目标不变时复用缓存，避免
    // 每帧遍历 ActiveObjects + GetProperty native 调用。
    private static long _cachedTargetPtr;
    private static ItemData _cachedItem;
    private static string _cachedNote;
    private static bool _cacheReady;

    /// <summary>备注保存/外部变更后调用，使缓存失效以便重新读取。</summary>
    public static void InvalidateCache()
    {
        _cacheReady = false;
        _cachedItem = null;
        _cachedNote = null;
    }

    public static void Apply(Harmony harmony)
    {
        var t = typeof(DescriptionTipPanel);
        var methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
        Plugin.L.LogInfo($"[NoteTag] DescriptionTipPanel 运行时方法数: {methods.Length}");
        foreach (var m in methods)
        {
            if (m.Name.Contains("ShowDescription") || m.Name == "Update" || m.Name == "ClosePanel")
                Plugin.L.LogInfo($"[NoteTag]   - 找到方法: {m.Name}");
        }

        var showDesc = t.GetMethod("ShowDescription", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (showDesc != null)
        {
            harmony.Patch(showDesc, postfix: new HarmonyMethod(
                typeof(TooltipPatcher).GetMethod(nameof(Postfix_ShowDescription), BindingFlags.NonPublic | BindingFlags.Static)));
            Plugin.L.LogInfo("[NoteTag] 已挂钩 DescriptionTipPanel.ShowDescription");
        }
        else
        {
            Plugin.L.LogWarning("[NoteTag] ShowDescription 反射失败（运行时无此方法），依赖 Update 兜底");
        }

        var update = t.GetMethod("Update", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (update != null)
        {
            harmony.Patch(update, postfix: new HarmonyMethod(
                typeof(TooltipPatcher).GetMethod(nameof(Postfix_Update), BindingFlags.NonPublic | BindingFlags.Static)));
            Plugin.L.LogInfo("[NoteTag] 已挂钩 DescriptionTipPanel.Update");
        }
        else
        {
            Plugin.L.LogWarning("[NoteTag] DescriptionTipPanel.Update 反射失败");
        }
    }

    // ---- ShowDescription 即时插入 ----
    private static void Postfix_ShowDescription(DescriptionTipPanel __instance, object[] __args)
    {
        try
        {
            if (__instance == null || __args == null || __args.Length < 2)
                return;

            var targetRect = __args[0] as RectTransform;
            var information = __args[1] as string;
            if (targetRect == null || string.IsNullOrEmpty(information))
                return;

            if (!_explored)
            {
                _explored = true;
                Plugin.L.LogInfo($"[NoteTag][探查] ShowDescription 原始文本 (target={targetRect.name}):\n---BEGIN---\n{information}\n---END---");
            }

            TryInsertNote(__instance, targetRect, information);
        }
        catch (Exception e)
        {
            Plugin.L.LogError($"[NoteTag] ShowDescription Postfix 异常: {e}");
        }
    }

    // ---- Update 兜底：文本被游戏重置后重新插入 ----
    private static void Postfix_Update(DescriptionTipPanel __instance)
    {
        try
        {
            if (__instance == null || __instance.informationText == null)
                return;

            var text = __instance.informationText.text;
            if (string.IsNullOrEmpty(text) || text.Contains(Marker()))
                return; // 无内容或已插入

            var target = __instance.targetRect;
            if (target == null)
                return;

            TryInsertNote(__instance, target, text);
        }
        catch (Exception e)
        {
            Plugin.L.LogError($"[NoteTag] Update Postfix 异常: {e}");
        }
    }

    private static void TryInsertNote(DescriptionTipPanel panel, RectTransform target, string information)
    {
        // 已插入则提前返回（省去目标查找与备注查询）
        if (information.Contains(Marker()))
            return;

        var item = GetCachedItem(target);
        if (item == null)
            return;

        string note = GetCachedNote(item);
        if (string.IsNullOrEmpty(note))
            return;

        string newText = InsertNote(information, note);
        if (newText != information)
        {
            panel.informationText.text = newText;
            Plugin.L.LogInfo($"[NoteTag] tooltip 已插入备注: {note}");
        }
    }

    /// <summary>按目标 RectTransform 取物品，带单条目缓存（目标不变时零遍历）。</summary>
    private static ItemData GetCachedItem(RectTransform target)
    {
        long ptr = target.Pointer.ToInt64();
        if (_cacheReady && ptr == _cachedTargetPtr && _cachedItem != null)
            return _cachedItem;

        // 缓存未命中：遍历查找并更新缓存（目标与备注缓存必须原子更新，防止错绑）
        var item = FindItemByRect(target);
        _cachedTargetPtr = ptr;
        _cachedItem = item;
        _cachedNote = null; // 关键：目标变化时清除备注缓存
        _cacheReady = true;
        return item;
    }

    /// <summary>读取物品备注，带单条目缓存（含空备注缓存，避免重复 native 调用）。</summary>
    private static string GetCachedNote(ItemData item)
    {
        if (_cacheReady && item == _cachedItem && _cachedNote != null)
            return _cachedNote;

        var note = NoteTagStore.Get(item);
        if (_cacheReady && item == _cachedItem)
            _cachedNote = note; // 空备注也缓存，防止每帧 GetProperty
        return note;
    }

    /// <summary>通过目标 RectTransform 反查对应的物品实例。</summary>
    private static ItemData FindItemByRect(RectTransform target)
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
                    return ui.itemdata;
            }
        }
        catch (Exception e)
        {
            Plugin.L.LogError($"[NoteTag] FindItemByRect 异常: {e}");
        }
        return null;
    }

    /// <summary>
    /// 把备注插入信息文本：优先插在第一个空行之后（描述与属性块之间的分隔处，
    /// 即"其他物品信息的上方"）；若无空行则插在第二行之后（物品名与描述之后）。
    /// </summary>
    private static string InsertNote(string info, string note)
    {
        var lines = new List<string>(info.Split('\n'));
        int insertAt = -1;
        for (int i = 1; i < lines.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                insertAt = i;
                break;
            }
        }
        if (insertAt < 0)
            insertAt = Math.Min(2, lines.Count);

        lines.Insert(insertAt + 1, $"{Marker()}{note}</color>");
        return string.Join("\n", lines);
    }
}
