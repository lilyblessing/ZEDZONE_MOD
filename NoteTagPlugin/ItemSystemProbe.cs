using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace NoteTagPlugin;

/// <summary>
/// 运行时探查：枚举 ItemManager 的物品字典，找出木头/木炭等基础材料的 itemId，
/// 并确认 itemAttrDic 的类型与规模，为命名牌物品注册做准备。
/// </summary>
public static class ItemSystemProbe
{
    private static bool _ran;

    public static void RunOnce()
    {
        if (_ran) return;
        _ran = true;
        try
        {
            var mgr = ItemManager.instance;
            if (mgr == null)
            {
                Plugin.L.LogWarning("[NoteTag][探查] ItemManager.instance 为 null（可能未初始化），跳过");
                return;
            }
            Plugin.L.LogInfo($"[NoteTag][探查] ItemManager.instance = {mgr.Pointer.ToInt64():X}");

            var t = typeof(ItemManager);
            foreach (var fieldName in new[] { "itemAttrDic", "itemList", "materialList", "allRecipeList" })
            {
                var v = Reflect.Get(mgr, fieldName);
                if (v == null) { Plugin.L.LogInfo($"[NoteTag][探查] 成员 {fieldName}: 未找到或 null"); continue; }
                Plugin.L.LogInfo($"[NoteTag][探查] 成员 {fieldName}: 类型={v.GetType().FullName}");
                try
                {
                    var countProp = v.GetType().GetProperty("Count");
                    if (countProp != null)
                        Plugin.L.LogInfo($"[NoteTag][探查]   Count={countProp.GetValue(v)}");
                }
                catch (Exception e) { Plugin.L.LogInfo($"[NoteTag][探查]   Count 读取失败: {e.Message}"); }
            }

            // 枚举 itemAttrDic
            var dic = Reflect.Get(mgr, "itemAttrDic");
            var entries = EnumerateDict(dic, out string err);
            if (err != null)
            {
                Plugin.L.LogInfo($"[NoteTag][探查] itemAttrDic 枚举失败: {err}");
                return;
            }
            Plugin.L.LogInfo($"[NoteTag][探查] itemAttrDic 条目数: {entries.Count}");

            int maxId = 0;
            var targets = new List<string> { "木头", "木炭", "Wood", "Charcoal", "原木", "木" };
            foreach (var (id, name) in entries)
            {
                if (id > maxId) maxId = id;
                foreach (var kw in targets)
                {
                    if (name != null && name.Contains(kw, StringComparison.OrdinalIgnoreCase))
                    {
                        Plugin.L.LogInfo($"[NoteTag][探查] 匹配[{kw}] itemId={id} name={name}");
                        break;
                    }
                }
            }
            Plugin.L.LogInfo($"[NoteTag][探查] 现有最大 itemId = {maxId}");

            // 打印前 30 个条目
            Plugin.L.LogInfo("[NoteTag][探查] 前 30 个物品: " + string.Join(", ", entries.GetRange(0, Math.Min(30, entries.Count)).Select(e => $"{e.id}={e.name}")));
        }
        catch (Exception e)
        {
            Plugin.L.LogError($"[NoteTag][探查] 异常: {e}");
        }
    }

    private static List<(int id, string name)> EnumerateDict(object dic, out string error)
    {
        error = null;
        var result = new List<(int, string)>();
        if (dic == null) return result;
        try
        {
            var t = dic.GetType();
            var en = t.GetMethod("GetEnumerator", Type.EmptyTypes)?.Invoke(dic, null);
            if (en == null) { error = "无 GetEnumerator"; return result; }
            var enType = en.GetType();
            var moveNext = enType.GetMethod("MoveNext", Type.EmptyTypes);
            var current = enType.GetProperty("Current");
            var kvType = current?.PropertyType;
            var keyProp = kvType?.GetProperty("Key");
            var valProp = kvType?.GetProperty("Value");
            if (moveNext == null || current == null || keyProp == null || valProp == null)
            {
                error = $"枚举器结构异常: enType={enType.FullName}";
                return result;
            }
            int guard = 0;
            while (moveNext.Invoke(en, null) is true && guard++ < 10000)
            {
                var kv = current.GetValue(en);
                int id = Convert.ToInt32(keyProp.GetValue(kv));
                var attr = valProp.GetValue(kv);
                string name = GetItemNameReflect(attr);
                result.Add((id, name));
            }
        }
        catch (Exception e)
        {
            error = e.Message;
        }
        return result;
    }

    private static string GetItemNameReflect(object attr)
    {
        if (attr == null) return null;
        try
        {
            var p = attr.GetType().GetProperty("ItemName",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var v = p?.GetValue(attr);
            return v?.ToString();
        }
        catch { }
        return null;
    }
}
