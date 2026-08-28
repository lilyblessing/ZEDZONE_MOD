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
[BepInPlugin("com.zedzone.tool.padsortprobe", "PadSortProbe", "0.1.5")]
public class Plugin : BasePlugin
{
    public static ManualLogSource L;

    public override void Load()
    {
        L = Log;
        AddComponent<SortProbe>();
        L.LogInfo("[PSP] PadSortProbe v0.1.5 已加载（特征必杀扫描：贴图 ID + 对象名 + 组件）");
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
        _next = 2f;
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

        // 必杀：全场景无条件找 ①实体贴图唯一标识的 SR ②root 名含 900102/TS_/Teleport 的对象 ③组件清单
        try
        {
            bool any = false;
            // ① 贴图搜索
            foreach (var sr in Resources.FindObjectsOfTypeAll<SpriteRenderer>())
            {
                if (sr == null) continue;
                string sname = "";
                try { sname = sr.sprite == null ? "" : (sr.sprite.name ?? ""); } catch { }
                if (sname.Contains("TeleportPad") || sname.Contains("TeleportConsole") || sname.Contains("Biomass") || sname.Contains("Teleport\u56de"))
                {
                    var pos = sr.transform.position;
                    string root = "?";
                    try { var r = sr.transform; while (r.parent != null) r = r.parent; root = r.name ?? "?"; } catch { }
                    string k = "SPR|" + sname + "|" + root + "|" + sr.sortingLayerName + "|" + sr.sortingOrder;
                    if (_seen.Contains(k)) continue;
                    _seen.Add(k);
                    any = true;
                    Plugin.L.LogInfo($"[PSP] ★贴图SR sprite='{sname}' root='{root}' layer={sr.sortingLayerName}({sr.sortingLayerID}) order={sr.sortingOrder} pos=({pos.x:F1},{pos.y:F1}) active={sr.gameObject.activeInHierarchy}");
                }
            }
            // ② 对象名搜索（name 含特征）
            foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (t == null || t.name == null) continue;
                string n = t.name;
                if (!n.Contains("900102") && !n.Contains("TS_") && !n.Contains("Teleport") && !n.Contains("传输")) continue;
                string k = "OBJ|" + n;
                if (_seen.Contains(k)) continue;
                _seen.Add(k);
                any = true;
                var comps = "";
                try
                {
                    foreach (var c in t.GetComponents<Component>())
                    {
                        if (c == null) continue;
                        comps += c.GetType().Name + ",";
                        if (comps.Length > 200) break;
                    }
                }
                catch { }
                Plugin.L.LogInfo($"[PSP] ★对象 '{n}' pos=({t.position.x:F1},{t.position.y:F1}) active={t.gameObject.activeInHierarchy} 组件=[{comps}]");
            }
            if (any) Plugin.L.LogInfo("[PSP] === 特征扫描命中 ===");
            else Plugin.L.LogInfo("[PSP] 特征扫描零命中（无 TeleportPad_Body 贴图 SR/无 900102/TS_/Teleport 对象名）");
        }
        catch (Exception e3) { Plugin.L.LogWarning($"[PSP] 特征扫描异常: {e3.Message.Split('\n')[0]}"); }
    }
}