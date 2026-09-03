using System;
using HarmonyLib;
using ZedZoneShared;

namespace TeleportStationPlugin;

/// <summary>
/// P6.2 控制台电脑菜单劫持 —— 已退役清理（P0#4）。
/// 反编译定案：ComputerPanel.OpenPanel VA 0x180588520（param_2=ComputerData存+0x60，
/// param_3=TerrainObject_Computer存+0x68）与 DOS 通讯终端链路无交叉；
/// ComputerPanel.ExecuteResult VA 0x180587E50 是链2（电脑菜单分派）专属，
/// 原生通讯终端不经过此链。旧的 OpenPanelPrefix（m_computer as TerrainObject +
/// attr.id==900101 判定及 6 层上溯兜底）与 ExecuteResultPrefix（TS_* 分派）挂钩
/// 均属无效挂钩：900101 控制台已不走 ComputerPanel，主路径为 MenuUI。
/// 因此 EnsurePatch 不再挂任何钩，三 prefix 仅保留透传签名（兼容外部引用），
/// ExecuteResultPrefix_Align 死代码已删除（定义无 Patch 引用）。
/// 原生通讯终端链路零触碰：本文件不读不写任何游戏 ComputerPanel/DOS 字段。
/// </summary>
public static class TeleportConsoleComputerFix
{
    /// <summary>选中第二项后记录，供 MapManager/选点面板使用（当前待操作控制台）</summary>
    public static TerrainObject PendingConsoleForMap;

    /// <summary>便于外部读取当前控制台（与 PendingConsoleForMap 同步）</summary>
    public static TerrainObject CurrentConsole;

    public const string RenameResult = "TS_Rename";
    public const string SelectResult = "TS_SelectMap";
    public const string ExitResult = "TS_Exit";

    /// <summary>
    /// P0#4 清理后：不再挂钩 ComputerPanel 任何方法（OpenPanel/ExecuteResult/
    /// OnComputerNodeClick 均不 Patch）。保留方法签名供 Plugin.cs 兼容调用。
    /// </summary>
    public static void EnsurePatch(Harmony h)
    {
        // 故意不 Patch：链2 无关（证据 VA 见类注释）。仅打一条日志便于验证。
        try { Plugin.L.LogInfo("[TS][ComputerFix] 已停用：ComputerPanel 无有效 Patch（P0#4 链2无关清理）"); } catch { }
    }

    // 透传保留：ComputerPanel.OpenPanel(ComputerData m_computerData, TerrainObject_Computer m_computer)
    // 不做任何菜单替换，直接放行原生逻辑。
    public static bool OpenPanelPrefix(object __instance, ref object m_computerData, object m_computer)
    {
        return true;
    }

    // 透传保留：仅透传，不阻断 children 下钻。
    public static bool OnComputerNodeClickPrefix(object __instance, object m_computerNode)
    {
        return true;
    }

    // 透传保留：ComputerPanel.ExecuteResult(string nodeResultStr)
    // 不再拦截 TS_*，直接放行原生逻辑。
    public static bool ExecuteResultPrefix(object __instance, string nodeResultStr)
    {
        return true;
    }

    // NOTE: ExecuteResultPrefix_Align（__0 别名转发）已删除：无 Patch 引用死代码（P0#4）。

    private static object CreateCustomComputerData()
    {
        // 已退役：不再构造自定义三项菜单。
        return null;
    }

    private static void ShowBubble(string msg)
    {
        // 已退役：不再触碰角色/气泡链路（原生通讯终端零触碰）。
    }
}
