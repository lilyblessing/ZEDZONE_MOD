using System;
using System.Collections.Generic;
using UnityEngine;
using HarmonyLib;
using ZedZoneShared;

namespace TeleportStationPlugin;

/// <summary>
/// P5 电池扣减：每次传送消耗 2 枚满电电池（单枚 5000Wh，已在设计定稿），按序扣减。
/// 仓位：pad 的 ProductionData.inventoryData1，4×4，totalBatterySoltNumber=4（ChargerPadFix 已初始化）。
/// 电量：ItemFeature_Battery 的 charge / maxCharge / wattage 字段（dump 待精确定位，当前用反射兼容多命名）。
/// </summary>
public static class TeleportBatteryManager
{
    private const int BatteryItemId = 86;
    private const float SingleCapacity = 5000f;
    private const float NeedTotal = 10000f;

    public static bool HasEnoughCharge(TerrainObject pad)
    {
        try
        {
            var inv = GetBatteryInventory(pad);
            if (inv == null) return false;
            float sum = GetTotalCharge(inv);
            return sum >= NeedTotal - 0.01f;
        }
        catch { return false; }
    }

    public static bool ConsumeCharge(TerrainObject pad, float amount = NeedTotal)
    {
        try
        {
            var inv = GetBatteryInventory(pad);
            if (inv == null) return false;
            var list = GetBatteryItems(inv);
            if (list == null || list.Count == 0) return false;
            // 按电量降序，优先扣满电的
            list.Sort((a,b) => GetCharge(b).CompareTo(GetCharge(a)));
            float need = amount;
            foreach (var item in list)
            {
                if (need <= 0.001f) break;
                float charge = GetCharge(item);
                if (charge <= 0.001f) continue;
                float take = Math.Min(charge, need);
                SetCharge(item, charge - take);
                need -= take;
                Plugin.L?.LogInfo($"[TS][Battery] 扣减 id={GetItemId(item)} {charge:F0}->{charge-take:F0} 余需 {need:F0}");
            }
            return need <= 0.001f;
        }
        catch (Exception e) { Plugin.L?.LogWarning($"[TS][Battery] 扣减异常: {e.Message}"); return false; }
    }

    public static float GetTotalCharge(InventoryData inv)
    {
        float sum = 0f;
        var list = GetBatteryItems(inv);
        if (list == null) return 0f;
        foreach (var it in list) sum += GetCharge(it);
        return sum;
    }

    private static InventoryData GetBatteryInventory(TerrainObject pad)
    {
        try
        {
            // pad -> TerrainObject_Production -> objectData -> productionData -> inventoryData1
            var prod = GetProduction(pad);
            if (prod == null) return null;
            var pd = Reflect.Get(prod, "objectData") ?? Reflect.Get(pad, "objectData") ?? Reflect.Get(pad, "terrainObjectData");
            // 另一路径：pad 本身是 Production，取 ProductionData
            object productionData = null;
            if (pd != null) productionData = Reflect.Get(pd, "productionData");
            if (productionData == null)
            {
                // 直接从 pad 的 Production 组件取
                var g = FindProductionComponent(pad);
                if (g != null)
                {
                    var od = Reflect.Get(g, "objectData");
                    if (od != null) productionData = Reflect.Get(od, "productionData");
                }
            }
            if (productionData == null) return null;
            var inv = Reflect.Get(productionData, "inventoryData1") as InventoryData;
            if (inv == null) inv = Reflect.Get(productionData, "inventoryData") as InventoryData;
            return inv;
        }
        catch { return null; }
    }

    private static Component FindProductionComponent(TerrainObject pad)
    {
        try
        {
            var tr = pad.transform;
            int d=0;
            while (tr!=null && d++<8)
            {
                foreach (var c in tr.GetComponents<Component>()) if (c!=null && c.GetType().Name.Contains("Production")) return c;
                tr = tr.parent;
            }
        } catch {}
        return null;
    }

    private static object GetProduction(TerrainObject pad)
    {
        try { return FindProductionComponent(pad); } catch { return null; }
    }

