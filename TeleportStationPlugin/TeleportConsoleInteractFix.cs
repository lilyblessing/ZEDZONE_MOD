using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Il2CppInterop.Runtime.InteropTypes;

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
    private static Type _playerCtrlType;
    private static Type _humanCtrlType;
    private static Type _interactUIType;
    private static Type _interactUITerrainType;
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

    private static bool _typeCacheDone = false;
    private static Type SafeTypeByName(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        try
        {
            var asm = typeof(TerrainObject).Assembly;
            var t = asm.GetType(name);
            if (t != null) return t;
            try { foreach (var tp in asm.GetTypes()) if (tp.Name == name || tp.FullName == name) return tp; } catch {}
        } catch {}
        try
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                string fn = a.FullName;
                if (fn.StartsWith("UnityEngine.")) continue;
                if (fn.StartsWith("Unity.")) continue;
                if (fn.StartsWith("mscorlib")) continue;
                if (fn.StartsWith("System.")) continue;
                try { var t = a.GetType(name); if (t != null) return t; } catch {}
                try
                {
                    Type[] types = null;
                    try { types = a.GetTypes(); } catch (System.Reflection.ReflectionTypeLoadException ex) { types = ex.Types; }
                    if (types == null) continue;
                    foreach (var tp in types) if (tp != null && (tp.Name == name || tp.FullName == name || tp.FullName == name.Replace("+","."))) return tp;
                } catch {}
            }
        } catch {}
        return null;
    }

    private static void EnsureTypeCache()
    {
        if (_typeCacheDone) return;
        _typeCacheDone = true;
        try { if (_interactMgrType == null) _interactMgrType = SafeTypeByName("InteractManager"); } catch {}
        try { if (_interactDataType == null) _interactDataType = SafeTypeByName("InteractData"); } catch {}
        try { if (_interactObjDataType == null) _interactObjDataType = SafeTypeByName("InteractObjectData"); } catch {}
        try { if (_interactDelegateType == null) _interactDelegateType = SafeTypeByName("InteractManager+InteractDelegate") ?? SafeTypeByName("InteractManager.InteractDelegate"); } catch {}
        try { if (_humanCtrlType == null) _humanCtrlType = SafeTypeByName("HumanCharacterController"); } catch {}
        try
        {
            if (_fInteractList == null && _interactMgrType != null)
                _fInteractList = _interactMgrType.GetField("interactObjectDataList", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        } catch {}
        try
        {
            if (_interactObjDataType != null)
                _fDataList = _interactObjDataType.GetField("interactDataList", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        } catch {}
        try
        {
            if (_mAddData == null && _interactMgrType != null && _interactObjDataType != null)
                _mAddData = _interactMgrType.GetMethod("AddInteractObjectData", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new Type[] { _interactObjDataType }, null);
            if (_mAddData == null && _interactMgrType != null)
                _mAddData = _interactMgrType.GetMethod("AddInteractObjectData", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        } catch {}
        try { if (_mRemove == null && _interactMgrType != null) _mRemove = _interactMgrType.GetMethod("RemoveInteract", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new Type[] { typeof(GameObject) }, null); } catch {}
    }

    public static void OnTeleportConsoleInteract(object obj)
    {
        try
        {
            var c = _currentConsole;
            if (c == null || c.attr == null || c.attr.id != 900101)
            {
                try
                {
                    var list = TeleportObjectCache.FindAllById(900101);
                    var player = GetPlayerTransform();
                    if (player != null && list != null)
                    {
                        float best = 25f;
                        TerrainObject bestObj = null;
                        foreach (var t in list)
                        {
                            if (t == null || t.transform == null) continue;
                            float d2 = (t.transform.position - player.position).sqrMagnitude;
                            if (d2 < best) { best = d2; bestObj = t; }
                        }
                        if (bestObj != null) c = bestObj;
                    }
                    else if (list != null && list.Count > 0) c = list[0];
                } catch {}
            }
            if (c == null) return;
            try { TeleportConsoleMenuUI.EnsureExists().ShowForConsole(c); } catch (Exception e) { Plugin.L.LogWarning($"[TS][Fix] menu show fail {e.Message.Split('\n')[0]}"); }
            Plugin.L.LogInfo($"[TS][Fix] 900101 delegate hijacked -> teleport menu console={c.GetInstanceID()}");
        } catch (Exception e) { try { Plugin.L.LogWarning($"[TS][Fix] OnTeleportConsoleInteract err {e.Message.Split('\n')[0]}"); } catch {} }
    }

    public static void CommuEnterPostfix(object __instance, object __0)
    {
        try
        {
            var t = __instance as TerrainObject;
            if (t == null || t.attr == null || t.attr.id != 900101) return;
            if (t.transform == null || t.gameObject == null) { try { Plugin.L.LogWarning("[TS][Fix] t.transform/gameObject null, skip hijack"); } catch {} return; }
            _currentConsole = t;
            EnsureTypeCache();
            Plugin.L.LogInfo($"[TS][Fix] CommuEnterPostfix hit console={t.GetInstanceID()} pos={t.transform.position} delegateType={_interactDelegateType?.FullName ?? "null"}");
            try
            {
                var im = GetInteractManagerInstance();
                if (im == null) { Plugin.L.LogWarning("[TS][Fix] GetInteractManagerInstance null"); return; }
                object listObj = GetInteractList(im);
                if (listObj == null) { Plugin.L.LogWarning("[TS][Fix] GetInteractList null"); return; }
                int count = 0;
                try { count = Convert.ToInt32(Reflect.Get(listObj, "Count")); } catch { try { count = (int)listObj.GetType().GetProperty("Count").GetValue(listObj); } catch {} }
                var getItem = listObj.GetType().GetMethod("get_Item") ?? listObj.GetType().GetMethod("Get");
                var tTrans = t.transform; int tId = 0; int goId = 0; Vector3 tPos = Vector3.zero;
                try { tId = tTrans!=null? tTrans.GetInstanceID():0; } catch {}
                try { goId = t.gameObject!=null? t.gameObject.GetInstanceID():0; } catch {}
                try { tPos = tTrans!=null? tTrans.position: Vector3.zero; } catch {}
                Plugin.L.LogInfo($"[TS][Fix] interactObjectDataList count={count} tId={tId} goId={goId} tPos={tPos}");
                for (int i=0;i<count;i++)
                {
                    object data=null; try { if (getItem!=null) data=getItem.Invoke(listObj,new object[]{i}); } catch (Exception ex) { Plugin.L.LogWarning($"[TS][Fix] getItem {i} fail {ex.Message.Split('\n')[0]}"); continue; }
                    if (data==null) continue;
                    object io=null; try { io=Reflect.Get(data,"interactObject"); } catch { try { io=data.GetType().GetField("interactObject",BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance)?.GetValue(data); } catch {} }
                    // also peek interactDataList[0].interactObjectTemp for owner check (dump 2146 has interactObjectTemp)
                    object tempOwner=null; string firstStr=null;
                    try {
                        object dl=null; try { dl=Reflect.Get(data,"interactDataList"); } catch { try { dl=data.GetType().GetField("interactDataList",BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance)?.GetValue(data); } catch {} }
                        if (dl!=null) {
                            int dc2=0; try { dc2=Convert.ToInt32(Reflect.Get(dl,"Count")); } catch { try { dc2=(int)dl.GetType().GetProperty("Count").GetValue(dl); } catch {} }
                            if (dc2>0) {
                                var g2=dl.GetType().GetMethod("get_Item") ?? dl.GetType().GetMethod("Get");
                                object id0=null; try { if (g2!=null) id0=g2.Invoke(dl,new object[]{0}); } catch {}
                                if (id0!=null) {
                                    try { firstStr=Reflect.Get(id0,"interactStr") as string; } catch { try { firstStr=id0.GetType().GetField("interactStr")?.GetValue(id0) as string; } catch {} }
                                    try { tempOwner=Reflect.Get(id0,"interactObjectTemp"); } catch { try { tempOwner=id0.GetType().GetField("interactObjectTemp",BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance)?.GetValue(id0); } catch {} }
                                }
                            }
                        }
                    } catch {}
                    bool isOurs=false; string reason="none";
                    try {
                        // 1. direct reference
                        if (!isOurs && io!=null) {
                            try { if (ReferenceEquals(io, (object)t)) { isOurs=true; reason="io==t"; } } catch {}
                            try { if (!isOurs && ReferenceEquals(io, (object)t.gameObject)) { isOurs=true; reason="io==go"; } } catch {}
                        }
                        // 2. GameObject ID match (including Il2Cpp wrapper via reflection)
                        if (!isOurs && io!=null && goId!=0) {
                            try {
                                int ioId=0; try { ioId=(int)io.GetType().GetMethod("GetInstanceID")?.Invoke(io,null); } catch {}
                                if (ioId==goId) { isOurs=true; reason=$"ioId==goId {ioId}"; }
                            } catch {}
                        }
                        // 3. Transform ID match
                        if (!isOurs && tTrans!=null) {
                            try { var ioTrans=GetTransformForIo(io); if (ioTrans!=null && ioTrans.GetInstanceID()==tId) { isOurs=true; reason="transId"; } } catch {}
                        }
                        // 4. position proximity (last resort) - only if interactObject is our type proximity
                        if (!isOurs && io!=null) {
                            try { var ioTrans=GetTransformForIo(io); if (ioTrans!=null && (ioTrans.position - tPos).sqrMagnitude < 0.25f) { isOurs=true; reason="pos0.5"; } } catch {}
                        }
                        // 5. interactObjectTemp owner check (most reliable for Furniture_Commu: InteractData.interactObjectTemp == this)
                        if (!isOurs && tempOwner!=null) {
                            try { if (ReferenceEquals(tempOwner, (object)t)) { isOurs=true; reason="temp==t"; } } catch {}
                            try { if (!isOurs && tempOwner!=null && tempOwner.GetType().GetMethod("GetInstanceID")!=null) { int tid2=(int)tempOwner.GetType().GetMethod("GetInstanceID").Invoke(tempOwner,null); int myId=t.GetInstanceID(); if (tid2==myId) { isOurs=true; reason="tempId"; } } } catch {}
                            try { if (!isOurs && tempOwner is TerrainObject to && to!=null && to.GetInstanceID()==t.GetInstanceID()) { isOurs=true; reason="tempTerrain"; } } catch {}
                        }
                        // 6. string hint: if list has our custom string already, consider ours
                        if (!isOurs && firstStr!=null && firstStr.Contains("传送")) { isOurs=true; reason="strHint"; }
                    } catch {}
                    string ioType = io?.GetType().FullName ?? "null";
                    string tempType = tempOwner?.GetType().FullName ?? "null";
                    int ioInst = 0; try { ioInst = (int)(io?.GetType().GetMethod("GetInstanceID")?.Invoke(io,null) ?? 0); } catch {}
                    Plugin.L.LogInfo($"[TS][Fix] data[{i}] isOurs={isOurs} reason={reason} ioType={ioType} ioId={ioInst} tempType={tempType} firstStr='{firstStr ?? "null"}'");
                    if (!isOurs) continue;
                    object dataList=null; try { dataList=Reflect.Get(data,"interactDataList"); } catch { try { dataList=data.GetType().GetField("interactDataList",BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance)?.GetValue(data); } catch {} }
                    if (dataList==null) { Plugin.L.LogWarning("[TS][Fix] dataList null"); continue; }
                    int dc=0; try { dc=Convert.ToInt32(Reflect.Get(dataList,"Count")); } catch { try { dc=(int)dataList.GetType().GetProperty("Count").GetValue(dataList); } catch {} }
                    var gItem=dataList.GetType().GetMethod("get_Item") ?? dataList.GetType().GetMethod("Get");
                    Plugin.L.LogInfo($"[TS][Fix] found our InteractObjectData dc={dc}");
                    for (int j=0;j<dc;j++) {
                        object id=null; try { if (gItem!=null) id=gItem.Invoke(dataList,new object[]{j}); } catch { continue; }
                        if (id==null) continue;
                        string s=null; try { s=Reflect.Get(id,"interactStr") as string; } catch { try { s=id.GetType().GetField("interactStr")?.GetValue(id) as string; } catch {} }
                        Plugin.L.LogInfo($"[TS][Fix]   InteractData[{j}] str='{s ?? "null"}'");
                    }
                    if (dc>0) {
                        // If already hijacked (first str is our target) just re-hijack delegate to ensure fresh
                        bool needFullRebuild = true;
                        try {
                            if (dc==1) {
                                object id0chk=null; try { if (gItem!=null) id0chk=gItem.Invoke(dataList,new object[]{0}); } catch {}
                                string chk=null; try { chk=Reflect.Get(id0chk,"interactStr") as string; } catch { try { chk=id0chk.GetType().GetField("interactStr")?.GetValue(id0chk) as string; } catch {} }
                                if (chk=="打开传送控制台") needFullRebuild=false;
                            }
                        } catch {}
                        if (needFullRebuild) {
                            // 0.9.52 preserve Q + avoid NRE: hijack existing F entry in-place (keep its transform/range etc.)
                            object targetF = null; int targetIdx = -1;
                            for (int k=0;k<dc;k++) {
                                object cand=null; try { if (gItem!=null) cand=gItem.Invoke(dataList,new object[]{k}); } catch { continue; }
                                if (cand==null) continue;
                                string cs=null; string btn=null; float ht=0f;
                                try { cs=Reflect.Get(cand,"interactStr") as string; } catch { try { cs=cand.GetType().GetField("interactStr", BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance)?.GetValue(cand) as string; } catch {} }
                                try { btn=Reflect.Get(cand,"interactButtonName") as string; } catch { try { btn=cand.GetType().GetField("interactButtonName", BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance)?.GetValue(cand) as string; } catch {} }
                                try { var hv=Reflect.Get(cand,"holdingTime"); if (hv!=null) ht=Convert.ToSingle(hv); } catch { try { var fht=cand.GetType().GetField("holdingTime", BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance); if (fht!=null) ht=Convert.ToSingle(fht.GetValue(cand)); } catch {} }
                                bool isQ = false;
                                if (cs!=null && (cs.Contains("抬起") || cs.Contains("拾取") || cs.Contains("拆除") || cs.Contains("回收"))) isQ=true;
                                if (btn!=null && btn!="Object Interact") isQ=true;
                                if (ht!=0f) isQ=true;
                                if (isQ) continue;
                                targetF=cand; targetIdx=k; break;
                            }
                            if (targetF!=null) {
                                string s0=null; try { s0=Reflect.Get(targetF,"interactStr") as string; } catch { try { s0=targetF.GetType().GetField("interactStr")?.GetValue(targetF) as string; } catch {} }
                                Plugin.L.LogInfo($"[TS][Fix] hijack F idx={targetIdx} dc={dc} orig='{s0 ?? "null"}' -> 打开传送控制台 (Q preserved)");
                                try { Reflect.Set(targetF,"interactStr","打开传送控制台"); } catch { try { targetF.GetType().GetField("interactStr")?.SetValue(targetF,"打开传送控制台"); } catch (Exception ex) { Plugin.L.LogWarning($"[TS][Fix] set interactStr fail {ex.Message.Split('\n')[0]}"); } }
                                try {
                                    if (_interactDelegateType != null) {
                                        var m2 = typeof(TeleportConsoleInteractFix).GetMethod(nameof(OnTeleportConsoleInteract), BindingFlags.Public|BindingFlags.Static);
                                        if (m2 != null) {
                                            var del = Delegate.CreateDelegate(_interactDelegateType, m2);
                                            try { Reflect.Set(targetF, "interactAction", del); } catch { try { targetF.GetType().GetField("interactAction", BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance)?.SetValue(targetF, del); } catch (Exception ex2) { Plugin.L.LogWarning($"[TS][Fix] set delegate fail {ex2.Message.Split('\n')[0]}"); } }
                                            Plugin.L.LogInfo($"[TS][Fix] delegate hijacked F idx={targetIdx} console={t.GetInstanceID()} orig='{s0}' Q preserved");
                                        }
                                    } else Plugin.L.LogWarning("[TS][Fix] _interactDelegateType null cannot hijack");
                                } catch (Exception ex) { Plugin.L.LogWarning($"[TS][Fix] delegate hijack fail {ex.Message.Split('\n')[0]}"); }
                                try { var curTemp=Reflect.Get(targetF,"interactObjectTemp"); if (curTemp==null) Reflect.Set(targetF,"interactObjectTemp", t); } catch {}
                            } else {
                                // no F found (only Q present), create new F via cloning Q's valid fields if possible else Create
                                var nd = CreateInteractData("打开传送控制台", "Object Interact", typeof(TeleportConsoleInteractFix).GetMethod(nameof(OnTeleportConsoleInteract), BindingFlags.Public|BindingFlags.Static), t);
                                if (nd!=null) {
                                    // try to copy valid position/range fields from Q if available
                                    try {
                                        if (dc>0) {
                                            object q=null; try { if (gItem!=null) q=gItem.Invoke(dataList,new object[]{0}); } catch {}
                                            if (q!=null) {
                                                try { var fPos=q.GetType().GetField("interactPosition", BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance); if (fPos!=null) { var v=fPos.GetValue(q); nd.GetType().GetField("interactPosition", BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance)?.SetValue(nd, v); } } catch {}
                                                try { var fRange=q.GetType().GetField("interactRange", BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance); if (fRange!=null) fRange.SetValue(nd, fRange.GetValue(q)); } catch {}
                                            }
                                        }
                                    } catch {}
                                    var add = dataList.GetType().GetMethod("Add");
                                    if (add!=null) { add.Invoke(dataList, new object[]{ nd }); Plugin.L.LogInfo($"[TS][Fix] added new F for console={t.GetInstanceID()} dc was {dc} Q preserved"); } else Plugin.L.LogWarning("[TS][Fix] Add method null, cannot add F");
                                } else Plugin.L.LogWarning("[TS][Fix] Create F failed, Q preserved but F not added");
                            }

                        } else {
                            // just ensure delegate fresh
                            object id0=null; try { if (gItem!=null) id0=gItem.Invoke(dataList,new object[]{0}); } catch {}
                            if (id0!=null) {
                                try {
                                    if (_interactDelegateType != null) {
                                        var m2 = typeof(TeleportConsoleInteractFix).GetMethod(nameof(OnTeleportConsoleInteract), BindingFlags.Public|BindingFlags.Static);
                                        var del = Delegate.CreateDelegate(_interactDelegateType, m2);
                                        try { Reflect.Set(id0, "interactAction", del); } catch { try { id0.GetType().GetField("interactAction")?.SetValue(id0, del); } catch {} }
                                        Plugin.L.LogInfo($"[TS][Fix] re-hijacked delegate for already single console={t.GetInstanceID()}");
                                    }
                                } catch (Exception ex) { Plugin.L.LogWarning($"[TS][Fix] re-hijack fail {ex.Message.Split('\n')[0]}"); }
                            }
                        }
                    }
                    break;
                }
            } catch (Exception ex) { Plugin.L.LogWarning($"[TS][Fix] postfix inner err {ex.Message.Split('\n')[0]}"); }
        } catch (Exception ex) { Plugin.L.LogWarning($"[TS][Fix] CommuEnterPostfix outer err {ex.Message.Split('\n')[0]}"); }
    }

    public static void ClearAllPostfix()
    {
        // P6.8 原生化：不再自动重建，ClearAllInteract 后由玩家重新进入范围时原生重建外部 F
        return;
    }

    /// <summary>0.5s 轮询由 ChargerPadFix.Tick 调用 — P6.8 原生化：已禁用，外部交互由原生实现，外层不再轮询</summary>
    public static void Tick()
    {
        // P6.8: 轮询禁用 — 外部 F 由克隆模板原生 OnPlayerEnterRange 提供，无需每帧重建
        // 保留方法签名以兼容 ChargerPadFix 调用，但直接返回避免刷屏与 GC
        return;
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
            var prop = _interactMgrType.GetProperty("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (prop != null) { var v = prop.GetValue(null); if (v != null) return v; }
            var field = _interactMgrType.GetField("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (field != null) return field.GetValue(null);
            return null;
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

    private static Transform GetTransformForIo(object io)
    {
        try { if (io is Component c) return c.transform; } catch {}
        try { if (io is GameObject go) return go.transform; } catch {}
        try { if (io is Il2CppObjectBase il) { var go2 = il.TryCast<GameObject>(); if (go2 != null) return go2.transform; var comp2 = il.TryCast<Component>(); if (comp2 != null) return comp2.transform; } } catch {}
        try
        {
            var tr = io?.GetType().GetProperty("transform", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(io) as Transform;
            if (tr != null) return tr;
        } catch {}
        try
        {
            var goProp = io?.GetType().GetProperty("gameObject", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(io) as GameObject;
            if (goProp != null) return goProp.transform;
        } catch {}
        // fallback: try Il2CppObjectBase cast via reflection without compile-time dependency
        try {
            var ilType = io?.GetType();
            if (ilType != null) {
                var tryCast = ilType.GetMethod("TryCast", BindingFlags.Public | BindingFlags.Instance);
                // not needed, already handled above
            }
        } catch {}
        return null;
    }

    private static bool HasInteractFor(object listObj, TerrainObject t)
    {
        try
        {
            if (listObj == null || t == null) return false;
            var tTrans = t.transform;
            int tInstId = 0; int goInstId = 0;
            try { tInstId = tTrans != null ? tTrans.GetInstanceID() : 0; } catch {}
            try { goInstId = t.gameObject != null ? t.gameObject.GetInstanceID() : 0; } catch {}
            Vector3 tPos = Vector3.zero;
            try { tPos = tTrans != null ? tTrans.position : t.transform.position; } catch {}
            int count = 0;
            try { count = Convert.ToInt32(Reflect.Get(listObj, "Count")); } catch { try { count = (int)listObj.GetType().GetProperty("Count").GetValue(listObj); } catch { return false; } }
            var getItem = listObj.GetType().GetMethod("get_Item") ?? listObj.GetType().GetMethod("Get");
            for (int i = 0; i < count; i++)
            {
                object data = null;
                try { if (getItem != null) data = getItem.Invoke(listObj, new object[] { i }); } catch { continue; }
                if (data == null) continue;
                object io = null;
                try { io = Reflect.Get(data, "interactObject"); } catch { try { io = data.GetType().GetField("interactObject", BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance)?.GetValue(data); } catch {} }
                if (io == null) continue;
                // 1. GameObject InstanceID
                try { if (io is GameObject go && goInstId != 0 && go.GetInstanceID() == goInstId) return true; } catch {}
                // 2. Direct reference (fallback)
                try { if (ReferenceEquals(io, (object)t)) return true; } catch {}
                try { if (ReferenceEquals(io, (object)t.gameObject)) return true; } catch {}
                // 3. Transform InstanceID (IL2CPP wrapper-safe)
                try
                {
                    var ioTrans = GetTransformForIo(io);
                    if (ioTrans != null && tTrans != null)
                    {
                        if (ioTrans.GetInstanceID() == tInstId) return true;
                        // 4. Position proximity as last resort (0.1m)
                        if ((ioTrans.position - tPos).sqrMagnitude < 0.01f) return true;
                    }
                } catch {}
            }
        } catch {}
        return false;
    }

    private static Transform GetPlayerTransform()
    {
        try
        {
            EnsureTypeCache();
            var pcType = _playerCtrlType ?? _humanCtrlType ?? AccessTools.TypeByName("PlayerController") ?? AccessTools.TypeByName("HumanCharacterController");
            if (pcType != null)
            {
                var inst = AccessTools.Property(pcType, "instance")?.GetValue(null) ?? AccessTools.Field(pcType, "instance")?.GetValue(null);
                if (inst == null) try { inst = pcType.GetProperty("instance", BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static)?.GetValue(null); } catch {}
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
            var nd1 = CreateInteractData("重命名传送站", "Object Interact", typeof(TeleportConsoleInteractFix).GetMethod(nameof(OnRename), BindingFlags.Public|BindingFlags.Static), t);
            var nd2 = CreateInteractData("选择传送目的地", "Object Interact", typeof(TeleportConsoleInteractFix).GetMethod(nameof(OnSelectList), BindingFlags.Public|BindingFlags.Static), t);
            var nd3 = CreateInteractData("退出", "Object Interact", typeof(TeleportConsoleInteractFix).GetMethod(nameof(OnExit), BindingFlags.Public|BindingFlags.Static), t);
            if (nd1 == null || nd2 == null || nd3 == null) { Plugin.L.LogWarning($"[TS][Fix] CreateInteractData 失败 nd1={nd1!=null} nd2={nd2!=null} nd3={nd3!=null}"); return false; }

            // 遍历 listObj 寻找匹配 t.gameObject
            object targetData = null;
            int count = 0;
            try { count = Convert.ToInt32(Reflect.Get(listObj, "Count")); } catch { try { count = (int)listObj.GetType().GetProperty("Count").GetValue(listObj); } catch { Plugin.L.LogWarning("[TS][Fix] list Count 获取失败"); } }
            Plugin.L.LogInfo($"[TS][Fix] 遍历 interactObjectDataList count={count} 寻找 console={t.GetInstanceID()}");
            var getItem = listObj.GetType().GetMethod("get_Item") ?? listObj.GetType().GetMethod("Get");
            var tTrans2 = t.transform;
            int tInstId2 = 0; int goInstId2 = 0;
            Vector3 tPos2 = Vector3.zero;
            try { tInstId2 = tTrans2 != null ? tTrans2.GetInstanceID() : 0; } catch {}
            try { goInstId2 = t.gameObject != null ? t.gameObject.GetInstanceID() : 0; } catch {}
            try { tPos2 = tTrans2 != null ? tTrans2.position : Vector3.zero; } catch {}
            for (int i = 0; i < count; i++)
            {
                object data = null;
                try { if (getItem != null) data = getItem.Invoke(listObj, new object[] { i }); else data = Reflect.Get(listObj, i.ToString()); } catch (Exception exi) { Plugin.L.LogWarning($"[TS][Fix] getItem[{i}] 异常 {exi.Message.Split('\n')[0]}"); continue; }
                if (data == null) continue;
                object io = null;
                try { io = Reflect.Get(data, "interactObject"); } catch { try { io = data.GetType().GetField("interactObject", BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance)?.GetValue(data); } catch {} }
                if (io == null) continue;
                bool match = false;
                try { if (io is GameObject go && goInstId2 != 0 && go.GetInstanceID() == goInstId2) match = true; } catch {}
                try { if (ReferenceEquals(io, (object)t)) match = true; } catch {}
                if (!match)
                {
                    try
                    {
                        var ioTrans = GetTransformForIo(io);
                        if (ioTrans != null && tTrans2 != null)
                        {
                            if (ioTrans.GetInstanceID() == tInstId2) match = true;
                            else if ((ioTrans.position - tPos2).sqrMagnitude < 0.01f) match = true;
                        }
                    } catch {}
                }
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
            var nd1 = CreateInteractData("重命名传送站", "Object Interact", typeof(TeleportConsoleInteractFix).GetMethod(nameof(OnRename), BindingFlags.Public|BindingFlags.Static), t);
            var nd2 = CreateInteractData("选择传送目的地", "Object Interact", typeof(TeleportConsoleInteractFix).GetMethod(nameof(OnSelectList), BindingFlags.Public|BindingFlags.Static), t);
            var nd3 = CreateInteractData("退出", "Object Interact", typeof(TeleportConsoleInteractFix).GetMethod(nameof(OnExit), BindingFlags.Public|BindingFlags.Static), t);
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
            _mAddEnter.Invoke(t, new object[] { "重命名传送站", del1, "Object Interact" });
            _mAddEnter.Invoke(t, new object[] { "选择传送目的地", del2, "Object Interact" });
            _mAddEnter.Invoke(t, new object[] { "退出", del3, "Object Interact" });
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
            try { EnsureTypeCache(); var uiType = _interactUIType ?? AccessTools.TypeByName("InteractUI"); var inst = AccessTools.Property(uiType, "instance")?.GetValue(null); var m = uiType?.GetMethod("ClosePanel"); m?.Invoke(inst, null); } catch {}
            try { EnsureTypeCache(); var t = _interactUITerrainType ?? AccessTools.TypeByName("InteractUI_TerrainObject"); var inst2 = AccessTools.Property(t, "instance")?.GetValue(null); var m2 = t?.GetMethod("ClosePanel"); m2?.Invoke(inst2, null); } catch {}
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
            try { EnsureTypeCache(); var uiType = _interactUIType ?? AccessTools.TypeByName("InteractUI"); var inst = AccessTools.Property(uiType, "instance")?.GetValue(null); var m = uiType?.GetMethod("ClosePanel"); m?.Invoke(inst, null); } catch {}
            var c = _currentConsole;
            if (c == null) { Plugin.L.LogWarning("[TS][Fix] OnSelectList 无 console"); return; }
            TeleportConsoleUI.EnsureExists().ShowForConsole(c);
            Plugin.L.LogInfo($"[TS][Fix] OnSelectList console={c.GetInstanceID()} -> 打开站列表");
        } catch (Exception e) { Plugin.L.LogWarning($"[TS][Fix] OnSelectList 异常: {e.Message.Split('\n')[0]}"); }
    }

    public static void OnExit(object obj)
    {
        try { EnsureTypeCache(); var uiType = _interactUIType ?? AccessTools.TypeByName("InteractUI"); var inst = AccessTools.Property(uiType, "instance")?.GetValue(null); var m = uiType?.GetMethod("ClosePanel"); m?.Invoke(inst, null); } catch {}
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
