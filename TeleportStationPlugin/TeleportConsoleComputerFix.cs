using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using ZedZoneShared;

namespace TeleportStationPlugin;

/// <summary>
/// P6.2 控制台电脑菜单劫持：将 900101 控制台的 ComputerPanel 原版菜单替换为三项（命名/选点/退出）并分派执行。
/// 依赖：dump.cs ComputerPanel OpenPanel VA0x180588520 / ExecuteResult VA0x180587E50 / ComputerNodeData 结构。
/// </summary>
public static class TeleportConsoleComputerFix
{
    private static bool _patched = false;

    /// <summary>选中第二项后记录，供 MapManager/选点面板使用（当前待操作控制台）</summary>
    public static TerrainObject PendingConsoleForMap;

    /// <summary>便于外部读取当前控制台（与 PendingConsoleForMap 同步）</summary>
    public static TerrainObject CurrentConsole;

    public const string RenameResult = "TS_Rename";
    public const string SelectResult = "TS_SelectMap";
    public const string ExitResult = "TS_Exit";

    public static void EnsurePatch(Harmony h)
    {
        if (_patched) return;
        _patched = true;
        try
        {
            var panelType = AccessTools.TypeByName("ComputerPanel");
            if (panelType == null)
            {
                Plugin.L.LogWarning("[TS][ComputerFix] 未找到 ComputerPanel 类型");
                return;
            }

            var open = AccessTools.Method(panelType, "OpenPanel");
            if (open != null)
            {
                var pre = new HarmonyMethod(typeof(TeleportConsoleComputerFix).GetMethod(nameof(OpenPanelPrefix), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));
                h.Patch(open, prefix: pre);
                Plugin.L.LogInfo("[TS][ComputerFix] 已挂钩 ComputerPanel.OpenPanel (900101 三项菜单)");
            }
            else Plugin.L.LogWarning("[TS][ComputerFix] 未找到 ComputerPanel.OpenPanel");

            var exec = AccessTools.Method(panelType, "ExecuteResult");
            if (exec != null)
            {
                var pre2 = new HarmonyMethod(typeof(TeleportConsoleComputerFix).GetMethod(nameof(ExecuteResultPrefix), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));
                h.Patch(exec, prefix: pre2);
                Plugin.L.LogInfo("[TS][ComputerFix] 已挂钩 ComputerPanel.ExecuteResult (TS_* 分派)");
            }
            else Plugin.L.LogWarning("[TS][ComputerFix] 未找到 ComputerPanel.ExecuteResult");

            var click = AccessTools.Method(panelType, "OnComputerNodeClick");
            if (click != null)
            {
                var pre3 = new HarmonyMethod(typeof(TeleportConsoleComputerFix).GetMethod(nameof(OnComputerNodeClickPrefix), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));
                h.Patch(click, prefix: pre3);
                Plugin.L.LogInfo("[TS][ComputerFix] 已挂钩 ComputerPanel.OnComputerNodeClick (children 透传)");
            }
        }
        catch (Exception e)
        {
            Plugin.L.LogWarning($"[TS][ComputerFix] 挂钩异常: {e.Message.Split('\n')[0]}");
        }
    }

    // ComputerPanel.OpenPanel(ComputerData m_computerData, TerrainObject_Computer m_computer)
    // 注意参数名必须与原方法一致：m_computerData / m_computer
    public static bool OpenPanelPrefix(object __instance, ref object m_computerData, object m_computer)
    {
        try
        {
            var t = m_computer as TerrainObject;
            if (t == null)
            {
                // 尝试从 m_computer 的 Component 上溯 TerrainObject
                if (m_computer is Component comp)
                {
                    var tr = comp.transform;
                    int d = 0;
                    while (tr != null && d++ < 6)
                    {
                        foreach (var c in tr.GetComponents<Component>())
                        {
                            if (c is TerrainObject tt) { t = tt; break; }
                        }
                        if (t != null) break;
                        tr = tr.parent;
                    }
                }
            }
            if (t != null && t.attr != null && t.attr.id == 900101)
            {
                // 替换为自定义三项菜单
                var custom = CreateCustomComputerData();
                if (custom != null)
                {
                    m_computerData = custom;
                    Plugin.L.LogInfo($"[TS][ComputerFix] 900101 控制台菜单已替换为三项 (console={t.GetInstanceID()})");
                }
                else
                {
                    Plugin.L.LogWarning("[TS][ComputerFix] CreateCustomComputerData 返回 null，回退原菜单");
                }
                PendingConsoleForMap = t;
                CurrentConsole = t;
                // return true 让原方法继续（展示我们注入的 ComputerData）
                return true;
            }
        }
        catch (Exception e)
        {
            Plugin.L.LogWarning($"[TS][ComputerFix] OpenPanelPrefix 异常: {e.Message.Split('\n')[0]}");
        }
        return true;
    }

