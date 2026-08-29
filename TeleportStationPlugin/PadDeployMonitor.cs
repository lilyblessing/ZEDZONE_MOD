using System;
using System.Collections.Generic;
using UnityEngine;

namespace TeleportStationPlugin;

/// <summary>
/// v0.7.1：圆盘放置物渲染监控（DeployableItem.ActiveDeployableItems 遍历——游戏原生放置物全局列表）。
/// 问题（v0.7.0 实测）：通用放置物生成对象 = 尺寸小（游戏默认 ppu）+ 被游戏 y-sort 抬升 order（盖玩家）。
/// 修复（睡袋同款机制：Character 层 + 固定负 order）：
///   - 首次发现（itemId=900110）：主 SR 贴图替换为 ppu 自适应 Sprite（texH/7 → 世界 ≈9.8×7），层=Character，order=-5，零件 SR 禁用；
///   - 之后每帧钉 order=-5（防游戏 y-sort 抬升——放置物数量少，开销可忽略）。
/// </summary>
public class PadDeployMonitor : MonoBehaviour
{
    private static readonly HashSet<long> FixedPtrs = new();
    private static readonly HashSet<long> PinnedPtrs = new();

    private void Update()
    {
        try
        {
            var list = DeployableItem.ActiveDeployableItems;
            if (list == null || PadDeployable.ItemId < 0) return;
            for (int i = 0; i < list.Count; i++)
            {
                var d = list[i];
                if (d == null) continue;
                int itemId = -1;
                try
                {
                    var attr = d.itemAttr;
                    if (attr != null) itemId = Convert.ToInt32(Reflect.Get(attr, "itemId"));
                }
                catch { }
                if (itemId != PadDeployable.ItemId) continue;

                long ptr = 0;
                try { ptr = (long)d.Pointer; } catch { ptr = d.GetHashCode(); }

                if (!FixedPtrs.Contains(ptr))
                {
                    FixedPtrs.Add(ptr);
                    try { FixItem(d); } catch (Exception e) { Plugin.L.LogWarning($"[TS] 圆盘放置物修正异常: {e.Message.Split('\n')[0]}"); }
                    Plugin.L.LogInfo($"[TS] 圆盘放置物已修正: ptr={ptr} itemId={itemId}（贴图/层/order）");
                }
                else if (PinnedPtrs.Contains(ptr))
                {
                    PinItem(d); // 每帧钉低 order（防 y-sort 抬升，睡袋同款）
                }
            }
        }
        catch { } // 静默低频（ActiveDeployableItems 遍历不应中断主循环）
    }

    private static void FixItem(DeployableItem d)
    {
        var srs = d.GetComponentsInChildren<SpriteRenderer>(true);
        Sprite body = null;
        if (SpriteInjector.Cache.TryGetValue(900102, out var icon) && icon != null && icon.texture != null)
        {
            var tex = icon.texture;
            body = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), tex.height / 7.0f);
            body.name = "TeleportPad_Deploy_Body";
        }
        bool mainDone = false;
        foreach (var sr in srs)
        {
            if (sr == null) continue;
            string sn = sr.name ?? "";
            sr.sortingLayerName = "Character";
            sr.sortingOrder = -5;
            if (!mainDone && body != null && (sn == "Sprite" || sn.StartsWith("Sprite") || sr.sprite == null))
            {
                sr.sprite = body; // 主贴图替换（ppu 自适应 → 世界 ≈9.8×7）
                mainDone = true;
                continue;
            }
            if (sn.Contains("Cylinder") || sn.Contains("Parts") || sn.Contains("Fire"))
                sr.enabled = false;
        }
        if (mainDone) PinnedPtrs.Add(PtrOf(d));
    }

    private static void PinItem(DeployableItem d)
    {
        try
        {
            var srs = d.GetComponentsInChildren<SpriteRenderer>(true);
            foreach (var sr in srs)
            {
                if (sr == null) continue;
                sr.sortingOrder = -5; // 每帧低 order：玩家 body order≥0 恒在其上
            }
        }
        catch { }
    }

    private static long PtrOf(DeployableItem d)
    {
        try { return (long)d.Pointer; } catch { return d.GetHashCode(); }
    }
}