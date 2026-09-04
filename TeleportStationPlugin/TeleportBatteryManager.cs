using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using HarmonyLib;
using ZedZoneShared;

namespace TeleportStationPlugin;

/// <summary>
/// P5 电池扣减：每次传送消耗 2 枚满电电池（单枚 5000Wh，已在设计定稿），按序扣减。
/// 仓位：pad 的 ProductionData.inventoryData1，8×8，totalBatterySoltNumber=4（ChargerPadFix 已初始化）。
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
            // 按电量降序，优先扣满电的（P1-4：先物化电量表，比较器只查表不再调 GetCharge；表取值走缓存 MethodInfo 的 GetCharge）
            float need = amount;
            var snapshot = new System.Collections.Generic.Dictionary<object,float>();
            foreach (var it in list) snapshot[it] = GetCharge(it);
            list.Sort((a,b) =>
            {
                float ca = 0f, cb = 0f;
                try { if (!snapshot.TryGetValue(a, out ca)) ca = GetCharge(a); } catch { }
                try { if (!snapshot.TryGetValue(b, out cb)) cb = GetCharge(b); } catch { }
                return cb.CompareTo(ca);
            });
            foreach (var item in list)
            {
                if (need <= 0.001f) break;
                float charge;
                try { if (!snapshot.TryGetValue(item, out charge)) charge = GetCharge(item); } catch { charge = GetCharge(item); }
                if (charge <= 0.001f) continue;
                float take = Math.Min(charge, need);
                SetCharge(item, charge - take);
                need -= take;
                Plugin.L?.LogInfo($"[TS][Battery] 扣减 id={GetItemId(item)} {charge:F0}->{charge-take:F0} 余需 {need:F0}");
            }
            if (need > 0.001f)
            {
                // 回滚
                foreach (var kv in snapshot) SetCharge(kv.Key, kv.Value);
                Plugin.L?.LogWarning($"[TS][Battery] 扣减失败回滚 需 {amount:F0} 余 {need:F0}");
                return false;
            }
            return true;
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

    public static InventoryData GetBatteryInventory(TerrainObject pad)
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

    private static Type _cachedFeatureType = null;
    private static object _cachedBatteryFeature = null;
    private static bool _featureInit = false;
    private static void EnsureFeatureCache()
    {
        if (_featureInit) return;
        _featureInit = true;
        try { _cachedFeatureType = AccessTools.TypeByName("ItemFeatureType"); } catch {}
        if (_cachedFeatureType != null)
        {
            try { _cachedBatteryFeature = Enum.Parse(_cachedFeatureType, "Battery"); return; } catch {}
            try { foreach(var v in Enum.GetValues(_cachedFeatureType)) if(v.ToString().Contains("Battery")) { _cachedBatteryFeature = v; return; } } catch {}
        }
    }
    private static object GetBatteryFeatureType()
    {
        EnsureFeatureCache();
        return _cachedBatteryFeature;
    }

    private static int GetItemId(object itemData)
    {
        try { var o=Reflect.Get(itemData,"itemId"); if(o!=null) return Convert.ToInt32(o); } catch {}
        try { var o=Reflect.Get(itemData,"id"); if(o!=null) return Convert.ToInt32(o); } catch {}
        return -1;
    }

    // P1-4: MethodInfo 缓存（懒初始化一次，失败置 null 则调用处回退现查，保证永不抛）
    private static MethodInfo _mGetCharge = null;
    private static bool _mGetChargeInit = false;
    private static MethodInfo _mSetCharge = null;
    private static bool _mSetChargeInit = false;
    private static MethodInfo CachedGetChargeMethod()
    {
        if (_mGetChargeInit) return _mGetCharge;
        _mGetChargeInit = true;
        try { _mGetCharge = typeof(ItemFeature_Battery).GetMethod("GetBatteryRemainingPower", BindingFlags.Public | BindingFlags.Static); } catch { _mGetCharge = null; }
        return _mGetCharge;
    }
    private static MethodInfo CachedSetChargeMethod()
    {
        if (_mSetChargeInit) return _mSetCharge;
        _mSetChargeInit = true;
        try { _mSetCharge = typeof(ItemData).GetMethod("SetProperty", new Type[]{ typeof(string), typeof(string) }); } catch { _mSetCharge = null; }
        return _mSetCharge;
    }

    private static float GetCharge(object itemData)
    {
        try
        {
            // 原生 API：ItemFeature_Battery.GetBatteryRemainingPower（P1-4：缓存 MethodInfo，null 回退现查）
            var m = CachedGetChargeMethod();
            if (m == null) { try { m = typeof(ItemFeature_Battery).GetMethod("GetBatteryRemainingPower", BindingFlags.Public | BindingFlags.Static); } catch { m = null; } }
            if (m != null)
            {
                var v = m.Invoke(null, new object[]{ (ItemData)itemData });
                if (v != null) return Convert.ToSingle(v);
            }
        } catch {}
        try
        {
            var v = Reflect.Get(itemData, "currentCharge") ?? Reflect.Get(itemData, "charge") ?? Reflect.Get(itemData, "batteryCharge");
            if (v!=null) return Convert.ToSingle(v);
        } catch {}
        return 0f;
    }

    private static void SetCharge(object itemData, float newCharge)
    {
        newCharge = Math.Max(0f, newCharge);
        string newStr = newCharge.ToString("F2");
        try
        {
            // 原生正确路径：直接写 itemPropertyPairs["RemainingBattery"]，ChargeBattery 负值无效（if wh<=0 return 0）（P1-4：缓存 MethodInfo，null 回退现查）
            var m = CachedSetChargeMethod();
            if (m == null) { try { m = typeof(ItemData).GetMethod("SetProperty", new Type[]{ typeof(string), typeof(string) }); } catch { m = null; } }
            if (m != null)
            {
                m.Invoke(itemData, new object[]{ "RemainingBattery", newStr });
                return;
            }
        } catch {}
        try
        {
            // 反射兜底：直接操纵 itemPropertyPairs
            var list = Reflect.Get(itemData, "itemPropertyPairs") as System.Collections.Generic.List<KeyValueDataPair>;
            if (list == null)
            {
                var ilist = Reflect.Get(itemData, "itemPropertyPairs") as Il2CppSystem.Collections.Generic.List<KeyValueDataPair>;
                if (ilist != null)
                {
                    bool found=false;
                    for(int i=0;i<ilist.Count;i++) if(ilist[i].key=="RemainingBattery"){ var kv=ilist[i]; kv.value=newStr; ilist[i]=kv; found=true; break; }
                    if(!found) ilist.Add(new KeyValueDataPair{ key="RemainingBattery", value=newStr });
                    return;
                }
            }
            if (list != null)
            {
                bool found=false;
                for(int i=0;i<list.Count;i++) if(list[i].key=="RemainingBattery"){ var kv=list[i]; kv.value=newStr; list[i]=kv; found=true; break; }
                if(!found) list.Add(new KeyValueDataPair{ key="RemainingBattery", value=newStr });
                return;
            }
        } catch {}
        try
        {
            if (Reflect.Get(itemData, "currentCharge")!=null) { Reflect.Set(itemData, "currentCharge", newCharge); return; }
        } catch {}
    }

    private static object GetBatteryFeature(object itemData)
    {
        try
        {
            var m = itemData.GetType().GetMethod("GetFeature");
            if (m!=null)
            {
                EnsureFeatureCache();
                var ft = GetBatteryFeatureType();
                if (ft!=null) return m.Invoke(itemData, new object[]{ ft });
            }
            var f = Reflect.Get(itemData, "batteryFeature") ?? Reflect.Get(itemData, "itemFeature_Battery");
            return f;
        } catch { return null; }
    }
}