    // 可选：拦截 children 下钻 - 仅透传，不阻断
    public static bool OnComputerNodeClickPrefix(object __instance, object m_computerNode)
    {
        return true;
    }

    // ComputerPanel.ExecuteResult(string nodeResultStr) — 私有方法，prefix 拦截 TS_* 并分派
    public static bool ExecuteResultPrefix(object __instance, string nodeResultStr)
    {
        // Harmony 位置绑定：第一个 string 参数即 nodeResultStr，对应 spec 中的 __0
        string result = nodeResultStr;
        try
        {
            // 兼容 __0 命名：若通过 __0 注入，method param name 可能为 __0，这里兜底从反射取第一个 string 参数
            if (result == null && __instance != null)
            {
                // 尝试从调用栈？不，Harmony 会正确注入；若为 null 直接放行
            }
        }
        catch { }
        if (string.IsNullOrEmpty(result)) return true;
        if (!result.StartsWith("TS_")) return true;

        try
        {
            // 取 TerrainObject：优先 computerTerrainObjectTemp (dump 0x68)，回退 m_computer / computer 等
            TerrainObject console = null;
            try { console = Reflect.Get(__instance, "computerTerrainObjectTemp") as TerrainObject; } catch { }
            if (console == null) try { console = Reflect.Get(__instance, "m_computer") as TerrainObject; } catch { }
            if (console == null) try { console = Reflect.Get(__instance, "computer") as TerrainObject; } catch { }
            if (console == null) try { console = Reflect.Get(__instance, "computerTerrainObject") as TerrainObject; } catch { }
            // 若仍为 null，尝试 Pending / Current 兜底
            if (console == null) console = PendingConsoleForMap ?? CurrentConsole;
            // 再尝试从 TerrainObject_Computer 类型转换
            if (console == null && __instance != null)
            {
                try
                {
                    var tmp = Reflect.Get(__instance, "computerTerrainObjectTemp");
                    if (tmp is Component c) console = c as TerrainObject;
                    if (console == null && tmp != null)
                    {
                        // tmp 本身可能是 TerrainObject_Computer，尝试 GetComponent
                        if (tmp is Component cc)
                        {
                            foreach (var x in cc.GetComponents<Component>()) if (x is TerrainObject tt) { console = tt; break; }
                        }
                    }
                }
                catch { }
            }

            Plugin.L.LogInfo($"[TS][ComputerFix] ExecuteResult 拦截 {result} console={(console != null ? console.GetInstanceID().ToString() : "null")}");

            switch (result)
            {
                case RenameResult:
                    try
                    {
                        // 关闭电脑面板
                        try { AccessTools.Method(__instance.GetType(), "ClosePanel")?.Invoke(__instance, null); } catch { }
                    }
                    catch { }
                    if (console != null)
                    {
                        try
                        {
                            var uiType = AccessTools.TypeByName("TeleportStationRenameUI");
                            if (uiType != null)
                            {
                                var ensure = AccessTools.Method(uiType, "EnsureExists");
                                var inst = ensure != null ? ensure.Invoke(null, null) : null;
                                var show = AccessTools.Method(uiType, "Show");
                                if (inst != null && show != null) show.Invoke(inst, new object[] { console });
                                else ShowBubble("重命名待实现");
                            }
                            else
                            {
                                // fallback 直接调用
                                try { TeleportStationRenameUI.EnsureExists().Show(console); }
                                catch { ShowBubble("重命名待实现"); }
                            }
                        }
                        catch (Exception ex)
                        {
                            Plugin.L.LogWarning($"[TS][ComputerFix] Show RenameUI 异常: {ex.Message.Split('\n')[0]}");
                            ShowBubble("重命名待实现");
                        }
                    }
                    else ShowBubble("重命名待实现");
                    return false;

                case SelectResult:
                    if (console != null)
                    {
                        PendingConsoleForMap = console;
                        CurrentConsole = console;
                    }
                    try { AccessTools.Method(__instance.GetType(), "ClosePanel")?.Invoke(__instance, null); } catch { }
                    try
                    {
                        // 优先 TeleportMapManager.RequestOpenMap，若不存在则回退 TeleportConsoleUI
                        var mapType = AccessTools.TypeByName("TeleportMapManager");
                        bool handled = false;
                        if (mapType != null)
                        {
                            var req = AccessTools.Method(mapType, "RequestOpenMap");
                            if (req != null)
                            {
                                req.Invoke(null, new object[] { console });
                                handled = true;
                                Plugin.L.LogInfo($"[TS][ComputerFix] TeleportMapManager.RequestOpenMap 已调用 console={console?.GetInstanceID()}");
                            }
                        }
                        if (!handled)
                        {
                            // 回退：打开选点面板（与 E 绑定逻辑一致）
                            try
                            {
                                var ui = TeleportConsoleUI.Instance ?? TeleportConsoleUI.EnsureExists();
                                ui.ShowForConsole(console);
                            }
                            catch (Exception ex2) { Plugin.L.LogWarning($"[TS][ComputerFix] ShowForConsole 异常: {ex2.Message.Split('\n')[0]}"); }
                        }
                    }
                    catch (Exception ex)
                    {
                        Plugin.L.LogWarning($"[TS][ComputerFix] SelectMap 分派异常: {ex.Message.Split('\n')[0]}");
                        try { TeleportConsoleUI.EnsureExists().ShowForConsole(console); } catch { }
                    }
                    return false;

                case ExitResult:
                    try { AccessTools.Method(__instance.GetType(), "ClosePanel")?.Invoke(__instance, null); } catch { }
                    return false;

                default:
                    return true;
            }
        }
        catch (Exception e)
        {
            Plugin.L.LogWarning($"[TS][ComputerFix] ExecuteResultPrefix 异常: {e.Message.Split('\n')[0]}");
            return true;
        }
    }

