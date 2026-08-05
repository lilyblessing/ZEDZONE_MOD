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
    /// <summary>插入备注行的标记前缀，用于幂等判断（避免重复插入）。</summary>
    private const string NoteMarker = "<color=#FFFF00>备注：";

    private static bool _explored;

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
            if (string.IsNullOrEmpty(text) || text.Contains(NoteMarker))
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
        var item = FindItemByRect(target);
        if (item == null)
            return;

        string note = NoteTagStore.Get(item);
        if (string.IsNullOrEmpty(note))
            return;

        if (information.Contains(NoteMarker))
            return; // 已插入

        string newText = InsertNote(information, note);
        if (newText != information)
        {
            panel.informationText.text = newText;
            Plugin.L.LogInfo($"[NoteTag] tooltip 已插入备注: {note}");
        }
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

        lines.Insert(insertAt + 1, $"{NoteMarker}{note}</color>");
        return string.Join("\n", lines);
    }
}
