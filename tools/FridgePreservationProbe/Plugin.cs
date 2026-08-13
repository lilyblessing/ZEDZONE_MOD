using System;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using UnityEngine;

namespace FridgePreservationProbe;

/// <summary>
/// 原版冰箱保鲜机制探查 v0.1.0：
/// 回答「游戏冰箱保鲜具体怎么实现」——用于对比/优化便携小冰箱。
/// 核心观察点（F9 触发快照，操作前后对比）：
/// - foodAgingRateWhenPowered（通电时食物老化速率配置值）
/// - 通电状态下容器内食物 properties[0]（采集时间戳）是否被前移
/// - PROP_LAST_REFRIGERATION_TIME / PROP_POWERED_AT_LAST_TICK 持久化键的值与变化
/// - 断电后的表现（保鲜停止？是否有补偿）
/// </summary>
[BepInPlugin("com.zedzone.tool.fridgepresprobe", "FridgePreservationProbe", "0.1.0")]
public class Plugin : BasePlugin
{
    internal static Plugin Instance;
    internal static ManualLogSource L;

    public override void Load()
    {
        Instance = this;
        L = Log;
        AddComponent<Probe>();
        Log.LogInfo("[FridgeProbe] 原版冰箱保鲜探查已加载 (v0.1.0)");
    }
}

