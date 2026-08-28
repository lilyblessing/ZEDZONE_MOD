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
[BepInPlugin("com.zedzone.tool.padsortprobe", "PadSortProbe", "0.1.7")]
public class Plugin : BasePlugin
{
    public static ManualLogSource L;

    public override void Load()
    {
        L = Log;
        AddComponent<SortProbe>();
        L.LogInfo("[PSP] PadSortProbe v0.1.7 已加载（场景根遍历：实例 Pin 检测 + SR dump）");
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

        // 场景根遍历：Skips 枚举盲区（HideAndDontSave 不被 FindObjectsOfTypeAll 返回）——
        // 遍历所有场景（含 DontDestroyOnLoad）的根对象 → PadLayerPin 组件（实例若带组件必中）→ dump SR 层/贴图
        try
        {
            bool any = false;
            foreach (var scene in UnityEngine.SceneManagement.SceneManager.GetAllScenes())
            {
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    if (root == null) continue;
                    PadLayerPin[] pins = null;
                    try { pins = root.GetComponentsInChildren<PadLayerPin>(true); } catch { }
                    if (pins != null && pins.Length > 0)
                    {
                        foreach (var pin in pins)
                        {
                            if (pin == null) continue;
                            var t = pin.transform;
                            var r = t;
                            while (r.parent != null) r = r.parent;
                            any = true;
                            Plugin.L.LogInfo($"[PSP] ★实例(PadLayerPin) root='{r.name}' 场景='{scene.name}' pos=({t.position.x:F1},{t.position.y:F1}) active={t.gameObject.activeInHierarchy}");
                            foreach (var sr in t.GetComponentsInChildren<SpriteRenderer>(true))
                            {
                                if (sr == null) continue;
                                string sn = "";
                                try { sn = sr.sprite == null ? "<null>" : (sr.sprite.name ?? "<unnamed>"); } catch { sn = "<读异常>"; }
                                Plugin.L.LogInfo($"[PSP]     SR '{sr.name}' sprite={sn} layer={sr.sortingLayerName}({sr.sortingLayerID}) order={sr.sortingOrder} pos=({sr.transform.position.x:F1},{sr.transform.position.y:F1})");
                            }
                        }
                    }
                    // 名字兜底：root 名含 900102/TS_/Teleport 的对象（可能无 Pin）
                    string rn = root.name ?? "";
                    if (rn.Contains("900102") || rn.Contains("TS_") || rn.Contains("Teleport"))
                    {
                        any = true;
                        Plugin.L.LogInfo($"[PSP] 场景根对象 '{rn}'（场景 '{scene.name}'，有名字特征）");
                        foreach (var sr in root.GetComponentsInChildren<SpriteRenderer>(true))
                        {
                            if (sr == null) continue;
                            string sn2 = "";
                            try { sn2 = sr.sprite == null ? "<null>" : (sr.sprite.name ?? ""); } catch { }
                            Plugin.L.LogInfo($"[PSP]     SR '{sr.name}' sprite={sn2} layer={sr.sortingLayerName}({sr.sortingLayerID}) order={sr.sortingOrder}");
                        }
                    }
                }
            }
            if (!any) Plugin.L.LogInfo("[PSP] 场景根遍历：未找到 PadLayerPin 实例（克隆组件的实例复制可能失败）");
        }
        catch (Exception e5) { Plugin.L.LogWarning($"[PSP] 场景遍历异常: {e5.Message.Split('\n')[0]}"); }
    }
}