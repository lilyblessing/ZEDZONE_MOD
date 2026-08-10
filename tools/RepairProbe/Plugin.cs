using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using UnityEngine;

namespace RepairProbe;

[BepInPlugin("com.zedzone.tool.repairprobe", "RepairProbe", "1.0.0")]
public class Plugin : BasePlugin
{
    internal static Plugin Instance;
    internal static ManualLogSource L;
    public override void Load()
    {
        Instance = this;
        L = Log;
        AddComponent<Prober>();
        Log.LogInfo("[RepairProbe] 已加载 (探查工具, 15 秒后执行)");
    }
}

public class Prober : MonoBehaviour
{
    private float _timer = 15f;
    private bool _done;

    private void Update()
    {
        if (_done) return;
        _timer -= Time.deltaTime;
        if (_timer > 0f) return;
        _done = true;
        try { Run(); }
        catch (System.Exception e) { Plugin.L.LogError($"[RepairProbe] 执行失败: {e}"); }
    }

    private void Run()
    {
        var mgr = ItemManager.instance;
        if (mgr == null)
        {
            Plugin.L.LogError("[RepairProbe] ItemManager.instance 为 null，跳过");
            return;
        }

        // 1) 探查 mod 弹链的 repairData
        DumpAttr(871704, "mod 弹链(200发)");

        // 2) 对照：遍历所有物品，找 repairData 非空的原版物品，打印其修复配方结构
        try
        {
            var list = mgr.itemList;
            if (list == null) { Plugin.L.LogWarning("[RepairProbe] itemList null"); return; }
            Plugin.L.LogInfo($"[RepairProbe] itemList.Count = {list.Count}");
            int shown = 0;
            for (int i = 0; i < list.Count && shown < 6; i++)
            {
                var a = list[i];
                if (a == null) continue;
                try
                {
                    var rd = a.repairData;
                    if (rd == null) continue;
                    var items = rd.recipeItems;
                    int cnt = (items != null) ? items.Count : -1;
                    if (cnt <= 0) continue;
                    Plugin.L.LogInfo($"[RepairProbe] 对照 itemId={a.itemId} name={GetName(a)} type={GetType(a)} recipeItems.Count={cnt} craftPlatform={rd.craftPlatform} toolType={rd.toolType} craftTime={rd.craftTime} outputItemNumber={rd.outputItemNumber}");
                    for (int j = 0; j < cnt; j++)
                    {
                        var ri = items[j];
                        Plugin.L.LogInfo($"    recipeItems[{j}]: itemId={ri.itemId} itemNumber={ri.itemNumber}");
                    }
                    shown++;
                }
                catch (System.Exception e) { Plugin.L.LogWarning($"[RepairProbe] 遍历项 {i} 失败: {e.Message}"); }
            }
            if (shown == 0) Plugin.L.LogWarning("[RepairProbe] 未找到任何 repairData 非空的原版物品");
        }
        catch (System.Exception e)
        {
            Plugin.L.LogError($"[RepairProbe] 遍历 itemList 失败: {e}");
        }

        Plugin.L.LogInfo("[RepairProbe] 探查完成");
    }

    private void DumpAttr(int id, string label)
    {
        try
        {
            var attr = ItemManager.instance.GetItemAttrById(id);
            if (attr == null)
            {
                Plugin.L.LogWarning($"[RepairProbe] GetItemAttrById({id}) 返回 null ({label})");
                return;
            }
            Plugin.L.LogInfo($"[RepairProbe] {label}: itemId={attr.itemId} name={GetName(attr)} type={GetType(attr)}");
            var rd = attr.repairData;
            if (rd == null)
            {
                Plugin.L.LogWarning($"[RepairProbe] {label} repairData == null (未被解析或未注入)");
                return;
            }
            var items = rd.recipeItems;
            int cnt = (items != null) ? items.Count : -1;
            Plugin.L.LogInfo($"[RepairProbe] {label} repairData != null: recipeItems.Count={cnt} craftPlatform={rd.craftPlatform} toolType={rd.toolType} craftTime={rd.craftTime} itemId={rd.itemId} outputItemNumber={rd.outputItemNumber}");
            if (items != null)
            {
                for (int j = 0; j < items.Count; j++)
                {
                    var ri = items[j];
                    Plugin.L.LogInfo($"    recipeItems[{j}]: itemId={ri.itemId} itemNumber={ri.itemNumber}");
                }
            }
        }
        catch (System.Exception e)
        {
            Plugin.L.LogError($"[RepairProbe] DumpAttr({id}) 失败: {e}");
        }
    }

    private static string GetName(object o)
    {
        try
        {
            var p = o.GetType().GetProperty("itemName_Runtime");
            return p?.GetValue(o)?.ToString() ?? "?";
        }
        catch { return "?"; }
    }

    private static string GetType(object o)
    {
        try
        {
            var p = o.GetType().GetProperty("itemType");
            return p?.GetValue(o)?.ToString() ?? "?";
        }
        catch { return "?"; }
    }
}
