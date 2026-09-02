using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace TeleportStationPlugin;

/// <summary>
/// P6.4 控制台劫持：复用原版通讯终端按F界面，仅替换三项菜单文字与行为。
/// 根因：900101 克隆自 108 Furniture_Commu，其 OnPlayerEnterRange@0x180997AD0 会注册 3 个 InteractData（雇佣/上传/退出）。
/// 修复：Postfix 在原版注册后，定位 InteractManager.interactObjectDataList 中对应 GameObject 的 InteractObjectData，
///    清空其 interactDataList 并重建为“重命名/选择目的地(列表)/退出”，保留原版 InteractUI 容器与按键提示。
/// 回退：若定位失败，则 RemoveInteract + 反射调用 AddEnterInteract 三次创建。
/// </summary>
public static class TeleportConsoleInteractFix
{
    private static bool _patched = false;
    private static Type _commuType;
    private static Type _interactMgrType;
    private static Type _interactDataType;
    private static Type _interactDelegateType;
    private static FieldInfo _fInteractList;
    private static FieldInfo _fDataList;
    private static MethodInfo _mAddEnter;
    private static MethodInfo _mRemove;
    private static TerrainObject _currentConsole;

    public static void EnsurePatch(Harmony h)
    {
        if (_patched) return;
        _patched = true;
        try { EnsureTypeCache(); } catch {}
        try
        {
            _commuType = AccessTools.TypeByName("TerrainObject_Furniture_Commu");
            if (_commuType == null) { Plugin.L.LogWarning("[TS][Fix] 未找到 TerrainObject_Furniture_Commu"); return; }
            var enter = AccessTools.Method(_commuType, "OnPlayerEnterRange");
            if (enter != null)
            {
                var pre = new HarmonyMethod(typeof(TeleportConsoleInteractFix).GetMethod(nameof(CommuEnterPrefix), BindingFlags.Public | BindingFlags.Static));
                var post = new HarmonyMethod(typeof(TeleportConsoleInteractFix).GetMethod(nameof(CommuEnterPostfix), BindingFlags.Public | BindingFlags.Static));
                h.Patch(enter, prefix: pre, postfix: post);
                Plugin.L.LogInfo("[TS][Fix] 已挂钩 Commu.OnPlayerEnterRange prefix+postfix (复用原版F界面)");
            }
            else Plugin.L.LogWarning("[TS][Fix] 未找到 Commu.OnPlayerEnterRange");
            var exit = AccessTools.Method(_commuType, "OnPlayerExitRange");
            if (exit == null) exit = AccessTools.Method(_commuType, "OnPlayerExitRange", new Type[0]);
            if (exit == null) exit = AccessTools.Method(_commuType, "OnPlayerExitRange", new Type[] { typeof(object) });
            if (exit == null) exit = AccessTools.Method(typeof(TerrainObject), "OnPlayerExitRange");
            if (exit != null)
            {
                try
                {
                    var pre = new HarmonyMethod(typeof(TeleportConsoleInteractFix).GetMethod(nameof(CommuExitPrefix), BindingFlags.Public | BindingFlags.Static));
                    h.Patch(exit, prefix: pre);
                    Plugin.L.LogInfo("[TS][Fix] 已挂钩 OnPlayerExitRange (清理)");
                } catch {}
            }
        }
        catch (Exception e) { Plugin.L.LogWarning($"[TS][Fix] 挂钩异常: {e.Message.Split('\n')[0]}"); }
    }

