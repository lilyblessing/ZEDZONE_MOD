using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using TeleportStationPlugin; // PadLayerPin 组件检测（编译期引用 TS 插件）
using UnityEngine;

namespace PadSortProbe;

/// <summary>
/// PadSortProbe v0.1.0 —— 圆盘排序取证（只读）。每 2s 采集一次：
///   a. 场景中「TS_TeleportPad」实例的所有 SpriteRenderer：sortingLayerName/ID、sortingOrder、世界 y；
///   b. 玩家角色（GameController.playerCharacter 树）的 SpriteRenderer：层/order/y；
///   c. 载具（drivingVehicle 树，若有）的 SR 同项。
/// 目的：定位 v0.6.40 PadLayerPin 是否生效、玩家实际所在层、圆盘与玩家的排序关系（y-sort 或层冲突）。
/// 日志关键字：[PSP]，仅在存在圆盘实例时打印（节流 2s）。
/// </summary>
[BepInPlugin("com.zedzone.tool.padsortprobe", "PadSortProbe", "0.1.8")]
public class Plugin : BasePlugin
{
    public static ManualLogSource L;

    public override void Load()
    {
        L = Log;
        AddComponent<SortProbe>();
        L.LogInfo("[PSP] PadSortProbe v0.1.8 已加载（MapController 建筑登记列表 + 字典直取）");
    }
}

public class SortProbe : MonoBehaviour
{
    private float _next = 3f;
    private static readonly HashSet<string> _seen = new();

    private void Update()
    {
        _next -= Time.unscaledDeltaTime;
        if (_next > 0f) return;
        _next = 6f;
        try { Collect(); }
        catch (Exception e) { Plugin.L.LogWarning($"[PSP] 采集异常: {e.Message.Split('\n')[0]}"); }
    }

