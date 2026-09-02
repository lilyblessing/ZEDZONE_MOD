using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace TeleportStationPlugin;

/// <summary>
/// P6.4 控制台劫持：复用原版通讯终端按F界面，仅替换三项菜单文字与行为。
/// P6.6 修复：Fallback 走 InteractManager.AddInteractObjectData 直建（原 AddEnterInteract 在 TerrainObject 上不存在，fallback 始终失败导致无F）；
///  + 挂钩 InteractManager.ClearAllInteract postfix 自动重建（Clear 后 OnPlayerEnterRange 不二次触发导致永久丢失，900101 非 Production 无补表覆盖）；
///  + 0.5s Tick 巡检补注（场景切块/重载后兜底）。
/// 根因：900101 克隆自 108 Furniture_Commu，其 OnPlayerEnterRange@0x180997AD0 会注册 3 个 InteractData（雇佣/上传/退出）。
/// 修复：Postfix 在原版注册后，定位 InteractManager.interactObjectDataList 中对应 GameObject 的 InteractObjectData，
///    清空其 interactDataList 并重建为“重命名/选择目的地(列表)/退出”，保留原版 InteractUI 容器与按键提示。
/// 回退：若定位失败，则直接构造 InteractObjectData + InteractData x3 并调用 AddInteractObjectData。
/// </summary>
public static class TeleportConsoleInteractFix
{
    private static bool _patched = false;
    private static Type _commuType;
    private static Type _interactMgrType;
    private static Type _interactDataType;
    private static Type _interactObjDataType;
    private static Type _interactDelegateType;
    private static FieldInfo _fInteractList;
    private static FieldInfo _fDataList;
    private static MethodInfo _mAddEnter;
    private static MethodInfo _mRemove;
    private static MethodInfo _mAddData;
    private static TerrainObject _currentConsole;
    private static float _nextTick = -1f;
    private const float TickInterval = 0.5f;

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
            // ClearAllInteract postfix 自动重建
            try
            {
                if (_interactMgrType != null)
                {
                    var clear = AccessTools.Method(_interactMgrType, "ClearAllInteract");
                    if (clear != null)
                    {
                        var post = new HarmonyMethod(typeof(TeleportConsoleInteractFix).GetMethod(nameof(ClearAllPostfix), BindingFlags.Public | BindingFlags.Static));
                        h.Patch(clear, postfix: post);
                        Plugin.L.LogInfo("[TS][Fix] 已挂钩 InteractManager.ClearAllInteract postfix (自动重建F)");
                    }
                }
            } catch (Exception e) { Plugin.L.LogWarning($"[TS][Fix] ClearAllInteract 挂钩异常: {e.Message.Split('\n')[0]}"); }
        }
        catch (Exception e) { Plugin.L.LogWarning($"[TS][Fix] 挂钩异常: {e.Message.Split('\n')[0]}"); }
    }

    private static void EnsureTypeCache()
    {
        try { if (_interactMgrType == null) _interactMgrType = AccessTools.TypeByName("InteractManager"); } catch {}
        try { if (_interactDataType == null) _interactDataType = AccessTools.TypeByName("InteractData"); } catch {}
        try { if (_interactObjDataType == null) _interactObjDataType = AccessTools.TypeByName("InteractObjectData"); } catch {}
        try { if (_interactDelegateType == null) _interactDelegateType = AccessTools.TypeByName("InteractManager+InteractDelegate") ?? AccessTools.TypeByName("InteractManager.InteractDelegate"); } catch {}
        try
        {
            if (_fInteractList == null && _interactMgrType != null)
                _fInteractList = _interactMgrType.GetField("interactObjectDataList", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        } catch {}
        try
        {
            if (_interactObjDataType != null)
                _fDataList = _interactObjDataType.GetField("interactDataList", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (_fDataList == null) _fDataList = AccessTools.TypeByName("InteractObjectData")?.GetField("interactDataList", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        } catch {}
        try
        {
            if (_mAddEnter == null) _mAddEnter = AccessTools.Method(typeof(TerrainObject), "AddEnterInteract");
            if (_mAddEnter == null && _commuType != null) _mAddEnter = AccessTools.Method(_commuType, "AddEnterInteract");
        } catch {}
        try { if (_mRemove == null && _interactMgrType != null) _mRemove = AccessTools.Method(_interactMgrType, "RemoveInteract", new Type[] { typeof(GameObject) }); } catch {}
        try
        {
            if (_mAddData == null && _interactMgrType != null && _interactObjDataType != null)
                _mAddData = AccessTools.Method(_interactMgrType, "AddInteractObjectData", new Type[] { _interactObjDataType });
            if (_mAddData == null && _interactMgrType != null)
                _mAddData = AccessTools.Method(_interactMgrType, "AddInteractObjectData");
        } catch {}
    }

    public static void CommuEnterPostfix(object __instance, object __0)
    {
        try
        {
            Plugin.L.LogInfo($"[TS][Fix] Postfix 触发 __instance={__instance?.GetType().Name} attr={(__instance as TerrainObject)?.attr?.id}");
            var t = __instance as TerrainObject;
            if (t == null || t.attr == null || t.attr.id != 900101) { Plugin.L.LogInfo($"[TS][Fix] 非900101 跳过 id={(t?.attr?.id.ToString()??"null")}"); return; }
            _currentConsole = t;
            Plugin.L.LogInfo($"[TS][Fix] Postfix 命中 900101 console={t.GetInstanceID()} 开始替换");
            EnsureTypeCache();
            // 尝试定位并替换 InteractData
            bool replaced = TryReplaceInteractData(t);
            if (!replaced)
            {
                Plugin.L.LogWarning($"[TS][Fix] TryReplace 失败，进入 FallbackDirect");
                bool fb = TryFallbackAddDirect(t);
                Plugin.L.LogInfo($"[TS][Fix] FallbackDirect 结果={fb}");
                if (!fb)
                {
                    bool fb2 = TryFallbackAdd(t);
                    Plugin.L.LogInfo($"[TS][Fix] FallbackAddEnter 结果={fb2}");
                }
            }
            else Plugin.L.LogInfo($"[TS][Fix] 900101 原版F菜单已替换为三项 console={t.GetInstanceID()}");
        } catch (Exception e) { Plugin.L.LogWarning($"[TS][Fix] Postfix 异常: {e.Message}"); }
    }

    public static void ClearAllPostfix()
    {
        try
        {
            EnsureTypeCache();
            Plugin.L.LogInfo("[TS][Fix] ClearAllInteract postfix 触发，尝试重建所有900101的F");
            // 延迟一帧后检查，避免刚Clear后立刻Add被同帧其他逻辑覆盖？直接同步重建
            ReAddAllMissing();
        } catch (Exception e) { Plugin.L.LogWarning($"[TS][Fix] ClearAllPostfix 异常: {e.Message.Split('\n')[0]}"); }
    }

    /// <summary>0.5s 轮询由 ChargerPadFix.Tick 调用</summary>
    public static void Tick()
    {
        try
        {
            float now = 0f;
            try { now = Time.unscaledTime; } catch { now = Time.realtimeSinceStartup; }
            if (now < _nextTick) return;
            _nextTick = now + TickInterval;
            EnsureTypeCache();
            ReAddAllMissing();
        } catch {}
    }

    private static void ReAddAllMissing()
    {
        try
        {
            EnsureTypeCache();
            if (_interactMgrType == null || _mAddData == null) return;
            List<TerrainObject> consoles = null;
            try { consoles = TeleportObjectCache.FindAllById(900101); } catch { return; }
            if (consoles == null || consoles.Count == 0) return;
            // 检查 interactObjectDataList 中是否已有
            var im = GetInteractManagerInstance();
            if (im == null) return;
            object listObj = GetInteractList(im);
            if (listObj == null) return;
            // 为每个 console 检查是否缺失
            foreach (var c in consoles)
            {
                if (c == null || c.gameObject == null) continue;
                bool has = HasInteractFor(listObj, c);
                if (has) continue;
                // 距离过滤：若玩家距离>30m则跳过，减少无意义注册
                try
                {
                    var player = GetPlayerTransform();
                    if (player != null)
                    {
                        float d2 = (c.transform.position - player.position).sqrMagnitude;
                        if (d2 > 900f) continue; // 30m
                    }
                } catch {}
                bool ok = TryFallbackAddDirect(c);
                if (ok) Plugin.L.LogInfo($"[TS][Fix] Tick/Clear 重建F成功 console={c.GetInstanceID()} pos={c.transform.position}");
            }
        } catch (Exception e) { Plugin.L.LogWarning($"[TS][Fix] ReAddAllMissing 异常: {e.Message.Split('\n')[0]}"); }
    }

    private static object GetInteractManagerInstance()
    {
        try
        {
            if (_interactMgrType == null) return null;
            var im = AccessTools.Property(_interactMgrType, "instance")?.GetValue(null) ?? AccessTools.Field(_interactMgrType, "instance")?.GetValue(null);
            if (im == null) im = _interactMgrType.GetProperty("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
            return im;
        } catch { return null; }
    }

    private static object GetInteractList(object im)
    {
        try
        {
            object listObj = null;
            try { listObj = _fInteractList?.GetValue(im); } catch {}
            if (listObj == null) listObj = Reflect.Get(im, "interactObjectDataList");
            return listObj;
        } catch { return null; }
    }

    private static bool HasInteractFor(object listObj, TerrainObject t)
    {
        try
        {
            int count = 0;
            try { count = Convert.ToInt32(Reflect.Get(listObj, "Count")); } catch { try { count = (int)listObj.GetType().GetProperty("Count").GetValue(listObj); } catch { return false; } }
            var getItem = listObj.GetType().GetMethod("get_Item") ?? listObj.GetType().GetMethod("Get");
            for (int i = 0; i < count; i++)
            {
                object data = null;
                try { if (getItem != null) data = getItem.Invoke(listObj, new object[] { i }); } catch { continue; }
                if (data == null) continue;
                object io = null;
                try { io = Reflect.Get(data, "interactObject"); } catch { try { io = data.GetType().GetField("interactObject").GetValue(data); } catch {} }
                if (io == null) continue;
                try { if (io is GameObject go && t.gameObject == go) return true; } catch {}
                try { if (io == (object)t) return true; } catch {}
                try { if (io is Component c && c.transform == t.transform) return true; } catch {}
                try { if (io is GameObject g2 && g2.transform == t.transform) return true; } catch {}
            }
        } catch {}
        return false;
    }

    private static Transform GetPlayerTransform()
    {
        try
        {
            var pcType = AccessTools.TypeByName("PlayerController") ?? AccessTools.TypeByName("HumanCharacterController");
            if (pcType != null)
            {
                var inst = AccessTools.Property(pcType, "instance")?.GetValue(null) ?? AccessTools.Field(pcType, "instance")?.GetValue(null);
                if (inst != null)
                {
                    var tr = AccessTools.Property(inst.GetType(), "transform")?.GetValue(inst) as Transform;
                    if (tr != null) return tr;
                    var go = (inst as Component)?.transform;
                    if (go != null) return go;
                }
            }
        } catch {}
        try
        {
            var cam = Camera.main;
            if (cam != null) return cam.transform;
        } catch {}
        return null;
    }

    private static bool TryReplaceInteractData(TerrainObject t)
    {
        try
        {
            if (_interactMgrType == null) { Plugin.L.LogWarning("[TS][Fix] _interactMgrType null"); return false; }
            var im = GetInteractManagerInstance();
            if (im == null) { Plugin.L.LogWarning("[TS][Fix] InteractManager.instance null"); return false; }
            object listObj = GetInteractList(im);
            if (listObj == null) { Plugin.L.LogWarning("[TS][Fix] interactObjectDataList null"); return false; }

            // 先创建3个新数据，成功后再清空旧列表（避免失败后留空）
            var nd1 = CreateInteractData("重命名传送站", "F", typeof(TeleportConsoleInteractFix).GetMethod(nameof(OnRename), BindingFlags.Public|BindingFlags.Static), t);
            var nd2 = CreateInteractData("选择传送目的地", "F", typeof(TeleportConsoleInteractFix).GetMethod(nameof(OnSelectList), BindingFlags.Public|BindingFlags.Static), t);
            var nd3 = CreateInteractData("退出", "F", typeof(TeleportConsoleInteractFix).GetMethod(nameof(OnExit), BindingFlags.Public|BindingFlags.Static), t);
            if (nd1 == null || nd2 == null || nd3 == null) { Plugin.L.LogWarning($"[TS][Fix] CreateInteractData 失败 nd1={nd1!=null} nd2={nd2!=null} nd3={nd3!=null}"); return false; }

            // 遍历 listObj 寻找匹配 t.gameObject
            object targetData = null;
            int count = 0;
            try { count = Convert.ToInt32(Reflect.Get(listObj, "Count")); } catch { try { count = (int)listObj.GetType().GetProperty("Count").GetValue(listObj); } catch { Plugin.L.LogWarning("[TS][Fix] list Count 获取失败"); } }
            Plugin.L.LogInfo($"[TS][Fix] 遍历 interactObjectDataList count={count} 寻找 console={t.GetInstanceID()}");
            var getItem = listObj.GetType().GetMethod("get_Item") ?? listObj.GetType().GetMethod("Get");
            for (int i = 0; i < count; i++)
            {
                object data = null;
                try { if (getItem != null) data = getItem.Invoke(listObj, new object[] { i }); else data = Reflect.Get(listObj, i.ToString()); } catch (Exception exi) { Plugin.L.LogWarning($"[TS][Fix] getItem[{i}] 异常 {exi.Message.Split('\n')[0]}"); continue; }
                if (data == null) continue;
                object io = null;
                try { io = Reflect.Get(data, "interactObject"); } catch { try { io = data.GetType().GetField("interactObject").GetValue(data); } catch {} }
                if (io == null) continue;
                bool match = false;
                try { if (io is GameObject go && t.gameObject == go) match = true; } catch {}
                try { if (io == (object)t) match = true; } catch {}
                try { if (io is Component c && c.transform == t.transform) match = true; } catch {}
                try { if (io is GameObject g2 && g2.transform == t.transform) match = true; } catch {}
                if (match) { targetData = data; Plugin.L.LogInfo($"[TS][Fix] 命中 targetData index={i} io={io.GetType().Name}"); break; }
            }
            if (targetData == null) { Plugin.L.LogWarning($"[TS][Fix] 未找到 targetData for console {t.GetInstanceID()}"); return false; }

            object dataList = null;
            try { dataList = Reflect.Get(targetData, "interactDataList"); } catch { try { dataList = targetData.GetType().GetField("interactDataList", BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance)?.GetValue(targetData); } catch {} }
            if (dataList == null) { Plugin.L.LogWarning("[TS][Fix] interactDataList null"); return false; }

            // 清空并添加
            try { var clear = dataList.GetType().GetMethod("Clear"); clear?.Invoke(dataList, null); Plugin.L.LogInfo($"[TS][Fix] 已清空旧 list，准备加入3项"); } catch (Exception e) { Plugin.L.LogWarning($"[TS][Fix] Clear 异常 {e.Message.Split('\n')[0]}"); }

            var add = dataList.GetType().GetMethod("Add");
            if (add == null) { Plugin.L.LogWarning("[TS][Fix] Add 方法未找到"); return false; }
            add.Invoke(dataList, new object[] { nd1 });
            add.Invoke(dataList, new object[] { nd2 });
            add.Invoke(dataList, new object[] { nd3 });
            Plugin.L.LogInfo($"[TS][Fix] 已重建3项菜单 for console {t.GetInstanceID()}");
            return true;
        } catch (Exception e) { Plugin.L.LogWarning($"[TS][Fix] TryReplace 异常: {e.Message}"); return false; }
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

    private static bool TryFallbackAddDirect(TerrainObject t)
    {
        try
        {
            EnsureTypeCache();
            if (_interactMgrType == null || _interactObjDataType == null || _mAddData == null) { Plugin.L.LogWarning("[TS][Fix] FallbackDirect 类型缺失"); return false; }
            var im = GetInteractManagerInstance();
            if (im == null) { Plugin.L.LogWarning("[TS][Fix] FallbackDirect instance null"); return false; }
            object listObj = GetInteractList(im);
            if (listObj != null && HasInteractFor(listObj, t)) { Plugin.L.LogInfo($"[TS][Fix] FallbackDirect 已存在，跳过 console={t.GetInstanceID()}"); return true; }
            if (_mRemove != null)
            {
                try { _mRemove.Invoke(im, new object[] { t.gameObject }); } catch {}
            }
            var nd1 = CreateInteractData("重命名传送站", "F", typeof(TeleportConsoleInteractFix).GetMethod(nameof(OnRename), BindingFlags.Public|BindingFlags.Static), t);
            var nd2 = CreateInteractData("选择传送目的地", "F", typeof(TeleportConsoleInteractFix).GetMethod(nameof(OnSelectList), BindingFlags.Public|BindingFlags.Static), t);
            var nd3 = CreateInteractData("退出", "F", typeof(TeleportConsoleInteractFix).GetMethod(nameof(OnExit), BindingFlags.Public|BindingFlags.Static), t);
            if (nd1 == null || nd2 == null || nd3 == null) { Plugin.L.LogWarning("[TS][Fix] FallbackDirect Create 失败"); return false; }
            // 构造 InteractObjectData
            object iod = null;
            try { iod = Activator.CreateInstance(_interactObjDataType); } catch (Exception e) { Plugin.L.LogWarning($"[TS][Fix] InteractObjectData 创建失败 {e.Message.Split('\n')[0]}"); return false; }
            // interactObject = t.gameObject
            try { Reflect.Set(iod, "interactObject", t.gameObject); } catch { try { _interactObjDataType.GetField("interactObject", BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance)?.SetValue(iod, t.gameObject); } catch {} }
            // interactDataList
            object dataList = null;
            try { dataList = Reflect.Get(iod, "interactDataList"); } catch {}
            if (dataList == null)
            {
                try { var f = _interactObjDataType.GetField("interactDataList", BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance); var listType = f.FieldType; dataList = Activator.CreateInstance(listType); Reflect.Set(iod, "interactDataList", dataList); } catch {}
            }
            if (dataList == null) { Plugin.L.LogWarning("[TS][Fix] FallbackDirect dataList 仍null"); return false; }
            var add = dataList.GetType().GetMethod("Add");
            if (add == null) { Plugin.L.LogWarning("[TS][Fix] FallbackDirect dataList.Add 未找到"); return false; }
            add.Invoke(dataList, new object[] { nd1 });
            add.Invoke(dataList, new object[] { nd2 });
            add.Invoke(dataList, new object[] { nd3 });
            // 可选：设置 interactRange 等默认值（若字段存在）
            try { var fR = _interactObjDataType.GetField("interactRange", BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance); if (fR != null) fR.SetValue(iod, 3f); } catch {}
            try { var fM = _interactObjDataType.GetField("maxPlayerInteractRange", BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance); if (fM != null) fM.SetValue(iod, 5f); } catch {}
            try { var fT = _interactObjDataType.GetField("interactType", BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance); if (fT != null) { var enumVal = Enum.ToObject(fT.FieldType, 0); fT.SetValue(iod, enumVal); } } catch {}
            // 添加到管理器
            try { _mAddData.Invoke(im, new object[] { iod }); } catch (Exception e) { Plugin.L.LogWarning($"[TS][Fix] AddInteractObjectData 异常 {e.Message.Split('\n')[0]}"); return false; }
            Plugin.L.LogInfo($"[TS][Fix] FallbackDirect AddInteractObjectData 成功 console={t.GetInstanceID()}");
            return true;
        } catch (Exception e) { Plugin.L.LogWarning($"[TS][Fix] FallbackDirect 异常: {e.Message.Split('\n')[0]}"); return false; }
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
            if (_mAddEnter == null) { Plugin.L.LogWarning("[TS][Fix] Fallback AddEnterInteract 方法仍null"); return false; }
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