    private static List<object> GetBatteryItems(InventoryData inv)
    {
        var result = new List<object>();
        try
        {
            // 尝试多种取列表方式
            var list = Reflect.Get(inv, "itemList") as Il2CppSystem.Collections.Generic.List<ItemData>;
            if (list != null)
            {
                for (int i=0;i<list.Count;i++) { var it=list[i]; if(it!=null && GetItemId(it)==BatteryItemId) result.Add(it); }
                return result;
            }
            var list2 = Reflect.Get(inv, "items") as System.Collections.Generic.List<ItemData>;
            if (list2 != null) { foreach(var it in list2) if(GetItemId(it)==BatteryItemId) result.Add(it); return result; }
            // 回退：用 GetItemListByFeature
            var m = inv.GetType().GetMethod("GetItemListByFeature");
            if (m!=null)
            {
                var ft = GetBatteryFeatureType();
                if (ft != null)
                {
                    var r = m.Invoke(inv, new object[]{ ft }) as Il2CppSystem.Collections.Generic.List<ItemData>;
                    if (r!=null) for(int i=0;i<r.Count;i++) result.Add(r[i]);
                }
            }
        } catch {}
        return result;
    }

    private static object GetBatteryFeatureType()
    {
        try
        {
            var t = AccessTools.TypeByName("ItemFeatureType");
            if (t!=null) return Enum.Parse(t, "Battery");
        } catch {}
        try
        {
            var t2 = AccessTools.TypeByName("ItemFeatureType");
            if (t2!=null) foreach(var v in Enum.GetValues(t2)) if(v.ToString().Contains("Battery")) return v;
        } catch {}
        return null;
    }

    private static int GetItemId(object itemData)
    {
        try { var o=Reflect.Get(itemData,"itemId"); if(o!=null) return Convert.ToInt32(o); } catch {}
        try { var o=Reflect.Get(itemData,"id"); if(o!=null) return Convert.ToInt32(o); } catch {}
        return -1;
    }

    private static float GetCharge(object itemData)
    {
        // ItemFeature_Battery 的 charge 字段可能在 itemData 或其 feature
        try
        {
            var v = Reflect.Get(itemData, "currentCharge") ?? Reflect.Get(itemData, "charge") ?? Reflect.Get(itemData, "batteryCharge") ?? Reflect.Get(itemData, "energy");
            if (v!=null) return Convert.ToSingle(v);
            // 尝试从 feature
            var feat = GetBatteryFeature(itemData);
            if (feat!=null)
            {
                var c2 = Reflect.Get(feat, "currentCharge") ?? Reflect.Get(feat, "charge") ?? Reflect.Get(feat, "batteryCharge");
                if (c2!=null) return Convert.ToSingle(c2);
            }
            // 回退：durability 视效
            var d = Reflect.Get(itemData, "durability") ?? Reflect.Get(itemData, "currentDurability");
            if (d!=null) return Convert.ToSingle(d) * SingleCapacity; // 粗略
        } catch {}
        return 0f;
    }

    private static void SetCharge(object itemData, float newCharge)
    {
        try
        {
            if (Reflect.Get(itemData, "currentCharge")!=null) { Reflect.Set(itemData, "currentCharge", newCharge); return; }
            if (Reflect.Get(itemData, "charge")!=null) { Reflect.Set(itemData, "charge", newCharge); return; }
            if (Reflect.Get(itemData, "batteryCharge")!=null) { Reflect.Set(itemData, "batteryCharge", newCharge); return; }
            var feat = GetBatteryFeature(itemData);
            if (feat!=null)
            {
                if (Reflect.Get(feat, "currentCharge")!=null) { Reflect.Set(feat, "currentCharge", newCharge); return; }
                if (Reflect.Get(feat, "charge")!=null) { Reflect.Set(feat, "charge", newCharge); return; }
            }
            // 回退 durability
            if (Reflect.Get(itemData, "durability")!=null) Reflect.Set(itemData, "durability", newCharge / SingleCapacity);
        } catch {}
    }

    private static object GetBatteryFeature(object itemData)
    {
        try
        {
            var m = itemData.GetType().GetMethod("GetFeature");
            if (m!=null)
            {
                var t = AccessTools.TypeByName("ItemFeatureType");
                var ft = GetBatteryFeatureType();
                if (ft!=null) return m.Invoke(itemData, new object[]{ ft });
            }
            var f = Reflect.Get(itemData, "batteryFeature") ?? Reflect.Get(itemData, "itemFeature_Battery");
            return f;
        } catch { return null; }
    }
}