    private static void EnsureTypeCache()
    {
        try { if (_interactMgrType == null) _interactMgrType = AccessTools.TypeByName("InteractManager"); } catch {}
        try { if (_interactDataType == null) _interactDataType = AccessTools.TypeByName("InteractData"); } catch {}
        try { if (_interactDelegateType == null) _interactDelegateType = AccessTools.TypeByName("InteractManager+InteractDelegate") ?? AccessTools.TypeByName("InteractManager.InteractDelegate"); } catch {}
        try
        {
            if (_fInteractList == null && _interactMgrType != null)
                _fInteractList = _interactMgrType.GetField("interactObjectDataList", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        } catch {}
        try
        {
            if (_interactDataType != null)
                _fDataList = AccessTools.Field(AccessTools.TypeByName("InteractObjectData"), "interactDataList");
            if (_fDataList == null) _fDataList = AccessTools.TypeByName("InteractObjectData")?.GetField("interactDataList", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        } catch {}
        try
        {
            if (_mAddEnter == null) _mAddEnter = AccessTools.Method(typeof(TerrainObject), "AddEnterInteract");
            if (_mAddEnter == null && _commuType != null) _mAddEnter = AccessTools.Method(_commuType, "AddEnterInteract");
        } catch {}
        try { if (_mRemove == null && _interactMgrType != null) _mRemove = AccessTools.Method(_interactMgrType, "RemoveInteract", new Type[] { typeof(GameObject) }); } catch {}
    }

    public static void CommuEnterPostfix(object __instance, object __0)
    {
        try
        {
            var t = __instance as TerrainObject;
            if (t == null || t.attr == null || t.attr.id != 900101) return;
            _currentConsole = t;
            EnsureTypeCache();
            // 尝试定位并替换 InteractData
            bool replaced = TryReplaceInteractData(t);
            if (!replaced)
            {
                // 回退：Remove + AddEnterInteract
                TryFallbackAdd(t);
            }
            else Plugin.L.LogInfo($"[TS][Fix] 900101 原版F菜单已替换为三项 console={t.GetInstanceID()}");
        } catch (Exception e) { Plugin.L.LogWarning($"[TS][Fix] Postfix 异常: {e.Message.Split('\n')[0]}"); }
    }

    private static bool TryReplaceInteractData(TerrainObject t)
    {
        try
        {
            if (_interactMgrType == null) return false;
            var im = AccessTools.Property(_interactMgrType, "instance")?.GetValue(null) ?? AccessTools.Field(_interactMgrType, "instance")?.GetValue(null);
            if (im == null) im = _interactMgrType.GetProperty("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
            if (im == null) return false;
            object listObj = null;
            try { listObj = _fInteractList?.GetValue(im); } catch {}
            if (listObj == null) listObj = Reflect.Get(im, "interactObjectDataList");
            if (listObj == null) return false;

            // 遍历 listObj 寻找匹配 t.gameObject
            object targetData = null;
            int count = 0;
            try { count = Convert.ToInt32(Reflect.Get(listObj, "Count")); } catch { try { count = (int)listObj.GetType().GetProperty("Count").GetValue(listObj); } catch {} }
            var getItem = listObj.GetType().GetMethod("get_Item") ?? listObj.GetType().GetMethod("Get");
            for (int i = 0; i < count; i++)
            {
                object data = null;
                try { if (getItem != null) data = getItem.Invoke(listObj, new object[] { i }); else data = Reflect.Get(listObj, i.ToString()); } catch {}
                if (data == null) continue;
                object io = null;
                try { io = Reflect.Get(data, "interactObject"); } catch { try { io = data.GetType().GetField("interactObject").GetValue(data); } catch {} }
                if (io == null) continue;
                bool match = false;
                try { if (io is GameObject go && t.gameObject == go) match = true; } catch {}
                try { if (io == (object)t) match = true; } catch {}
                try { if (io.ToString().Contains(t.GetInstanceID().ToString())) match = true; } catch {}
                // 兜底：比较 Transform
                try { if (io is Component c && c.transform == t.transform) match = true; } catch {}
                try { if (io is GameObject g2 && g2.transform == t.transform) match = true; } catch {}
                if (match) { targetData = data; break; }
            }
            if (targetData == null) return false;

            object dataList = null;
            try { dataList = Reflect.Get(targetData, "interactDataList"); } catch { try { dataList = targetData.GetType().GetField("interactDataList", BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance)?.GetValue(targetData); } catch {} }
            if (dataList == null) return false;

            // 清空
            try { var clear = dataList.GetType().GetMethod("Clear"); clear?.Invoke(dataList, null); } catch {}

            // 创建3个新 InteractData
            var nd1 = CreateInteractData("重命名传送站", "F", typeof(TeleportConsoleInteractFix).GetMethod(nameof(OnRename), BindingFlags.Public|BindingFlags.Static), t);
            var nd2 = CreateInteractData("选择传送目的地", "F", typeof(TeleportConsoleInteractFix).GetMethod(nameof(OnSelectList), BindingFlags.Public|BindingFlags.Static), t);
            var nd3 = CreateInteractData("退出", "F", typeof(TeleportConsoleInteractFix).GetMethod(nameof(OnExit), BindingFlags.Public|BindingFlags.Static), t);
            if (nd1 == null || nd2 == null || nd3 == null) return false;
            var add = dataList.GetType().GetMethod("Add");
            if (add == null) return false;
            add.Invoke(dataList, new object[] { nd1 });
            add.Invoke(dataList, new object[] { nd2 });
            add.Invoke(dataList, new object[] { nd3 });
            return true;
        } catch (Exception e) { Plugin.L.LogWarning($"[TS][Fix] TryReplace 异常: {e.Message.Split('\n')[0]}"); return false; }
    }

    private static object CreateInteractData(string str, string btn, MethodInfo handler, TerrainObject t)
    {
        try
        {
            object nd = null;
            if (_interactDataType != null) try { nd = Activator.CreateInstance(_interactDataType); } catch {}
            if (nd == null) nd = new InteractData(); // fallback if type available compile-time
            if (nd == null) return null;
            // 设置字段 via Reflect
            try { Reflect.Set(nd, "interactStr", str); } catch { try { nd.GetType().GetField("interactStr").SetValue(nd, str); } catch {} }
            try { Reflect.Set(nd, "interactButtonName", btn); } catch { try { nd.GetType().GetField("interactButtonName").SetValue(nd, btn); } catch {} }
            try { Reflect.Set(nd, "holdingTime", 0f); } catch { try { nd.GetType().GetField("holdingTime").SetValue(nd, 0f); } catch {} }
            try { Reflect.Set(nd, "interactObjectTemp", t); } catch {}
            // delegate
            object del = null;
            try
            {
                if (_interactDelegateType != null && handler != null)
                    del = Delegate.CreateDelegate(_interactDelegateType, handler);
                else
                    del = Delegate.CreateDelegate(typeof(InteractManager.InteractDelegate), handler);
            } catch { try { del = Delegate.CreateDelegate(_interactDelegateType, handler); } catch {} }
            if (del != null) try { Reflect.Set(nd, "interactAction", del); } catch { try { nd.GetType().GetField("interactAction").SetValue(nd, del); } catch {} }
            return nd;
        } catch { return null; }
    }

    private static bool TryFallbackAdd(TerrainObject t)
    {
        try
        {
            EnsureTypeCache();
            var im = _interactMgrType != null ? AccessTools.Property(_interactMgrType, "instance")?.GetValue(null) : null;
            if (im == null && _interactMgrType != null) im = _interactMgrType.GetField("instance")?.GetValue(null);
            if (im != null && _mRemove != null)
            {
                try { _mRemove.Invoke(im, new object[] { t.gameObject }); } catch {}
            }
            if (_mAddEnter == null) return false;
            var del1 = CreateDelegateFor(typeof(TeleportConsoleInteractFix).GetMethod(nameof(OnRename), BindingFlags.Public|BindingFlags.Static));
            var del2 = CreateDelegateFor(typeof(TeleportConsoleInteractFix).GetMethod(nameof(OnSelectList), BindingFlags.Public|BindingFlags.Static));
            var del3 = CreateDelegateFor(typeof(TeleportConsoleInteractFix).GetMethod(nameof(OnExit), BindingFlags.Public|BindingFlags.Static));
            if (del1 == null || del2 == null || del3 == null) return false;
            _mAddEnter.Invoke(t, new object[] { "重命名传送站", del1, "F" });
            _mAddEnter.Invoke(t, new object[] { "选择传送目的地", del2, "F" });
            _mAddEnter.Invoke(t, new object[] { "退出", del3, "F" });
            Plugin.L.LogInfo($"[TS][Fix] Fallback AddEnterInteract 900101 三项已注入");
            return true;
        } catch (Exception e) { Plugin.L.LogWarning($"[TS][Fix] Fallback 异常: {e.Message.Split('\n')[0]}"); return false; }
    }

    private static object CreateDelegateFor(MethodInfo mi)
    {
        try
        {
            if (_interactDelegateType != null) return Delegate.CreateDelegate(_interactDelegateType, mi);
            return Delegate.CreateDelegate(typeof(InteractManager.InteractDelegate), mi);
        } catch { return null; }
    }

    public static void OnRename(object obj)
    {
        try
        {
            // 关闭 InteractUI
            try { var uiType = AccessTools.TypeByName("InteractUI"); var inst = AccessTools.Property(uiType, "instance")?.GetValue(null); var m = uiType?.GetMethod("ClosePanel"); m?.Invoke(inst, null); } catch {}
            try { var t = AccessTools.TypeByName("InteractUI_TerrainObject"); var inst2 = AccessTools.Property(t, "instance")?.GetValue(null); var m2 = t?.GetMethod("ClosePanel"); m2?.Invoke(inst2, null); } catch {}
            var c = _currentConsole;
            if (c == null) { Plugin.L.LogWarning("[TS][Fix] OnRename 无 console"); return; }
            TeleportStationRenameUI.EnsureExists().Show(c);
            Plugin.L.LogInfo($"[TS][Fix] OnRename console={c.GetInstanceID()}");
        } catch (Exception e) { Plugin.L.LogWarning($"[TS][Fix] OnRename 异常: {e.Message.Split('\n')[0]}"); }
    }

    public static void OnSelectList(object obj)
    {
        try
        {
            try { var uiType = AccessTools.TypeByName("InteractUI"); var inst = AccessTools.Property(uiType, "instance")?.GetValue(null); var m = uiType?.GetMethod("ClosePanel"); m?.Invoke(inst, null); } catch {}
            var c = _currentConsole;
            if (c == null) { Plugin.L.LogWarning("[TS][Fix] OnSelectList 无 console"); return; }
            TeleportConsoleUI.EnsureExists().ShowForConsole(c);
            Plugin.L.LogInfo($"[TS][Fix] OnSelectList console={c.GetInstanceID()} -> 打开站列表");
        } catch (Exception e) { Plugin.L.LogWarning($"[TS][Fix] OnSelectList 异常: {e.Message.Split('\n')[0]}"); }
    }

    public static void OnExit(object obj)
    {
        try { var uiType = AccessTools.TypeByName("InteractUI"); var inst = AccessTools.Property(uiType, "instance")?.GetValue(null); var m = uiType?.GetMethod("ClosePanel"); m?.Invoke(inst, null); } catch {}
        Plugin.L.LogInfo("[TS][Fix] OnExit");
    }

    public static bool CommuExitPrefix(object __instance)
    {
        return true;
    }
    public static bool CommuLeavePrefix(object __instance, object __0) { return true; }
    public static bool ComputerEnterPrefix(object __instance, object __0) { return true; }
    public static bool ComputerOpenPrefix(object __instance, object m_computerData, object m_computer) { return true; }
    public static bool InteractOpenPrefix(object __instance, GameObject go, string str, object del) { return true; }
    // 兼容旧 prefix 签名（保留但不阻断）
    public static bool CommuEnterPrefix(object __instance, object __0) { return true; }
}
