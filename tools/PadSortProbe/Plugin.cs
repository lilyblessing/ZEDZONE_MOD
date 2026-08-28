using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
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
[BepInPlugin("com.zedzone.tool.padsortprobe", "PadSortProbe", "0.1.1")]
public class Plugin : BasePlugin
{
    public static ManualLogSource L;

    public override void Load()
    {
        L = Log;
        AddComponent<SortProbe>();
        L.LogInfo("[PSP] PadSortProbe v0.1.1 已加载（Resources 全量采集含隐藏对象）");
    }
}

public class SortProbe : MonoBehaviour
{
    private float _next = 3f;

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

        // 圆盘：Resources.FindObjectsOfTypeAll（含隐藏对象——克隆 prefab 的 HideAndDontSave 可能被实例继承，FindObjectsOfType 看不到）
        bool found = false;
        try
        {
            foreach (var sr in Resources.FindObjectsOfTypeAll<SpriteRenderer>())
            {
                if (sr == null) continue;
                bool isPad = false;
                var cur = sr.transform;
                int d = 0;
                while (cur != null && d++ < 16)
                {
                    if (cur.name != null && cur.name.Contains("TS_TeleportPad")) { isPad = true; break; }
                    cur = cur.parent;
                }
                if (!isPad) continue;
                found = true;
                Plugin.L.LogInfo($"[PSP] 圆盘SR '{sr.transform.name}' layer={sr.sortingLayerName}({sr.sortingLayerID}) order={sr.sortingOrder} pos=({sr.transform.position.x:F1},{sr.transform.position.y:F1}) activeInHierarchy={sr.gameObject.activeInHierarchy} hideFlags={sr.gameObject.hideFlags}");
            }
        }
        catch (Exception e2) { Plugin.L.LogWarning($"[PSP] 圆盘采集异常: {e2.Message.Split('\n')[0]}"); }
        Plugin.L.LogInfo(found ? "[PSP] === 圆盘采集完成 ===" : "[PSP] 圆盘未找到（Resources 全量亦无，排查克隆/实例化）");
    }
}