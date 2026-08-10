using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using UnityEngine;

namespace PortableFridgeProbe;

/// <summary>
/// 便携小冰箱可行性探查插件 v0.3.0（只读，无副作用）：
/// - 启动延迟打印 弹药箱(532) 全部属性（找 Backpack 容量）、电瓶(85) 特性配置深挖
/// - F9：完整 dump 玩家背包所有【食物】ItemData 的全部字段/属性/属性对
///   用户放 1 个未腐烂 + 1 个已腐烂的同类食物，F9 对比差异 → 定位腐烂/保鲜参数
/// </summary>
[BepInPlugin("com.zedzone.portablefridgeprobe", "PortableFridgeProbe", "0.3.0")]
public class Plugin : BasePlugin
{
    internal static Plugin Instance;
    internal static ManualLogSource L;

    public override void Load()
    {
        Instance = this;
        L = Log;
        AddComponent<ProbeComponent>();
        Log.LogInfo("[FridgeProbe] 探查插件已加载 (v0.3.0)");
    }
}

public class ProbeComponent : MonoBehaviour
{
    private float _probeTimer = 20f;
    private bool _probeDone;

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.F9))
        {
            try { DumpPlayerFood("F9 食物完整对比快照"); }
            catch (Exception e) { Plugin.L.LogError($"[FridgeProbe] F9 异常: {e}"); }
        }

        if (_probeDone) return;
        _probeTimer -= Time.deltaTime;
        if (_probeTimer > 0f) return;
        _probeDone = true;

        try { DumpItemAttrDetails(); }
        catch (Exception e) { Plugin.L.LogError($"[FridgeProbe] 物品定义探查异常: {e}"); }

        try { DumpPlayerFood("启动自动快照"); }
        catch (Exception e) { Plugin.L.LogError($"[FridgeProbe] 背包快照异常: {e}"); }
    }

    // ---------- 物品定义细节 ----------

    private void DumpItemAttrDetails()
    {
        var mgr = ItemManager.instance;
        if (mgr == null) { Plugin.L.LogWarning("[FridgeProbe] ItemManager 未就绪"); return; }

        foreach (var id in new[] { 532, 85 })
        {
            try
            {
                var attr = mgr.GetItemAttrById(id);
                if (attr == null) continue;
                var sb = new StringBuilder();
                sb.AppendLine($"[FridgeProbe] === 物品 {id} attr 运行时类型: {attr.GetType().FullName} ===");

                // 全部实例属性（找非空/非默认值）
                foreach (var p in attr.GetType().GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (p.GetIndexParameters().Length > 0) continue;
                    object v;
                    try { v = p.GetValue(attr); }
                    catch { continue; }
                    if (v == null) continue;
                    var s = v.ToString();
                    if (s.Length > 120) s = s.Substring(0, 120) + "...";
                    sb.AppendLine($"  {p.Name} = {s}");
                }
                Plugin.L.LogInfo(sb.ToString());
            }
            catch (Exception e) { Plugin.L.LogError($"[FridgeProbe] 物品 {id} 详情异常: {e}"); }
        }

        // 电瓶 85 的 ItemFeatureConfigData 深挖
        try
        {
            var attr85 = mgr.GetItemAttrById(85);
            var cfgList = attr85?.itemFeatureConfigDatas;
            if (cfgList != null && cfgList.Count > 0)
            {
                var sb = new StringBuilder();
                sb.AppendLine("[FridgeProbe] === 电瓶(85) itemFeatureConfigDatas 深挖 ===");
                foreach (var c in cfgList)
                {
                    sb.AppendLine($"  元素类型: {c.GetType().FullName}");
                    foreach (var p in c.GetType().GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        if (p.GetIndexParameters().Length > 0) continue;
                        object v;
                        try { v = p.GetValue(c); } catch { continue; }
                        if (v == null) continue;
                        var s = v.ToString();
                        if (s.Length > 100) s = s.Substring(0, 100) + "...";
                        sb.AppendLine($"    {p.Name} = {s}");
                    }
                }
                Plugin.L.LogInfo(sb.ToString());
            }
        }
        catch (Exception e) { Plugin.L.LogError($"[FridgeProbe] 电瓶配置深挖异常: {e}"); }
    }

    // ---------- 玩家食物完整 dump ----------

    private void DumpPlayerFood(string tag)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[FridgeProbe] ===== 食物完整对比快照 [{tag}] =====");
        try
        {
            var gc = GameController.instance;
            if (gc == null) { sb.AppendLine("  GameController 未就绪"); return; }
            var pc = gc.playerCharacter;
            if (pc == null) { sb.AppendLine("  playerCharacter 未就绪"); return; }
            var cd = pc.characterData;
            if (cd == null) { sb.AppendLine("  characterData 未就绪"); return; }
            var inv = cd.inventoryData;
            if (inv == null) { sb.AppendLine("  inventoryData 未就绪"); return; }

            sb.AppendLine($"  背包 Ptr={inv.Pointer.ToInt64():X} 物品总数={inv.itemList?.Count ?? -1}");

            var list = inv.itemList;
            if (list != null)
            {
                int foodCount = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    var item = list[i];
                    if (item == null) continue;
                    ItemAttr attr;
                    try { attr = ItemManager.instance.GetItemAttrById(item.itemId); }
                    catch { continue; }
                    if (attr == null || attr.itemType != ItemType.Food) continue;

                    foodCount++;
                    DumpItemFull(sb, item, attr, foodCount);
                    if (foodCount >= 20) { sb.AppendLine("  ...(食物过多，截断)"); break; }
                }
                sb.AppendLine($"  食物数量: {foodCount}");
            }
        }
        catch (Exception e) { sb.AppendLine($"  异常: {e}"); }
        Plugin.L.LogInfo(sb.ToString());
    }

    private void DumpItemFull(StringBuilder sb, ItemData item, ItemAttr attr, int idx)
    {
        string name = "";
        try { name = attr.itemName_Runtime ?? ""; } catch { }
        sb.AppendLine($"  ---- [{idx}] itemId={item.itemId} [{name}] Ptr=0x{item.Pointer.ToInt64():X} ----");

        // 1) ItemData 全部实例属性
        foreach (var p in item.GetType().GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0) continue;
            object v;
            try { v = p.GetValue(item); }
            catch { continue; }
            if (v == null) continue;
            var s = v.ToString();
            if (s.Length > 100) s = s.Substring(0, 100) + "...";
            sb.AppendLine($"    ItemData.{p.Name} = {s}");
        }

        // 2) properties（直接类型访问 + 反射双保险）
        try
        {
            var props = item.itemPropertyPairs;
            if (props != null)
            {
                sb.AppendLine($"    itemPropertyPairs (Count={props.Count}):");
                var e = props.GetEnumerator();
                while (e.MoveNext())
                {
                    try
                    {
                        var cur = e.Current;
                        var k = cur?.GetType().GetProperty("key", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(cur);
                        var v = cur?.GetType().GetProperty("value", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(cur);
                        sb.AppendLine($"      [{k}] = {v}");
                    }
                    catch (Exception ex) { sb.AppendLine($"      (枚举异常 {ex.Message})"); }
                }
            }
        }
        catch (Exception ex) { sb.AppendLine($"    itemPropertyPairs 访问异常: {ex.Message}"); }
    }
}