    // 兼容 spec 中 __0 命名入口（Harmony 位置绑定别名），转发到主入口
    public static bool ExecuteResultPrefix_Align(object __instance, string __0)
    {
        return ExecuteResultPrefix(__instance, __0);
    }

    private static object CreateCustomComputerData()
    {
        try
        {
            var dataType = AccessTools.TypeByName("ComputerData");
            var nodeType = AccessTools.TypeByName("ComputerNodeData");
            if (dataType == null || nodeType == null)
            {
                Plugin.L.LogWarning($"[TS][ComputerFix] 类型未找到 ComputerData={dataType} ComputerNodeData={nodeType}");
                return null;
            }

            var data = Activator.CreateInstance(dataType);
            if (data == null) return null;

            Reflect.Set(data, "computerDataID", 900101);
            Reflect.Set(data, "computerMainPageName", "传送站控制台");
            Reflect.Set(data, "repeatFlag", false);

            // 探测 List 类型（优先字段声明类型）
            Type listType = null;
            try
            {
                var f = dataType.GetField("nodeList", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f != null) listType = f.FieldType;
                if (listType == null)
                {
                    var f2 = dataType.GetField("rootNodes", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (f2 != null) listType = f2.FieldType;
                }
            }
            catch { }

            object nodeList = null;
            object rootNodes = null;
            try
            {
                if (listType != null) { nodeList = Activator.CreateInstance(listType); rootNodes = Activator.CreateInstance(listType); }
            }
            catch { }

            // 回退：BCL List<ComputerNodeData>
            if (nodeList == null || rootNodes == null)
            {
                try
                {
                    var bclListType = typeof(List<>).MakeGenericType(nodeType);
                    if (nodeList == null) nodeList = Activator.CreateInstance(bclListType);
                    if (rootNodes == null) rootNodes = Activator.CreateInstance(bclListType);
                }
                catch { }
            }

            // 最后回退：Il2Cpp List
            if (nodeList == null || rootNodes == null)
            {
                try
                {
                    var ilType = AccessTools.TypeByName("Il2CppSystem.Collections.Generic.List`1");
                    if (ilType != null)
                    {
                        var ilListType = ilType.MakeGenericType(nodeType);
                        if (nodeList == null) nodeList = Activator.CreateInstance(ilListType);
                        if (rootNodes == null) rootNodes = Activator.CreateInstance(ilListType);
                    }
                }
                catch { }
            }

            if (nodeList == null || rootNodes == null)
            {
                Plugin.L.LogWarning("[TS][ComputerFix] 无法创建 nodeList/rootNodes 列表实例");
                return null;
            }

            Reflect.Set(data, "nodeList", nodeList);
            Reflect.Set(data, "rootNodes", rootNodes);

            // helper 创建节点
            object CreateNode(int id, int parent, string content, string resultVal)
            {
                var n = Activator.CreateInstance(nodeType);
                Reflect.Set(n, "nodeID", id);
                Reflect.Set(n, "parentNodeID", parent);
                Reflect.Set(n, "contentStr", content);
                Reflect.Set(n, "nodeResult", resultVal);
                // children 空列表
                try
                {
                    var cf = nodeType.GetField("children", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    Type childListType = cf != null ? cf.FieldType : null;
                    object childList = null;
                    if (childListType != null) try { childList = Activator.CreateInstance(childListType); } catch { }
                    if (childList == null)
                    {
                        try { childList = Activator.CreateInstance(typeof(List<>).MakeGenericType(nodeType)); } catch { }
                    }
                    if (childList != null) Reflect.Set(n, "children", childList);
                    else Reflect.Set(n, "children", null);
                }
                catch { try { Reflect.Set(n, "children", null); } catch { } }
                return n;
            }

            var n1 = CreateNode(90010101, 0, "给当前的传送站命名", RenameResult);
            var n2 = CreateNode(90010102, 0, "选择传送目的地 (打开地图)", SelectResult);
            var n3 = CreateNode(90010103, 0, "退出", ExitResult);

            // 加入列表：通过反射调用 Add
            void AddTo(object list, object item)
            {
                try
                {
                    var m = list.GetType().GetMethod("Add");
                    if (m != null) m.Invoke(list, new object[] { item });
                }
                catch (Exception e) { Plugin.L.LogWarning($"[TS][ComputerFix] Add 节点异常: {e.Message.Split('\n')[0]}"); }
            }

            AddTo(nodeList, n1); AddTo(rootNodes, n1);
            AddTo(nodeList, n2); AddTo(rootNodes, n2);
            AddTo(nodeList, n3); AddTo(rootNodes, n3);

            // 尝试调用 Initialize 初始化父子关系
            try { AccessTools.Method(dataType, "Initialize")?.Invoke(data, null); } catch { }

            return data;
        }
        catch (Exception e)
        {
            Plugin.L.LogWarning($"[TS][ComputerFix] CreateCustomComputerData 异常: {e.Message.Split('\n')[0]}");
            return null;
        }
    }

    private static void ShowBubble(string msg)
    {
        try
        {
            var t = AccessTools.TypeByName("BasicCharacterController");
            if (t == null) t = AccessTools.TypeByName("HumanCharacterController");
            var m = t?.GetMethod("ShowDialogueBubble", BindingFlags.Public | BindingFlags.Instance);
            if (m == null) return;
            object player = null;
            try
            {
                var gc = GameController.instance;
                if (gc != null)
                {
                    player = Reflect.Get(gc, "player");
                    if (player == null) player = Reflect.Get(gc, "playerCharacter");
                    if (player == null) player = Reflect.Get(gc, "localPlayer");
                    if (player == null) player = Reflect.Get(gc, "controlledCharacter");
                    if (player == null) player = Reflect.Get(gc, "mainCharacter");
                }
            }
            catch { }
            if (player == null)
            {
                try
                {
                    var go = GameObject.FindWithTag("Player");
                    if (go != null && t != null)
                    {
                        foreach (var c in go.GetComponents<Component>()) if (c != null && c.GetType().Name == t.Name) { player = c; break; }
                        if (player == null) foreach (var c in go.GetComponentsInChildren<Component>(true)) if (c != null && c.GetType().Name == t.Name) { player = c; break; }
                    }
                }
                catch { }
            }
            if (player != null) m.Invoke(player, new object[] { msg, 4f });
        }
        catch { }
    }
}