public class Probe : MonoBehaviour
{
    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.F9))
        {
            try { Snapshot("F9 冰箱保鲜快照"); }
            catch (Exception e) { Plugin.L.LogError($"[FridgeProbe] F9 异常: {e}"); }
        }
        if (Input.GetKeyDown(KeyCode.F8))
        {
            try { FastForward(1f); }
            catch (Exception e) { Plugin.L.LogError($"[FridgeProbe] 加速时间异常: {e}"); }
        }
        if (Input.GetKeyDown(KeyCode.F7))
        {
            try { FastForward(0.1f); }
            catch (Exception e) { Plugin.L.LogError($"[FridgeProbe] 加速时间异常: {e}"); }
        }
    }

    /// <summary>调用 TimeController.AddTime 推进游戏时间（制作耗时同源机制，走完整时间更新链路）。</summary>
    private static void FastForward(float days)
    {
        var t = typeof(TimeController);
        var inst = GetTimeControllerInstance();
        var m = t.GetMethod("AddTime", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            null, new[] { typeof(float) }, null);
        if (inst == null || m == null)
        {
            Plugin.L.LogError("[FridgeProbe] 无法推进时间（instance 或 AddTime 不可用）");
            return;
        }
        m.Invoke(inst, new object[] { days });
        Plugin.L.LogInfo($"[FridgeProbe] 已推进时间 +{days} 游戏天（F7=0.1天 / F8=1天）");
    }

    private static object GetTimeControllerInstance()
    {
        try
        {
            var t = typeof(TimeController);
            var instProp = t.GetProperty("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                        ?? t.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            return instProp?.GetValue(null);
        }
        catch { return null; }
    }

    private void Snapshot(string tag)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[FridgeProbe] ===== {tag} =====");
        sb.AppendLine($"  当前游戏时间 = {GetGameTime()}");

        try
        {
            var fridges = Resources.FindObjectsOfTypeAll<TerrainObject_Production_Fridge>();
            sb.AppendLine($"  冰箱数量 = {(fridges != null ? fridges.Length : 0)}");
            if (fridges == null) return;
            for (int i = 0; i < fridges.Length; i++)
            {
                var f = fridges[i];
                if (f == null) continue;
                sb.AppendLine($"  ---- 冰箱[{i}] Ptr=0x{f.Pointer.ToInt64():X} [{f.name}] ----");
                try { sb.AppendLine($"    foodAgingRateWhenPowered = {f.foodAgingRateWhenPowered}"); }
                catch (Exception e) { sb.AppendLine($"    foodAgingRateWhenPowered 读取异常: {e.Message}"); }
                try { sb.AppendLine($"    poweredObject.activeSelf = {f.poweredObject?.activeSelf}"); } catch { }
                try { sb.AppendLine($"    unpoweredObject.activeSelf = {f.unpoweredObject?.activeSelf}"); } catch { }
                DumpPersistentProps(sb, f);
                DumpFoods(sb, f);
            }
        }
        catch (Exception e) { sb.AppendLine($"  总异常: {e}"); }
        Plugin.L.LogInfo(sb.ToString());
    }

    // ---------- 持久化键 ----------

    private void DumpPersistentProps(StringBuilder sb, TerrainObject_Production_Fridge f)
    {
        string keyRefr = GetConstString(f.GetType(), "PROP_LAST_REFRIGERATION_TIME");
        string keyPowered = GetConstString(f.GetType(), "PROP_POWERED_AT_LAST_TICK");
        sb.AppendLine($"    常量键名: PROP_LAST_REFRIGERATION_TIME={keyRefr ?? "?"} / PROP_POWERED_AT_LAST_TICK={keyPowered ?? "?"}");

        foreach (var (host, hostName) in new (object, string)[] { (f, "fridge"), (f.objectData, "objectData") })
        {
            if (host == null) continue;
            foreach (var key in new[] { keyRefr, keyPowered })
            {
                if (string.IsNullOrEmpty(key)) continue;
                try
                {
                    var v = CallGetProperty(host, key);
                    sb.AppendLine($"    {hostName}.GetProperty(\"{key}\") = {v ?? "<null>"}");
                }
                catch (Exception e) { sb.AppendLine($"    {hostName}.GetProperty 异常: {e.Message}"); }
            }
        }
    }

    // ---------- 容器内食物 ----------

    private void DumpFoods(StringBuilder sb, TerrainObject_Production_Fridge f)
    {
        if (f.objectData == null) { sb.AppendLine("    objectData = null"); return; }
        foreach (var inv in new[] { f.objectData.inventoryData, f.objectData.inventoryData2, f.objectData.inventoryData3 })
        {
            if (inv == null) continue;
            var list = inv.itemList;
            int n = 0;
            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    var item = list[i];
                    if (item == null) continue;
                    var attr = ItemManager.instance?.GetItemAttrById(item.itemId);
                    if (attr == null || attr.itemType != ItemType.Food) continue;
                    n++;
                    sb.AppendLine($"    [食物] itemId={item.itemId} [{NameOf(item.itemId)}] Ptr=0x{item.Pointer.ToInt64():X}");
                    sb.AppendLine($"      properties[0] = {ReadProps0(item)}（采集时间戳，游戏天）");
                    sb.AppendLine($"      perishTime = {GetPerishTime(attr)}（游戏天）");
                    try { sb.AppendLine($"      IsFoodExpired = {ItemData.IsFoodExpired(item, attr)}"); }
                    catch (Exception e) { sb.AppendLine($"      IsFoodExpired 异常: {e.Message}"); }
                }
            }
            sb.AppendLine($"    inventoryData 食物数 = {n}");
        }
    }

    // ---------- 辅助 ----------

    private static string GetGameTime()
    {
        try
        {
            var t = typeof(TimeController);
            var instProp = t.GetProperty("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                        ?? t.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (instProp == null) return "?（无 instance 属性）";
            var inst = instProp.GetValue(null);
            if (inst == null) return "?（instance null）";
            // 找 float 时间属性
            foreach (var p in inst.GetType().GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (p.PropertyType != typeof(float)) continue;
                object v;
                try { v = p.GetValue(inst); } catch { continue; }
                if (v is float fv && fv >= 0f && fv < 100000f)
                    return $"{p.Name}={fv:F4}";
            }
            return "?（未找到时间属性）";
        }
        catch (Exception e) { return $"?（{e.Message}）"; }
    }

    private static string GetConstString(Type t, string field)
    {
        try
        {
            var f = t.GetField(field, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            return f?.GetValue(null)?.ToString();
        }
        catch { return null; }
    }

    private static object CallGetProperty(object obj, string key)
    {
        var m = obj.GetType().GetMethod("GetProperty", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        return m?.Invoke(obj, new object[] { key });
    }

    private static string ReadProps0(ItemData item)
    {
        try
        {
            var props = item.properties;
            if (props == null || props.Length == 0) return "<无>";
            return props[0].ToString("F4");
        }
        catch (Exception e) { return $"<异常:{e.Message}>"; }
    }

    private static object GetPerishTime(ItemAttr attr)
    {
        try
        {
            var p = attr.GetType().GetProperty("perishTime", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return p?.GetValue(attr);
        }
        catch { return "?"; }
    }

    private static string NameOf(int id)
    {
        try { return ItemManager.instance?.GetItemAttrById(id)?.itemName_Runtime ?? "?"; }
        catch { return "?"; }
    }
}
