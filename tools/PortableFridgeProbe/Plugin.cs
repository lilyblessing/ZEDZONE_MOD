using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using UnityEngine;

namespace PortableFridgeProbe;

/// <summary>
/// 便携小冰箱可行性探查 v0.3.4：
/// - F9：食物完整 dump + 电瓶 dump + BatteryConsuming 物品扫描（找手电筒配置做电池槽）
/// </summary>
[BepInPlugin("com.zedzone.portablefridgeprobe", "PortableFridgeProbe", "0.3.4")]
public class Plugin : BasePlugin
{
    internal static Plugin Instance;
    internal static ManualLogSource L;

    public override void Load()
    {
        Instance = this;
        L = Log;
        AddComponent<ProbeComponent>();
        Log.LogInfo("[FridgeProbe] 探查插件已加载 (v0.3.4)");
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
            try { DumpBattery("F9 电瓶快照"); }
            catch (Exception e) { Plugin.L.LogError($"[FridgeProbe] 电瓶 F9 异常: {e}"); }
            try { FindBatteryConsumingItems(); }
            catch (Exception e) { Plugin.L.LogError($"[FridgeProbe] 扫描异常: {e}"); }
            try { DumpSlotDevices("F9 电池槽设备快照"); }
            catch (Exception e) { Plugin.L.LogError($"[FridgeProbe] 槽设备异常: {e}"); }
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

        foreach (var id in new[] { 91, 93, 85 })
        {
            try
            {
                var attr = mgr.GetItemAttrById(id);
                if (attr == null) continue;
                var sb = new StringBuilder();
                sb.AppendLine($"[FridgeProbe] === 物品 {id} attr 运行时类型: {attr.GetType().FullName} ===");

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

                // 特性配置深挖
                var dic2 = GetProp(attr, "itemFeatureDataDic");
                sb.AppendLine($"  itemFeatureDataDic (Count={CountOf(dic2)}):");
                foreach (var kv in EnumerateKeyValue(dic2))
                    sb.AppendLine($"    [{kv.Key}] = {kv.Value} ({kv.Value?.GetType().FullName})  => {DescribeObject(kv.Value)}");

                var cfg = GetProp(attr, "itemFeatureConfigDatas");
                sb.AppendLine($"  itemFeatureConfigDatas (Count={CountOf(cfg)}):");
                foreach (var c in Enumerate(cfg))
                {
                    sb.AppendLine($"    --- {c?.GetType().FullName} ---");
                    DumpAllProperties(sb, c, "      ");
                    // 深挖字段级配置
                    var fieldCfgs = GetProp(c, "ItemFeatureFieldConfigDatas");
                    sb.AppendLine($"      ItemFeatureFieldConfigDatas (Count={CountOf(fieldCfgs)}):");
                    foreach (var fc in Enumerate(fieldCfgs))
                    {
                        sb.AppendLine($"        -- {fc?.GetType().FullName} --");
                        DumpAllProperties(sb, fc, "          ");
                    }
                }

                Plugin.L.LogInfo(sb.ToString());
            }
            catch (Exception e) { Plugin.L.LogError($"[FridgeProbe] 物品 {id} 详情异常: {e}"); }
        }
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
        sb.AppendLine($"  ---- [{idx}] itemId={item.itemId} [{name}] attrType={attr.GetType().FullName} Ptr=0x{item.Pointer.ToInt64():X} ----");

        try
        {
            var expired = ItemData.IsFoodExpired(item, attr);
            sb.AppendLine($"    IsFoodExpired = {expired}");
        }
        catch (Exception ex) { sb.AppendLine($"    IsFoodExpired 调用异常: {ex.Message}"); }

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

        try
        {
            var props = item.properties;
            if (props != null)
            {
                sb.AppendLine($"    properties (Length={props.Length}):");
                for (int i = 0; i < props.Length; i++)
                {
                    try { sb.AppendLine($"      [{i}] = {props[i]}"); }
                    catch (Exception ex) { sb.AppendLine($"      [{i}] 读取异常 {ex.Message}"); }
                }
            }
            else sb.AppendLine($"    properties = null");
        }
        catch (Exception ex) { sb.AppendLine($"    properties 访问异常: {ex.Message}"); }
    }

