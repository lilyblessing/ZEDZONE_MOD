using System;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using UnityEngine;

namespace NoteTagProbe;

/// <summary>
/// 命名牌拖放状态探查（临时）：F9 打印所有格子的 itemdata / itemdataTemp / drag_icon 状态。
/// 用途：验证"拖放命名牌 → 拦截 DropOn 后 itemdataTemp 是否残留"的假设。
/// </summary>
[BepInPlugin("com.zedzone.notetagprobe", "NoteTagProbe", "0.1.0")]
public class Plugin : BasePlugin
{
    internal static Plugin Instance;
    internal static ManualLogSource L;

    public override void Load()
    {
        Instance = this;
        L = Log;
        AddComponent<ProbeComponent>();
        Log.LogInfo("[NoteTagProbe] 探查插件已加载 (F9 打印拖拽状态)");
    }
}

public class ProbeComponent : MonoBehaviour
{
    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.F9))
        {
            try { DumpDragState(); }
            catch (Exception e) { Plugin.L.LogError($"[NoteTagProbe] 异常: {e}"); }
        }
    }

    private void DumpDragState()
    {
        var sb = new StringBuilder();
        sb.AppendLine("[NoteTagProbe] ===== 拖拽状态快照 =====");
        try
        {
            var list = BasicItemUI.ActiveObjects;
            int total = list != null ? list.Count : -1;
            sb.AppendLine($"  ActiveObjects={total}");
            if (list == null) { Plugin.L.LogInfo(sb.ToString()); return; }

            int interesting = 0;
            for (int i = 0; i < list.Count; i++)
            {
                var ui = list[i];
                if (ui == null) continue;

                var item = ui.itemdata;
                var temp = ui.itemdataTemp;

                string itemDesc = item != null ? $"id={item.itemId} x{item.itemNumberFloat:F0} ptr=0x{item.Pointer.ToInt64():X}" : "null";
                string tempDesc = temp != null ? $"id={temp.itemId} x{temp.itemNumberFloat:F0} ptr=0x{temp.Pointer.ToInt64():X}" : "null";

                // 只打印：命名牌格子 / 有 temp 的格子 / 拖拽图标激活的格子
                bool isNameTag = (item != null && item.itemId == 900000) || (temp != null && temp.itemId == 900000);
                bool hasTemp = temp != null;
                bool hasDragIcon = ui.drag_icon != null && ui.drag_icon.activeInHierarchy;

                if (isNameTag || hasTemp || hasDragIcon)
                {
                    interesting++;
                    string coord = "?";
                    try { coord = (ui.itemdata?.inventoryCoordinate).ToString(); } catch { }
                    sb.AppendLine($"  [{i}] coord={coord} itemdata={itemDesc} itemdataTemp={tempDesc} dragIcon={hasDragIcon}");
                }
            }
            sb.AppendLine($"  关注格子数: {interesting}");
        }
        catch (Exception e) { sb.AppendLine($"  异常: {e}"); }
        Plugin.L.LogInfo(sb.ToString());
    }
}