    private static void Collect()
    {
        // 玩家 + 载具（无条件采集，无论圆盘是否可见）
        try
        {
            var pc = typeof(GameController).GetProperty("instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.GetValue(null);
            if (pc != null)
            {
                var player = Reflect.Get(pc, "playerCharacter") as Component;
                if (player != null)
                {
                    var psrs = player.GetComponentsInChildren<SpriteRenderer>(true);
                    Plugin.L.LogInfo($"[PSP] 玩家 '{player.name}' 位置=({player.transform.position.x:F1},{player.transform.position.y:F1}) SR数={psrs.Length} hideFlags={player.gameObject.hideFlags}");
                    foreach (var sr in psrs)
                    {
                        if (sr == null) continue;
                        Plugin.L.LogInfo($"[PSP]   玩家SR '{sr.name}' layer={sr.sortingLayerName}({sr.sortingLayerID}) order={sr.sortingOrder}");
                    }
                    try
                    {
                        var vehicle = Reflect.Get(player, "drivingVehicle") as Component;
                        if (vehicle != null)
                        {
                            var vsrs = vehicle.GetComponentsInChildren<SpriteRenderer>(true);
                            Plugin.L.LogInfo($"[PSP] 载具 '{vehicle.name}' 位置=({vehicle.transform.position.x:F1},{vehicle.transform.position.y:F1}) SR数={vsrs.Length}");
                            foreach (var sr in vsrs)
                            {
                                if (sr == null) continue;
                                Plugin.L.LogInfo($"[PSP]   车辆SR '{sr.name}' layer={sr.sortingLayerName}({sr.sortingLayerID}) order={sr.sortingOrder}");
                            }
                        }
                    }
                    catch { }
                }
                else Plugin.L.LogInfo("[PSP] playerCharacter=null");
            }
        }
        catch (Exception e) { Plugin.L.LogWarning($"[PSP] 玩家采集异常: {e.Message.Split('\n')[0]}"); }

        // MapController/GameController 建筑登记列表（反射字段，不受 IL2CPP stripping 影响）——找 TerrainObject 列表并 dump 每建筑 SR 层/贴图
        try
        {
            foreach (var host in new object[] { GetFieldOrProp("MapController", "instance"), GetFieldOrProp("GameController", "instance") })
            {
                if (host == null) continue;
                foreach (var p in host.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                {
                    try { DumpTerrainList(p.GetValue(host), p.Name); } catch { }
                }
                foreach (var f in host.GetType().GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                {
                    if (f.FieldType.Name.Contains("List") || f.FieldType.Name.Contains("Array"))
                    {
                        try { DumpTerrainList(f.GetValue(host), f.Name); } catch { }
                    }
                }
            }
        }
        catch (Exception e6) { Plugin.L.LogWarning($"[PSP] 列表遍历异常: {e6.Message.Split('\n')[0]}"); }

        // 反射直取 GameController 含 900102 键的字典 → 克隆状态（真实存在性铁证）
        try
        {
            var gcObj = GetFieldOrProp("GameController", "instance");
            if (gcObj != null)
            {
                foreach (var p in gcObj.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                {
                    try
                    {
                        var v = p.GetValue(gcObj);
                        if (v == null) continue;
                        string vt = v.GetType().Name;
                        if (!vt.Contains("Dictionary")) continue;
                        bool has = (bool)v.GetType().GetMethod("ContainsKey").Invoke(v, new object[] { 900102 });
                        if (!has) continue;
                        object val = v.GetType().GetProperty("Item").GetValue(v, new object[] { 900102 });
                        bool isGo = val is GameObject;
                        Plugin.L.LogInfo($"[PSP] ★字典 {vt} [900102] → {(val == null ? "null" : val.GetType().FullName)} isGameObject={isGo}");
                        if (isGo)
                        {
                            var go = (GameObject)val;
                            Plugin.L.LogInfo($"[PSP]   克隆 '{go.name}' pos=({go.transform.position.x:F1},{go.transform.position.y:F1}) active={go.activeInHierarchy} hideFlags={go.hideFlags}");
                            foreach (var sr in go.GetComponentsInChildren<SpriteRenderer>(true))
                            {
                                if (sr == null) continue;
                                string sn = "";
                                try { sn = sr.sprite == null ? "<null>" : (sr.sprite.name ?? "<unnamed>"); } catch { sn = "<读异常>"; }
                                Plugin.L.LogInfo($"[PSP]     克隆SR '{sr.name}' sprite={sn} layer={sr.sortingLayerName}({sr.sortingLayerID}) order={sr.sortingOrder}");
                            }
                        }
                    }
                    catch { }
                }
            }
            else Plugin.L.LogInfo("[PSP] GC.instance=null（未进存档）");
        }
        catch (Exception e4) { Plugin.L.LogWarning($"[PSP] 字典直取异常: {e4.Message.Split('\n')[0]}"); }
    }

    private static object GetFieldOrProp(string typeName, string member)
    {
        try
        {
            var t = typeof(GameController).Assembly.GetType(typeName);
            if (t == null) return null;
            var prop = t.GetProperty(member, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            if (prop != null) return prop.GetValue(null);
            var fld = t.GetField(member, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            return fld == null ? null : fld.GetValue(null);
        }
        catch { return null; }
    }

    private static void DumpTerrainList(object listObj, string ownerName)
    {
        if (listObj == null) return;
        string lt = listObj.GetType().FullName ?? "?";
        // 只要元素类型为 TerrainObject 相关或对象列表较大的——先看元素通用方法
        if (!lt.Contains("Collections.Generic.IList") && !lt.Contains("List`1")) return;
        var cntP = listObj.GetType().GetProperty("Count");
        if (cntP == null) return;
        int cnt = Convert.ToInt32(cntP.GetValue(listObj));
        if (cnt == 0) return;
        Plugin.L.LogInfo($"[PSP] 列表 {lt} [{ownerName}] count={cnt}");
        var itemP = listObj.GetType().GetProperty("Item");
        if (itemP == null) return;
        for (int i = 0; i < cnt; i++)
        {
            try
            {
                var el = itemP.GetValue(listObj, new object[] { i });
                if (el == null) continue;
                if (el is Component comp && comp != null && comp.gameObject != null)
                {
                    var t = comp.transform;
                    var r = t;
                    while (r.parent != null) r = r.parent;
                    string key2 = "LIST|" + ownerName + "|" + r.name + "|" + comp.GetType().Name;
                    if (_seen.Contains(key2)) continue;
                    _seen.Add(key2);
                    var srs = r.GetComponentsInChildren<SpriteRenderer>(true);
                    string srInfo = "";
                    foreach (var sr in srs)
                    {
                        if (sr == null) continue;
                        string sn3 = "";
                        try { sn3 = sr.sprite == null ? "<null>" : (sr.sprite.name ?? ""); } catch { }
                        srInfo += $" [{sr.name}:{sr.sortingLayerName}/{sr.sortingOrder}/{sn3}]";
                        if (srInfo.Length > 300) break;
                    }
                    Plugin.L.LogInfo($"[PSP]   建筑[{i}] root='{r.name}' 组件={comp.GetType().Name} SR:{srInfo}");
                }
            }
            catch { }
        }
    }
}