    // ---------- 电瓶 dump ----------

    private void DumpBattery(string tag)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[FridgeProbe] ===== 电瓶快照 [{tag}] =====");
        try
        {
            var gc = GameController.instance;
            var cd = gc?.playerCharacter?.characterData;
            var inv = cd?.inventoryData;
            if (inv == null) { sb.AppendLine("  背包未就绪"); Plugin.L.LogInfo(sb.ToString()); return; }

            var list = inv.itemList;
            int n = 0;
            for (int i = 0; i < (list?.Count ?? 0); i++)
            {
                var item = list[i];
                if (item == null || item.itemId != 85) continue;
                n++;
                sb.AppendLine($"  ---- 电瓶[{n}] Ptr=0x{item.Pointer.ToInt64():X} ----");
                try { sb.AppendLine($"    GetBatteryRemainingPower = {ItemFeature_Battery.GetBatteryRemainingPower(item)}"); }
                catch (Exception ex) { sb.AppendLine($"    GetBatteryRemainingPower 异常: {ex.Message}"); }
                try { sb.AppendLine($"    GetBatteryEnergy = {ItemData.GetBatteryEnergy(item)}"); }
                catch (Exception ex) { sb.AppendLine($"    GetBatteryEnergy 异常: {ex.Message}"); }

                foreach (var p in item.GetType().GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (p.GetIndexParameters().Length > 0) continue;
                    object v;
                    try { v = p.GetValue(item); } catch { continue; }
                    if (v == null) continue;
                    var s = v.ToString();
                    if (s.Length > 80) s = s.Substring(0, 80) + "...";
                    sb.AppendLine($"    ItemData.{p.Name} = {s}");
                }
            }
            if (n == 0) sb.AppendLine("  背包中无电瓶(85)");
        }
        catch (Exception e) { sb.AppendLine($"  异常: {e}"); }
        Plugin.L.LogInfo(sb.ToString());
    }

    // ---------- 电池槽设备 dump（小冰箱/手电筒装电池后的槽状态）----------

    private void DumpSlotDevices(string tag)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[FridgeProbe] ===== 电池槽设备快照 [{tag}] =====");
        try
        {
            var gc = GameController.instance;
            var cd = gc?.playerCharacter?.characterData;
            var inv = cd?.inventoryData;
            if (inv == null) { sb.AppendLine("  背包未就绪"); Plugin.L.LogInfo(sb.ToString()); return; }

            var list = inv.itemList;
            // 背包中的设备
            for (int i = 0; i < (list?.Count ?? 0); i++)
            {
                var item = list[i];
                if (item == null) continue;
                if (item.itemId != 900100 && item.itemId != 91 && item.itemId != 93) continue;
                sb.AppendLine($"  ---- [背包] {item.itemId} [{NameOf(item.itemId)}] Ptr=0x{item.Pointer.ToInt64():X} ----");
                DumpSlotInfo(sb, item, "    ");
                try { sb.AppendLine($"    BatteryHavePower = {ItemFeature_BatteryBox.BatteryHavePower(item)}"); }
                catch (Exception ex) { sb.AppendLine($"    BatteryHavePower 异常: {ex.Message}"); }
                try { sb.AppendLine($"    IsSwitchOn = {ItemFeature_BatteryConsuming.IsSwitchOn(item)}"); }
                catch (Exception ex) { sb.AppendLine($"    IsSwitchOn 异常: {ex.Message}"); }
            }

            // 装备栏设备（手电筒/照明棒/夜视仪/能量武器等）
            var eq = cd.characterEquipmentData;
            if (eq == null) { sb.AppendLine("  装备栏 null"); }
            else
            {
                // 照明工具位
                try
                {
                    var lt = eq.lightingToolItemData;
                    if (lt != null)
                    {
                        sb.AppendLine($"  ---- [装备·照明] {lt.itemId} [{NameOf(lt.itemId)}] Ptr=0x{lt.Pointer.ToInt64():X} ----");
                        DumpSlotInfo(sb, lt, "    ");
                        try { sb.AppendLine($"    BatteryHavePower = {ItemFeature_BatteryBox.BatteryHavePower(lt)}"); }
                        catch { }
                        try { sb.AppendLine($"    IsSwitchOn = {ItemFeature_BatteryConsuming.IsSwitchOn(lt)}"); }
                        catch { }
                    }
                    else sb.AppendLine("  [装备·照明] 无");
                }
                catch (Exception ex) { sb.AppendLine($"  照明位异常: {ex.Message}"); }

                // 特殊装备位（夜视仪等）
                try
                {
                    var se = eq.specialEquipmentItemData;
                    if (se != null)
                    {
                        sb.AppendLine($"  ---- [装备·特殊] {se.itemId} [{NameOf(se.itemId)}] Ptr=0x{se.Pointer.ToInt64():X} ----");
                        DumpSlotInfo(sb, se, "    ");
                        try { sb.AppendLine($"    IsSwitchOn = {ItemFeature_BatteryConsuming.IsSwitchOn(se)}"); }
                        catch { }
                    }
                    else sb.AppendLine("  [装备·特殊] 无");
                }
                catch (Exception ex) { sb.AppendLine($"  特殊位异常: {ex.Message}"); }

                // 武器位（能量武器）
                try
                {
                    var weapons = eq.weaponItemDataAry;
                    if (weapons != null)
                    {
                        for (int w = 0; w < weapons.Length; w++)
                        {
                            var wd = weapons[w];
                            if (wd == null) continue;
                            sb.AppendLine($"  ---- [装备·武器{w}] {wd.itemId} [{NameOf(wd.itemId)}] Ptr=0x{wd.Pointer.ToInt64():X} ----");
                            DumpSlotInfo(sb, wd, "    ");
                            try { sb.AppendLine($"    IsSwitchOn = {ItemFeature_BatteryConsuming.IsSwitchOn(wd)}"); }
                            catch { }
                        }
                    }
                    else sb.AppendLine("  [装备·武器] 无");
                }
                catch (Exception ex) { sb.AppendLine($"  武器位异常: {ex.Message}"); }
            }
        }
        catch (Exception e) { sb.AppendLine($"  异常: {e}"); }
        Plugin.L.LogInfo(sb.ToString());
    }

    private static string NameOf(int id)
    {
        try { return ItemManager.instance.GetItemAttrById(id)?.itemName_Runtime ?? "?"; }
        catch { return "?"; }
    }

    private static void DumpSlotInfo(StringBuilder sb, ItemData item, string indent)
    {
        // 反射找 BatterySlotInfo / 相关字段
        foreach (var name in new[] { "BatterySlotInfo", "batterySlotInfo", "batteryInfo" })
        {
            var p = item.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (p != null)
            {
                object v;
                try { v = p.GetValue(item); } catch { continue; }
                if (v == null) { sb.AppendLine($"{indent}{name} = null"); continue; }
                sb.AppendLine($"{indent}{name} = {v.GetType().FullName}");
                DumpAllProperties(sb, v, indent + "  ");
            }
        }
        // wattage（从 attr 的 BatteryConsuming 配置读）
        try
        {
            var attr = ItemManager.instance.GetItemAttrById(item.itemId);
            var cfg = GetProp(attr, "itemFeatureConfigDatas");
            foreach (var c in Enumerate(cfg))
            {
                var ft = GetProp(c, "featureType");
                if (ft != null && ft.ToString() == "BatteryConsuming")
                {
                    var flds = GetProp(c, "ItemFeatureFieldConfigDatas");
                    foreach (var fc in Enumerate(flds))
                    {
                        var n = GetProp(fc, "itemFeatureFieldName");
                        var v = GetProp(fc, "itemFeatureFieldValue");
                        if (n != null && v != null)
                            sb.AppendLine($"{indent}wattage配置: {n} = {v}");
                    }
                }
            }
        }
        catch { }
        // 全部私有字段（找电池槽存储）
        foreach (var f in item.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (f.Name.Contains("attery") || f.Name.Contains("lot"))
            {
                object v;
                try { v = f.GetValue(item); } catch { continue; }
                sb.AppendLine($"{indent}field:{f.Name} = {v}");
            }
        }
        // itemPropertyPairs（电池槽可能存这里）
        try
        {
            var pairs = item.itemPropertyPairs;
            if (pairs != null && pairs.Count > 0)
            {
                sb.AppendLine($"{indent}itemPropertyPairs (Count={pairs.Count}):");
                var e = pairs.GetEnumerator();
                while (e.MoveNext())
                {
                    try
                    {
                        var cur = e.Current;
                        var k = cur?.GetType().GetProperty("key", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(cur);
                        var v = cur?.GetType().GetProperty("value", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(cur);
                        sb.AppendLine($"{indent}  [{k}] = {v}");
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    // ---------- BatteryConsuming 物品扫描 ----------

    private void FindBatteryConsumingItems()
    {
        var sb = new StringBuilder();
        sb.AppendLine("[FridgeProbe] ===== BatteryConsuming 物品扫描 =====");
        try
        {
            var mgr = ItemManager.instance;
            var dic = GetProp(mgr, "itemAttrDic");
            if (dic == null) { sb.AppendLine("  itemAttrDic null"); Plugin.L.LogInfo(sb.ToString()); return; }

            var keys = GetProp(dic, "Keys");
            int found = 0;
            foreach (var k in Enumerate(keys))
            {
                if (k == null) continue;
                int id;
                try { id = Convert.ToInt32(k); } catch { continue; }
                object attr;
                try
                {
                    var tryGet = dic.GetType().GetMethod("TryGetValue", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (tryGet == null) continue;
                    var args = new object[] { k, null };
                    if (!(tryGet.Invoke(dic, args) is bool ok && ok)) continue;
                    attr = args[1];
                }
                catch { continue; }
                if (attr == null) continue;

                var feats = GetProp(attr, "itemFeatures");
                bool isConsuming = false;
                foreach (var fv in Enumerate(feats))
                {
                    if (fv != null && fv.ToString() == "BatteryConsuming") { isConsuming = true; break; }
                }
                if (!isConsuming) continue;

                found++;
                sb.AppendLine($"  --- 物品 {id} [{GetProp(attr, "itemName_Runtime")}] ---");
                sb.AppendLine($"    attrType={attr.GetType().FullName}  itemType={GetProp(attr, "itemType")}");

                var dic2 = GetProp(attr, "itemFeatureDataDic");
                sb.AppendLine($"    itemFeatureDataDic (Count={CountOf(dic2)}):");
                foreach (var kv in EnumerateKeyValue(dic2))
                    sb.AppendLine($"      [{kv.Key}] = {kv.Value} ({kv.Value?.GetType().FullName})");

                var cfg = GetProp(attr, "itemFeatureConfigDatas");
                sb.AppendLine($"    itemFeatureConfigDatas (Count={CountOf(cfg)}):");
                foreach (var c in Enumerate(cfg))
                    sb.AppendLine($"      {c?.GetType().FullName}: {DescribeObject(c)}");

                if (found >= 8) { sb.AppendLine("  ...(截断)"); break; }
            }
            sb.AppendLine($"  共找到 {found}+ 个");
        }
        catch (Exception e) { sb.AppendLine($"  异常: {e}"); }
        Plugin.L.LogInfo(sb.ToString());
    }

    // ---------- 反射辅助 ----------

    private static string DescribeObject(object o)
    {
        if (o == null) return "<null>";
        var sb = new StringBuilder();
        foreach (var name in new[] { "wattage", "batteryModel", "batteryNumber", "durabilityCostPerSecond", "itemId", "itemNumber", "batteryCapacity", "batteryCapacityFloat" })
        {
            var p = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (p != null)
            {
                try { sb.Append($"{name}={p.GetValue(o)} "); } catch { }
            }
        }
        return sb.Length > 0 ? sb.ToString().Trim() : o.ToString();
    }

    private static object GetProp(object obj, string name)
    {
        if (obj == null) return null;
        var t = obj.GetType();
        var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
             ?? t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (p == null) return null;
        try { return p.GetValue(obj); }
        catch { return null; }
    }

    private static int CountOf(object coll)
    {
        if (coll == null) return 0;
        var p = coll.GetType().GetProperty("Count", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (p != null) { try { return Convert.ToInt32(p.GetValue(coll)); } catch { } }
        return -1;
    }

    private static IEnumerable Enumerate(object coll)
    {
        if (coll == null) yield break;
        var t = coll.GetType();
        var get = t.GetMethod("get_Item", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var cnt = CountOf(coll);
        if (get != null && cnt >= 0)
        {
            for (int i = 0; i < cnt; i++)
            {
                object item = null;
                try { item = get.Invoke(coll, new object[] { i }); } catch { }
                yield return item;
            }
            yield break;
        }
        yield break;
    }

    private static IEnumerable<KeyValuePair<string, string>> EnumerateKeyValue(object dict)
    {
        if (dict == null) yield break;
        var t = dict.GetType();

        // 方式1：GetEnumerator（Il2CppSystem 字典）
        var getEnum = t.GetMethod("GetEnumerator", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (getEnum != null)
        {
            object e = null;
            try { e = getEnum.Invoke(dict, null); } catch { }
            var moveNext = e?.GetType().GetMethod("MoveNext", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var currentProp = e?.GetType().GetProperty("Current", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (moveNext != null && currentProp != null)
            {
                while (true)
                {
                    bool has;
                    try { has = (bool)moveNext.Invoke(e, null); }
                    catch { break; }
                    if (!has) break;
                    object cur = null;
                    try { cur = currentProp.GetValue(e); } catch { }
                    if (cur == null) continue;
                    string k = null, v = null;
                    try { k = cur.GetType().GetProperty("Key", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(cur)?.ToString(); } catch { }
                    try { v = cur.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(cur)?.ToString(); } catch { }
                    yield return new KeyValuePair<string, string>(k ?? "?", v ?? "<null>");
                }
                yield break;
            }
        }

        // 方式2：Keys + TryGetValue
        var getMethod = t.GetMethod("TryGetValue", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (getMethod != null)
        {
            var keys = GetProp(dict, "Keys");
            foreach (var k in Enumerate(keys))
            {
                if (k == null) continue;
                string v = null;
                try
                {
                    var args = new object[] { k, null };
                    if (getMethod.Invoke(dict, args) is bool ok && ok) v = args[1]?.ToString();
                }
                catch { }
                yield return new KeyValuePair<string, string>(k.ToString(), v ?? "<null>");
            }
        }
    }

    private static void DumpAllProperties(StringBuilder sb, object o, string indent)
    {
        if (o == null) { sb.AppendLine($"{indent}<null>"); return; }
        foreach (var p in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0) continue;
            object v;
            try { v = p.GetValue(o); }
            catch { continue; }
            var s = v?.ToString();
            if (s != null && s.Length > 120) s = s.Substring(0, 120) + "...";
            sb.AppendLine($"{indent}{p.Name} = {s ?? "<null>"}");
        }
        foreach (var f in o.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            object v;
            try { v = f.GetValue(o); }
            catch { continue; }
            var s = v?.ToString();
            if (s != null && s.Length > 120) s = s.Substring(0, 120) + "...";
            sb.AppendLine($"{indent}field:{f.Name} = {s ?? "<null>"}");
        }
    }
}
