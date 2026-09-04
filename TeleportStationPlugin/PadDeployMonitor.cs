using System;
using System.Collections.Generic;
using UnityEngine;

namespace TeleportStationPlugin;

/// <summary>
/// v0.7.1：圆盘放置物渲染监控（DeployableItem.ActiveDeployableItems 遍历——游戏原生放置物全局列表）。
/// v0.7.2：①贴图也每帧钉（游戏每帧重设放置物 SR.sprite，只写一次无效）→ 大小恒 9.8×7；
/// v0.7.3：一次性分类缓存——非目标放置物不再每帧反射（每帧仅 O(1) 跳过）。
/// P0-B（2026-08-31）：分类键由 Pointer 改 GetInstanceID（存活期唯一，防指针地址复用误分类）+ 10s 全量重扫（缓存永不失效兜底）；
///                    Apply 的 GetComponentsInChildren 降频到 0.5s 一次（SR 缓存），不再每帧分配数组。
/// </summary>
public class PadDeployMonitor : MonoBehaviour
{
    private static Sprite _bodySprite;
    private static bool _bodyInit;

    private static readonly HashSet<int> _classified = new(); // 已分类（含非目标，避免重复反射）
    private static readonly HashSet<int> _pads = new();       // 目标盘（每帧钉）
    private static readonly HashSet<int> _dumped = new();     // 已取证盘（DumpItem/首见日志仅一次，W1 修复：10s 重扫不重复取证）
    private static readonly Dictionary<int, SpriteRenderer[]> _srCache = new();
    private static float _lastRescan = -1f;
    private static float _srCacheAt = -1f;
    private static float _lastWarn = -100f; // W2 修复：启动期 3s 内的首次告警不被吞
    private static int _characterLayerId = int.MinValue; // P2-9：Character 层 ID 常驻（建时取一次，int.MinValue=未初始化）

    private void Update()
    {
        try
        {
            var list = DeployableItem.ActiveDeployableItems;
            if (list == null || PadDeployable.ItemId < 0) return;

            // P0-B：10s 全量重扫，防实例 ID 复用后误分类（原缓存只增不减是缺陷）
            float now = Time.unscaledTime;
            if (_lastRescan < 0f || now - _lastRescan > 10f)
            {
                _lastRescan = now;
                _classified.Clear();
                _pads.Clear();
            }
            // Apply 的 SR 数组每 0.5s 失效一次（替代每帧 GetComponentsInChildren 分配；构建时顺带零件禁用）
            if (_srCacheAt < 0f || now - _srCacheAt > 0.5f)
            {
                _srCacheAt = now;
                _srCache.Clear();
            }

            for (int i = 0; i < list.Count; i++)
            {
                var d = list[i];
                if (d == null) continue;

                int key = d.GetInstanceID(); // 实例 ID：存活期唯一（P0-B 替代 Pointer）
                if (_classified.Contains(key))
                {
                    if (_pads.Contains(key)) Apply(d);
                    continue;
                }
                _classified.Add(key);

                int itemId = -1;
                try
                {
                    var attr = d.itemAttr;
                    if (attr != null) itemId = Convert.ToInt32(Reflect.Get(attr, "itemId"));
                }
                catch (Exception e) { LogWarnOnce($"itemAttr 读取异常: {e.Message.Split('\n')[0]}"); }

                if (itemId != PadDeployable.ItemId) continue;
                _pads.Add(key);
                if (_dumped.Add(key)) // W1：取证（DumpItem+首见日志）仅首次执行，10s 重扫不重复
                {
                    try { DumpItem(d); }
                    catch (Exception e) { LogWarnOnce($"Dump 异常: {e.Message.Split('\n')[0]}"); }
                    Plugin.L.LogInfo($"[TS] 圆盘放置物已修正: key={key} itemId={itemId}（进入每帧钉维护）");
                }
                Apply(d);
            }
        }
        catch (Exception e) { LogWarnOnce($"Update 异常: {e.Message.Split('\n')[0]}"); }
    }

    /// <summary>每帧钉：层 Character + order -5 + 主贴图（ppu 自适应）。SR 数组 0.5s 缓存；
    /// 缓存构建时把零件（Cylinder/Parts/Fire）禁用并剔除——每帧零 name 检查零分配（游戏每帧重设 sprite/order，我们每帧覆盖）。</summary>
    private static void Apply(DeployableItem d)
    {
        try
        {
            EnsureBodySprite();
            if (!_srCache.TryGetValue(d.GetInstanceID(), out var srs))
            {
                var all = d.GetComponentsInChildren<SpriteRenderer>(true);
                var keep = new List<SpriteRenderer>();
                foreach (var sr in all)
                {
                    if (sr == null) continue;
                    string n = sr.name ?? "";
                    if (n.Contains("Cylinder") || n.Contains("Parts") || n.Contains("Fire"))
                    {
                        sr.enabled = false; // 零件禁用（游戏重建 SR 恢复后最多 0.5s 重申）
                        continue;
                    }
                    keep.Add(sr);
                }
                srs = keep.ToArray();
                _srCache[d.GetInstanceID()] = srs;
            }
            // P2-9：层归属快照语义——已在目标层/order 则零重复写入；sortingLayerID 整型比较替代字符串比较
            int layerId = GetCharacterLayerId();
            bool useId = layerId != int.MinValue;
            foreach (var sr in srs)
            {
                if (sr == null) continue;
                try
                {
                    if (useId) { if (sr.sortingLayerID != layerId) sr.sortingLayerID = layerId; }
                    else sr.sortingLayerName = "Character";
                }
                catch { try { sr.sortingLayerName = "Character"; } catch {} }
                if (sr.sortingOrder != -5) sr.sortingOrder = -5;
                if (_bodySprite != null) sr.sprite = _bodySprite;
            }
        }
        catch (Exception e) { LogWarnOnce($"Apply 异常: {e.Message.Split('\n')[0]}"); }
    }

    // P2-9：Character 层 ID 建时取一次常驻；取失败返回 int.MinValue 由调用方回退字符串赋值。全程 try/catch。
    private static int GetCharacterLayerId()
    {
        try
        {
            if (_characterLayerId == int.MinValue)
                _characterLayerId = SortingLayer.NameToID("Character");
        }
        catch { }
        return _characterLayerId;
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
        catch (Exception e) { LogWarnOnce($"BodySprite 异常: {e.Message.Split('\n')[0]}"); }
    }

    internal static void ResetForIdentity() { try { _classified.Clear(); _pads.Clear(); _dumped.Clear(); _srCache.Clear(); } catch { } }

    private static void LogWarnOnce(string msg)
    {
        if (Time.unscaledTime - _lastWarn < 3f) return;
        _lastWarn = Time.unscaledTime;
        Plugin.L.LogWarning($"[TS] PadMonitor {msg}");
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
        catch (Exception e) { LogWarnOnce($"Dump 失败: {e.Message.Split('\n')[0]}"); }
    }
}