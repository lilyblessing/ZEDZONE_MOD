using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
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
/// A方案（F注册层分流，v0.9.56）已回退：CommuEnterPrefix 内吞原生（return false）+ TryRegisterConsoleSelf 自建登记，
/// 自建委托 TryCreateDelegate R1-R6 全灭恒 null → list 真空（count=0）→ 外部 F/Q 全灭（v0.9.56 回归）。
/// v0.9.57 放行方案：CommuEnterPrefix 对 900101 也不再 return false（只打日志 + 标记 _currentConsole，返回 true 放行原生注册，保住 F+Q 原生条目）；
/// postfix 沿用 v0.9.55 做法只改 F 条目 interactStr（Q 条目 isQ 透传保留不动）；F 分派走 <>c 拦截，
/// <>c 入口改用回调 obj 链判 900101（InteractObjectData.interactObject@0x10 / InteractData.interactObjectTemp@0x30 → attr.id），是才 return false 弹自建菜单；
/// TryRegisterConsoleSelf 整段休眠（R-all-fail），不再向 list 写 null 委托。
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
            // P6.12: 委托目标兜底 — 直接 patch 闭包方法 <>c.<OnPlayerEnterRange>b__0_0，绕过 Delegate.CreateDelegate 的 IL2CPP 类型校验
            // dump.cs:78378-78394 实证：<>c 系 TerrainObject_Furniture_Commu 的 private sealed 嵌套类（TypeDefIndex 1824），
            //   方法 internal void <OnPlayerEnterRange>b__0_0(object obj)，RVA 0x9A49A0 / VA 0x1809A49A0。
            // 穷举解析（命中即停）+ 单条 trace 诊断：成功打 Info（含命中路），全失败才打一条 Warn（含逐路结果）。
            try
            {
                var trace = new System.Text.StringBuilder();
                Type closure = null; string hitRoute = null; MethodInfo bMethod = null;
                // L1: 直访嵌套（Public|NonPublic 双旗；v0.9.55 仅传 NonPublic，若代理层嵌套可见性被改写则此处 null）
                try
                {
                    closure = typeof(TerrainObject_Furniture_Commu).GetNestedType("<>c", BindingFlags.Public | BindingFlags.NonPublic);
                    trace.Append($"L1(GetNestedType Public|NonPublic)={(closure != null ? closure.FullName : "null")};");
                    if (closure != null) hitRoute = "L1";
                } catch (Exception ex) { trace.Append($"L1-ex:{ex.Message.Split('\n')[0]};"); }
                // L2: 枚举全部嵌套（定位：嵌套是否存在、实际叫什么名；Il2CppInterop 改名/剥离在此现形）
                if (closure == null) try
                {
                    var nested = typeof(TerrainObject_Furniture_Commu).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic);
                    var names = new List<string>();
                    foreach (var n in nested) { names.Add(n.Name); if (n.Name == "<>c" || n.Name.Contains(">c")) { closure = n; hitRoute = "L2"; break; } }
                    trace.Append($"L2(enumNested count={nested.Length} names=[{string.Join(",", names)}]);");
                } catch (Exception ex) { trace.Append($"L2-ex:{ex.Message.Split('\n')[0]};"); }
                // L3: Harmony Inner（名精确匹配 "<>c"）
                if (closure == null) try
                {
                    closure = AccessTools.Inner(typeof(TerrainObject_Furniture_Commu), "<>c");
                    trace.Append($"L3(Inner)={(closure != null ? closure.FullName : "null")};");
                    if (closure != null) hitRoute = "L3";
                } catch (Exception ex) { trace.Append($"L3-ex:{ex.Message.Split('\n')[0]};"); }
                // L4: 同程序集全扫描（不经嵌套名：FullName 含 Furniture_Commu 且以 +<>c / .<>c 结尾；覆盖代理层改名或顶层化）
                if (closure == null) try
                {
                    var asm = typeof(TerrainObject_Furniture_Commu).Assembly;
                    Type[] types = null;
                    try { types = asm.GetTypes(); } catch (System.Reflection.ReflectionTypeLoadException ex) { types = ex.Types; }
                    int candCount = 0; var candNames = new List<string>();
                    if (types != null) foreach (var tp in types)
                    {
                        if (tp == null || tp.FullName == null) continue;
                        if (tp.FullName.Contains("Furniture_Commu"))
                        {
                            candCount++;
                            if (candNames.Count < 12) candNames.Add(tp.FullName);
                            if (tp.Name == "<>c" || tp.FullName.EndsWith("+<>c") || tp.FullName.EndsWith(".<>c")) { closure = tp; hitRoute = "L4"; break; }
                        }
                    }
                    trace.Append($"L4(asmScan asm={asm.GetName().Name} commuCandidates={candCount} [{string.Join(",", candNames)}]){(closure != null ? " HIT" : "")};");
                } catch (Exception ex) { trace.Append($"L4-ex:{ex.Message.Split('\n')[0]};"); }
                // L5: AccessTools.TypeByName 变体（+ 分隔与 . 分隔）
                if (closure == null) try
                {
                    closure = AccessTools.TypeByName("TerrainObject_Furniture_Commu+<>c") ?? AccessTools.TypeByName("TerrainObject_Furniture_Commu.<>c");
                    trace.Append($"L5(TypeByName)={(closure != null ? closure.FullName : "null")};");
                    if (closure != null) hitRoute = "L5";
                } catch (Exception ex) { trace.Append($"L5-ex:{ex.Message.Split('\n')[0]};"); }
                // L6: SafeTypeByName 变体（+ 分隔与 . 分隔，全 AppDomain 扫描）
                if (closure == null) try
                {
                    closure = SafeTypeByName("TerrainObject_Furniture_Commu+<>c") ?? SafeTypeByName("TerrainObject_Furniture_Commu.<>c");
                    trace.Append($"L6(SafeTypeByName)={(closure != null ? closure.FullName : "null")};");
                    if (closure != null) hitRoute = "L6";
                } catch (Exception ex) { trace.Append($"L6-ex:{ex.Message.Split('\n')[0]};"); }
                // 方法反查（不经类型名精确语义）：M1 精确名 → M2 按“含 OnPlayerEnterRange 且含 b__0_0”枚举
                // 注：RVA/VA→MethodInfo 无托管 API（Il2CppInterop 不暴露 RVA 查表），M2 方法名枚举即“从方法反查”的等价实现。
                if (closure != null)
                {
                    try { bMethod = AccessTools.Method(closure, "<OnPlayerEnterRange>b__0_0"); trace.Append($"M1(exact)={(bMethod != null ? "HIT" : "null")};"); } catch (Exception ex) { trace.Append($"M1-ex:{ex.Message.Split('\n')[0]};"); }
                    if (bMethod == null) try
                    {
                        var mNames = new List<string>();
                        foreach (var m in closure.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                        {
                            if (mNames.Count < 16) mNames.Add(m.Name);
                            if (m.Name.Contains("OnPlayerEnterRange") && m.Name.Contains("b__0_0")) { bMethod = m; break; }
                        }
                        trace.Append($"M2(enumMethods [{string.Join(",", mNames)}]){(bMethod != null ? " HIT" : "")};");
                    } catch (Exception ex) { trace.Append($"M2-ex:{ex.Message.Split('\n')[0]};"); }
                }
                if (closure != null && bMethod != null)
                {
                    var pre = new HarmonyMethod(typeof(TeleportConsoleInteractFix).GetMethod(nameof(CommuDelegatePrefix), BindingFlags.NonPublic | BindingFlags.Static));
                    h.Patch(bMethod, prefix: pre);
                    Plugin.L.LogInfo($"[TS][Fix] 已挂钩 <>c.<OnPlayerEnterRange>b__0_0 prefix route={hitRoute} type={closure.FullName} trace=[{trace}]");
                }
                else Plugin.L.LogWarning($"[TS][Fix] <>c 劫持未挂钩 closure={(closure != null ? closure.FullName : "null")} bMethod={(bMethod != null ? bMethod.Name : "null")} trace=[{trace}]");
            } catch (Exception e) { Plugin.L.LogWarning($"[TS][Fix] <>c 挂钩异常: {e.Message.Split('\n')[0]}"); }
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
                            // 0.9.53 fieldType delegate fix + preserve Q: hijack F in-place via fieldType
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
                                    var m2 = typeof(TeleportConsoleInteractFix).GetMethod(nameof(OnTeleportConsoleInteract), BindingFlags.Public|BindingFlags.Static);
                                    if (m2 != null) {
                                        Type delType = null;
                                        try { var f = targetF.GetType().GetField("interactAction", BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance); if (f!=null) delType = f.FieldType; } catch {}
                                        if (delType == null) delType = _interactDelegateType;
                                        if (delType == null) try { delType = typeof(InteractManager.InteractDelegate); } catch {}
                                        if (delType != null) {
                                            object del = null;
                                            try { del = TryCreateDelegate(delType, m2); } catch (Exception ex0) { Plugin.L.LogWarning($"[TS][Fix] TryCreateDelegate fail delType={delType.FullName} err={ex0.Message.Split('\n')[0]}"); }
                                            if (del == null) try { del = TryCreateDelegate(typeof(InteractManager.InteractDelegate), m2); } catch (Exception ex1) { Plugin.L.LogWarning($"[TS][Fix] fallback TryCreateDelegate fail {ex1.Message.Split('\n')[0]}"); }
                                            if (del != null) {
                                                try { Reflect.Set(targetF, "interactAction", del); } catch { try { targetF.GetType().GetField("interactAction", BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance)?.SetValue(targetF, del); } catch (Exception ex2) { Plugin.L.LogWarning($"[TS][Fix] set delegate fail {ex2.Message.Split('\n')[0]}"); } }
                                                Plugin.L.LogInfo($"[TS][Fix] delegate hijacked F idx={targetIdx} console={t.GetInstanceID()} orig='{s0}' Q preserved delType={delType.FullName} via TryCreateDelegate");
                                            } else Plugin.L.LogWarning("[TS][Fix] del null after TryCreateDelegate");
                                        } else Plugin.L.LogWarning("[TS][Fix] delType null cannot hijack");
                                    }
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
                                    var m2 = typeof(TeleportConsoleInteractFix).GetMethod(nameof(OnTeleportConsoleInteract), BindingFlags.Public|BindingFlags.Static);
                                    if (m2 != null) {
                                        Type delType = null;
                                        try { var f = id0.GetType().GetField("interactAction", BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance); if (f!=null) delType = f.FieldType; } catch {}
                                        if (delType == null) delType = _interactDelegateType;
                                        if (delType == null) try { delType = typeof(InteractManager.InteractDelegate); } catch {}
                                        if (delType != null) {
                                            object del = null;
                                            try { del = TryCreateDelegate(delType, m2); } catch (Exception ex0) { Plugin.L.LogWarning($"[TS][Fix] re-hijack TryCreateDelegate fail delType={delType.FullName} err={ex0.Message.Split('\n')[0]}"); try { del = TryCreateDelegate(typeof(InteractManager.InteractDelegate), m2); } catch {} }
                                            if (del != null) {
                                                try { Reflect.Set(id0, "interactAction", del); } catch { try { id0.GetType().GetField("interactAction")?.SetValue(id0, del); } catch {} }
                                                Plugin.L.LogInfo($"[TS][Fix] re-hijacked delegate for already single console={t.GetInstanceID()} delType={delType.FullName} via TryCreateDelegate");
                                            } else Plugin.L.LogWarning("[TS][Fix] re-hijack del null after TryCreateDelegate");
                                        }
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
            // delegate via TryCreateDelegate (IL2CPP-compatible)
            object del = null;
            try
            {
                if (_interactDelegateType != null && handler != null)
                    del = TryCreateDelegate(_interactDelegateType, handler);
                if (del == null && handler != null)
                    del = TryCreateDelegate(typeof(InteractManager.InteractDelegate), handler);
                if (del == null && _interactDelegateType != null && handler != null)
                    del = TryCreateDelegate(_interactDelegateType, handler);
            } catch {}
            if (del != null) try { Reflect.Set(nd, "interactAction", del); } catch { try { nd.GetType().GetField("interactAction").SetValue(nd, del); } catch {} }
            else Plugin.L.LogWarning($"[TS][Fix] CreateInteractData delegate null str={str}");
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
            var d = _interactDelegateType != null ? TryCreateDelegate(_interactDelegateType, mi) : null;
            if (d != null) return d;
            return TryCreateDelegate(typeof(InteractManager.InteractDelegate), mi);
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

    private static object TryCreateDelegate(Type delType, MethodInfo mi)
    {
        if (delType == null || mi == null) return null;
        // R1 CreateDelegate 开放静态绑定
        try { var d0 = Delegate.CreateDelegate(delType, mi); if (d0 != null) { Plugin.L.LogInfo($"[TS][Fix] TryCreateDelegate R1 System success delType={delType.FullName}"); return d0; } } catch (Exception ex) { Plugin.L.LogWarning($"[TS][Fix] TryCreateDelegate R1 fail delType={delType.FullName} err={ex.Message.Split('\n')[0]}"); }
        // R2 CreateDelegate 显式target绑定（静态mi + null target）
        try { var d1 = Delegate.CreateDelegate(delType, null, mi); if (d1 != null) { Plugin.L.LogInfo($"[TS][Fix] TryCreateDelegate R2 explicit-target success delType={delType.FullName}"); return d1; } } catch (Exception ex) { Plugin.L.LogWarning($"[TS][Fix] TryCreateDelegate R2 fail delType={delType.FullName} err={ex.Message.Split('\n')[0]}"); }
        // R3 MethodInfo.CreateDelegate绑定路径
        try { var d2 = mi.CreateDelegate(delType); if (d2 != null) { Plugin.L.LogInfo($"[TS][Fix] TryCreateDelegate R3 MethodInfo success delType={delType.FullName}"); return d2; } } catch (Exception ex) { Plugin.L.LogWarning($"[TS][Fix] TryCreateDelegate R3 fail delType={delType.FullName} err={ex.Message.Split('\n')[0]}"); }
        // R4 newobj+绑定：DelegateSupport.ConvertDelegate（互操作元数据实测存在：Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<T>(Delegate)，
        //   public static generic，ret=!!0 params=[Delegate]；managed static方法先绑成 Action<object> 再转 Il2Cpp 原生 trampoline，不走 IntPtr 裸指针）
        try
        {
            if (mi.IsStatic)
            {
                var managed = Delegate.CreateDelegate(typeof(Action<object>), mi);
                if (managed != null)
                {
                    var conv = typeof(Il2CppInterop.Runtime.DelegateSupport).GetMethod("ConvertDelegate", BindingFlags.Public | BindingFlags.Static);
                    var generic = conv != null ? conv.MakeGenericMethod(delType) : null;
                    var d = generic != null ? generic.Invoke(null, new object[] { managed }) : null;
                    if (d != null) { Plugin.L.LogInfo($"[TS][Fix] TryCreateDelegate R4 ConvertDelegate success delType={delType.FullName} mi={mi.Name}"); return d; }
                }
            }
        } catch (Exception ex) { Plugin.L.LogWarning($"[TS][Fix] TryCreateDelegate R4 fail {ex.Message.Split('\n')[0]}"); }
        // R5 Il2CppSystem.Delegate 绑定路径
        try
        {
            var ilType = SafeTypeByName("Il2CppSystem.Delegate");
            if (ilType != null)
            {
                var m = ilType.GetMethod("CreateDelegate", BindingFlags.Public | BindingFlags.Static, null, new Type[]{ typeof(Type), typeof(MethodInfo) }, null);
                if (m != null)
                {
                    var d = m.Invoke(null, new object[]{ delType, mi });
                    if (d != null) { Plugin.L.LogInfo($"[TS][Fix] TryCreateDelegate R5 via Il2CppSystem.Delegate success delType={delType.FullName}"); return d; }
                }
            }
        } catch (Exception ex2) { Plugin.L.LogWarning($"[TS][Fix] TryCreateDelegate R5 fail {ex2.Message.Split('\n')[0]}"); }
        // R6 null：P6.11 hotfix 0.9.55 纪律保留 — Activator IntPtr 路径禁用（Invoke时崩溃 0x7FFBEE...），主路径不再依赖，靠 CommuDelegatePrefix(<>c)兜底
        Plugin.L.LogWarning($"[TS][Fix] TryCreateDelegate all routes fail delType={delType.FullName} -> null (use <>c prefix)");
        return null;
    }

    /// <summary>
    /// <>c 分派：F 按下时 b__0_0 回调入口。判别一律先走回调 obj 链（编译期直访，禁反射读游戏字段）：
    /// 主路 obj as InteractObjectData → interactObject@0x10 → TerrainObject.attr.id；
    /// 次路 obj as InteractData → interactObjectTemp@0x30（Furniture_Commu 注册时 == this）→ attr.id。
    /// 命中 900101 才 return false 弹自建菜单；obj 链可解析但非 900101 → return true 放原生 DOS；
    /// obj 不可解析（null/未知类型）→ 沿用 v0.9.55 近距门控兜底（_currentConsole + 6m）并打 fallback 日志。
    /// 旧问题：纯距离门控下，传送台旁的原生终端按 F 也会被吞；现仅未知 payload 才走此兜底。
    /// </summary>
    private static bool CommuDelegatePrefix(object __instance, object __0)
    {
        try
        {
            object obj = __0;
            // 主判别：回调 obj 链（编译期直访，禁反射读游戏字段）
            TerrainObject hit = ResolveConsoleFromCallback(obj);
            if (hit != null)
            {
                _currentConsole = hit;
                try { TeleportConsoleMenuUI.EnsureExists().ShowForConsole(hit); } catch (Exception e) { Plugin.L.LogWarning($"[TS][Fix] delegate prefix menu fail {e.Message.Split('\n')[0]}"); }
                Plugin.L.LogInfo($"[TS][Fix] <>c delegate intercepted (obj链) console={hit.GetInstanceID()} objType={obj?.GetType().FullName ?? "null"}");
                return false; // 900101：吞原生 DOS，弹自建菜单
            }
            if (obj != null && IsKnownInteractPayload(obj)) return true; // 链可解析但非传送台：原生终端，放原生 DOS
            try { Plugin.L.LogInfo($"[TS][Fix] <>c obj不可解析走近距兜底 objType={obj?.GetType().FullName ?? "null"}"); } catch {}
            var c = _currentConsole;
            if (c == null || c.attr == null || c.attr.id != 900101)
            {
                try
                {
                    var list = TeleportObjectCache.FindAllById(900101);
                    var player = GetPlayerTransform();
                    if (player != null && list != null && list.Count > 0)
                    {
                        TerrainObject best = null; float bestD2 = 36f;
                        foreach (var t in list)
                        {
                            if (t == null || t.transform == null) continue;
                            float d2 = (t.transform.position - player.position).sqrMagnitude;
                            if (d2 < bestD2) { bestD2 = d2; best = t; }
                        }
                        if (best != null) c = best;
                    }
                } catch {}
            }
            if (c == null || c.attr == null || c.attr.id != 900101) return true;
            try
            {
                var player = GetPlayerTransform();
                if (player != null)
                {
                    float d2 = (c.transform.position - player.position).sqrMagnitude;
                    if (d2 > 36f) return true;
                }
            } catch {}
            try { TeleportConsoleMenuUI.EnsureExists().ShowForConsole(c); } catch (Exception e) { Plugin.L.LogWarning($"[TS][Fix] delegate prefix menu fail {e.Message.Split('\n')[0]}"); }
            Plugin.L.LogInfo($"[TS][Fix] <>c delegate intercepted (近距兜底) console={c.GetInstanceID()} pos={c.transform.position} objParam={obj?.GetType().FullName ?? "null"}");
            return false;
        } catch (Exception e) { Plugin.L.LogWarning($"[TS][Fix] delegate prefix err {e.Message.Split('\n')[0]}"); return true; }
    }

    /// <summary>obj 链主判别：obj → InteractObjectData.interactObject@0x10 / InteractData.interactObjectTemp@0x30 → TerrainObject.attr.id==900101。编译期直访，禁反射读游戏字段；命中返回拥有者，否则 null。</summary>
    private static TerrainObject ResolveConsoleFromCallback(object obj)
    {
        if (obj == null) return null;
        try
        {
            var iod = obj as InteractObjectData;
            if (iod != null)
            {
                TerrainObject to = iod.interactObject as TerrainObject;
                if (to == null)
                {
                    try { var go = iod.interactObject as GameObject; if (go != null) to = go.GetComponent<TerrainObject>(); } catch {}
                }
                if (to != null && to.attr != null && to.attr.id == 900101) return to;
            }
        } catch {}
        try
        {
            var id = obj as InteractData;
            if (id != null)
            {
                TerrainObject to = id.interactObjectTemp as TerrainObject;
                if (to == null)
                {
                    try { var go = id.interactObjectTemp as GameObject; if (go != null) to = go.GetComponent<TerrainObject>(); } catch {}
                }
                if (to != null && to.attr != null && to.attr.id == 900101) return to;
            }
        } catch {}
        return null;
    }

    /// <summary>obj 是否为可解析的交互 payload（任一链可走通类型判断）。用于区分“明确非传送台”（放原生 DOS）与“不可解析”（走近距兜底）。只读自家类型判断，零反射读游戏字段。</summary>
    private static bool IsKnownInteractPayload(object obj)
    {
        if (obj == null) return false;
        try { if (obj is InteractData) return true; } catch {}
        try { if (obj is InteractObjectData) return true; } catch {}
        return false;
    }

    public static bool CommuExitPrefix(object __instance)
    {
        return true;
    }
    // 兼容旧 prefix 签名占位（已由A分流实现替代，保留方法族其余项不动）
    public static bool CommuLeavePrefix(object __instance, object __0) { return true; }
    public static bool ComputerEnterPrefix(object __instance, object __0) { return true; }
    public static bool ComputerOpenPrefix(object __instance, object m_computerData, object m_computer) { return true; }
    public static bool InteractOpenPrefix(object __instance, GameObject go, string str, object del) { return true; }
    /// <summary>
    /// v0.9.57 放行方案（A 方案吞原生已回退）。
    /// v0.9.56 回归根因：传送台 return false 吞原生注册（含 Q 条目），而 TryRegisterConsoleSelf 自建委托 R1-R6 全灭恒 null → 拒绝注册 → list 真空（postfix 实测 count=0）→ 外部 F/Q 全灭。
    /// 现 900101 也不再 return false：只打日志 + 标记 _currentConsole（postfix 改字 / <>c 分派用），返回 true 放行原生注册，保住 F+Q 原生条目。
    /// 判定=编译期直访 attr@0xB8（attr.id==900101），禁反射读游戏字段；原生零触碰。
    /// 反编译结论直接用：F注册=TerrainObject_Furniture_Commu.OnPlayerEnterRange Slot19 VA 0x180997AD0（virtual非final，互操作元数据已验），
    /// 只登记InteractData（+0x28缓存委托<>9__0_0，+0x18按键名，+0x10交互文本）；F按下=b__0_0 VA 0x1809A49A0仅调DOS OpenDOSPanel，
    /// __this共享单例实例已丢，实例仅注册瞬间存在（__this/interactObject@0x10）；判定=attr@0xB8，传送台attr.id==900101。
    /// Q说明：isQ透传跳过逻辑在CommuEnterPostfix内原样保留未动；F分派走CommuDelegatePrefix(<>c)obj链判别。
    /// </summary>
    public static bool CommuEnterPrefix(object __instance, object __0)
    {
        try
        {
            // 编译期直访代理类型public成员，禁反射读游戏字段；实例仅注册瞬间有效
            var t = __instance as TerrainObject;
            if (t == null) return true;
            var attr = t.attr;     // @0xB8
            if (attr == null || attr.id != 900101) return true; // 原生：什么都不做，直接放行
            _currentConsole = t;
            try { Plugin.L.LogInfo($"[TS][Fix] 900101 放行原生 console={t.GetInstanceID()} (F+Q保留→postfix改字+<>c分派)"); } catch {}
            return true; // 放行原生：雇佣/上传/退出（+Q抬走）原样登记，F/Q 不再全灭
        }
        catch (Exception e) { try { Plugin.L.LogWarning($"[TS][Fix] CommuEnterPrefix err {e.Message.Split('\n')[0]}"); } catch {} return true; }
    }

    /// <summary>
    /// A 方案自建登记：已休眠（DORMANT v0.9.57，不再调用）。
    /// 休眠原因 R-all-fail：TryCreateDelegate R1-R3 System.Delegate 均报 Type must derive from Delegate（InteractDelegate 系 Il2CppSystem.MulticastDelegate 派生，非 System 派系），
    /// R4 ConvertDelegate 实机抛 target invocation，R5/R6 亦无可用 → 委托恒 null；继续登记会写坏项，而 prefix 若吞原生则 list 真空（count=0）→ F/Q 全灭（v0.9.56 回归）。
    /// 现改走原生放行 + postfix 改字 + &lt;&gt;c 分派，本方法不再调用；旧实现整段保留在 #if false 内备查，不再向 list 写 null 委托。
    /// </summary>
    private static bool TryRegisterConsoleSelf(TerrainObject t)
    {
        try { Plugin.L.LogWarning("[TS][Fix] TryRegisterConsoleSelf dormant (R-all-fail)，拒绝自建登记"); } catch {}
        return false;
#if false
        // ===== v0.9.56 A 方案自建实现（休眠保留，不编译）=====
        try
        {
            EnsureTypeCache();
            if (_interactMgrType == null || _interactObjDataType == null || _mAddData == null) { Plugin.L.LogWarning("[TS][Fix] 自建 类型缺失"); return false; }
            var im = GetInteractManagerInstance();
            if (im == null) { Plugin.L.LogWarning("[TS][Fix] 自建 instance null"); return false; }
            object listObj = GetInteractList(im);
            if (listObj != null && HasInteractFor(listObj, t)) return true; // 已有：postfix负责刷新委托
            var m = typeof(TeleportConsoleInteractFix).GetMethod(nameof(OnTeleportConsoleInteract), BindingFlags.Public | BindingFlags.Static);
            // 先验委托可用（只读自家返回值，零反射读游戏字段）：null则拒绝注册，不登记坏项，主路径不再依赖<>c兜底
            object delCheck = null;
            try { delCheck = TryCreateDelegate(typeof(InteractManager.InteractDelegate), m); } catch {}
            if (delCheck == null) { Plugin.L.LogWarning("[TS][Fix] 自建 delegate null，拒绝注册（等<>c兜底）"); return false; }
            if (_mRemove != null) { try { _mRemove.Invoke(im, new object[] { t.gameObject }); } catch {} }
            var nd = CreateInteractData("打开传送控制台", "Object Interact", m, t);
            if (nd == null) { Plugin.L.LogWarning("[TS][Fix] 自建 CreateInteractData null"); return false; }
            object iod = null;
            try { iod = Activator.CreateInstance(_interactObjDataType); } catch (Exception e) { Plugin.L.LogWarning($"[TS][Fix] 自建 iod创建失败 {e.Message.Split('\n')[0]}"); return false; }
            try { Reflect.Set(iod, "interactObject", t.gameObject); } catch { try { _interactObjDataType.GetField("interactObject", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(iod, t.gameObject); } catch {} }
            object dataList = null;
            try { dataList = Reflect.Get(iod, "interactDataList"); } catch {}
            if (dataList == null)
            {
                try { var f = _interactObjDataType.GetField("interactDataList", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance); var listType = f.FieldType; dataList = Activator.CreateInstance(listType); Reflect.Set(iod, "interactDataList", dataList); } catch {}
            }
            if (dataList == null) { Plugin.L.LogWarning("[TS][Fix] 自建 dataList null"); return false; }
            var add = dataList.GetType().GetMethod("Add");
            if (add == null) { Plugin.L.LogWarning("[TS][Fix] 自建 Add未找到"); return false; }
            add.Invoke(dataList, new object[] { nd });
            try { var fR = _interactObjDataType.GetField("interactRange", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance); if (fR != null) fR.SetValue(iod, 3f); } catch {}
            try { var fM = _interactObjDataType.GetField("maxPlayerInteractRange", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance); if (fM != null) fM.SetValue(iod, 5f); } catch {}
            try { _mAddData.Invoke(im, new object[] { iod }); } catch (Exception e) { Plugin.L.LogWarning($"[TS][Fix] 自建 Add异常 {e.Message.Split('\n')[0]}"); return false; }
            Plugin.L.LogInfo($"[TS][Fix] 自建 AddInteractObjectData成功 console={t.GetInstanceID()} 单F=打开传送控制台");
            return true;
        }
        catch (Exception e) { Plugin.L.LogWarning($"[TS][Fix] 自建异常 {e.Message.Split('\n')[0]}"); return false; }
#endif
    }
}
