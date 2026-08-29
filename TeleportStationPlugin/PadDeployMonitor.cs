using System;
using System.Collections.Generic;
using UnityEngine;

namespace TeleportStationPlugin;

/// <summary>
/// v0.7.1：圆盘放置物渲染监控（DeployableItem.ActiveDeployableItems 遍历——游戏原生放置物全局列表）。
/// v0.7.2：①贴图也每帧钉（游戏每帧重设放置物 SR.sprite，只写一次无效）→ 大小恒 9.8×7；
///        ②未见过放置物一次性 dump（root 名/组件类型/SR 概览）——摸清"放置物→prefab 绑定"（睡袋 vs 通用对比，源头化前置取证）。
/// 睡袋同款机制：Character 层 + 固定负 order；sprite ppu 自适应（texH/7 → 世界 ≈9.8×7）。
/// </summary>
public class PadDeployMonitor : MonoBehaviour
{
    private static Sprite _bodySprite;
    private static bool _bodyInit;

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

                long ptr = 0;
                try { ptr = (long)d.Pointer; } catch { ptr = d.GetHashCode(); }

                // v0.7.3：一次性分类缓存——非目标放置物不再每帧反射（每帧仅 O(1) 跳过）
                if (_classified.Contains(ptr))
                {
                    if (_pads.Contains(ptr)) Apply(d); // 每帧钉已知目标
                    continue;
                }
                _classified.Add(ptr);

                int itemId = -1;
                try
                {
                    var attr = d.itemAttr;
                    if (attr != null) itemId = Convert.ToInt32(Reflect.Get(attr, "itemId"));
                }
                catch { }

                if (itemId != PadDeployable.ItemId) continue;
                _pads.Add(ptr);
                try { DumpItem(d); } catch { }
                Plugin.L.LogInfo($"[TS] 圆盘放置物已修正: ptr={ptr} itemId={itemId}（进入每帧钉维护）");
                Apply(d);
            }
        }
        catch { } // 静默低频
    }

    private static readonly HashSet<long> _classified = new(); // 已分类（含非目标，避免重复反射）
    private static readonly HashSet<long> _pads = new();       // 目标盘（每帧钉）

    /// <summary>每帧钉：层 Character + order -5 + 主贴图（ppu 自适应）+ 零件禁用（游戏每帧重设 sprite/order，我们同样每帧覆盖）。</summary>
    private static void Apply(DeployableItem d)
    {
        EnsureBodySprite();
        var srs = d.GetComponentsInChildren<SpriteRenderer>(true);
        foreach (var sr in srs)
        {
            if (sr == null) continue;
            string sn = sr.name ?? "";
            if (sn.Contains("Cylinder") || sn.Contains("Parts") || sn.Contains("Fire"))
            {
                sr.enabled = false;
                continue;
            }
            sr.sortingLayerName = "Character";
            sr.sortingOrder = -5;
            if (_bodySprite != null) sr.sprite = _bodySprite;
        }
    }

    private static void EnsureBodySprite()
    {
        if (_bodyInit) return;
        _bodyInit = true;
        try
        {
            if (SpriteInjector.Cache.TryGetValue(900102, out var icon) && icon != null && icon.texture != null)
            {
                var tex = icon.texture;
                _bodySprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), tex.height / 7.0f);
                _bodySprite.name = "TeleportPad_Deploy_Body";
            }
        }
        catch { }
    }

    /// <summary>一次性 dump：root 名/组件类型/SR 概览（睡袋 vs 圆盘 vs 通用——寻找 prefab 绑定规则）。</summary>
    private static void DumpItem(DeployableItem d)
    {
        try
        {
            var t = d.transform;
            var r = t;
            while (r.parent != null) r = r.parent;
            string comps = "";
            foreach (var c in r.GetComponents<Component>())
            {
                if (c == null) continue;
                comps += c.GetType().Name + ",";
                if (comps.Length > 250) break;
            }
            string srs = "";
            foreach (var sr in r.GetComponentsInChildren<SpriteRenderer>(true))
            {
                if (sr == null) continue;
                string sn = "";
                try { sn = sr.sprite == null ? "<null>" : (sr.sprite.name ?? ""); } catch { }
                srs += $" [{sr.name}:{sr.sortingLayerName}/{sr.sortingOrder}/{sn}]";
            }
            int itId = -1;
            try
            {
                var attr = d.itemAttr;
                if (attr != null) itId = Convert.ToInt32(Reflect.Get(attr, "itemId"));
            }
            catch { }
            Plugin.L.LogInfo($"[TS] 放置物调查: root='{r.name}' itemId={itId} 组件=[{comps}] SR:{srs}");
        }
        catch { }
    }
}