using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace TeleportIconProbe;

/// <summary>
/// TeleportIconProbe v0.1.0 —— 建造卡片/详情图标加载链取证（只读，零 UI 写入）。
/// 目标：搞清 v0.6.33（无写路径）下卡片图标/详情大图标空白时，游戏原生赋给 iconImage/detailIcon 的 sprite 是什么、
///       以及 GetTerrainObjectIconSprite 的真实调用者。
/// 录入日志：全部 [TIP]。
/// </summary>
[BepInPlugin("com.zedzone.tool.teleporticonprobe", "TeleportIconProbe", "0.1.0")]
public class Plugin : BasePlugin
{
    public static ManualLogSource L;
    internal static float _lastImgLog = -1f;
    internal static float _lastSetLog = -1f;

    public override void Load()
    {
        L = Log;
        var h = new Harmony("com.zedzone.tool.teleporticonprobe");
        try
        {
            // hook1：建造菜单加载后 0.5s 一次性 dump 卡片层（原版卡 vs 9001xx 对照）
            var lcm = AccessTools.Method(typeof(ConstructionPanel), "LoadConstructionMenu");
            if (lcm != null)
            {
                h.Patch(lcm, postfix: new HarmonyMethod(typeof(Patches).GetMethod(
                    nameof(Patches.OnMenu), System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)));
                L.LogInfo("[TIP] 已挂钩 ConstructionPanel.LoadConstructionMenu（postfix）");
            }
            else L.LogWarning("[TIP] LoadConstructionMenu 挂钩失败");

            // hook2：Image.set_sprite 只读观察（详情/卡片图标的赋值链；节流 0.2s；不拦不改）
            var setSp = AccessTools.Method(typeof(Image), "set_sprite");
            if (setSp != null)
            {
                h.Patch(setSp, prefix: new HarmonyMethod(typeof(Patches).GetMethod(
                    nameof(Patches.OnSetSprite), System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)));
                L.LogInfo("[TIP] 已挂钩 Image.set_sprite（只读观察）");
            }
            else L.LogWarning("[TIP] Image.set_sprite 挂钩失败");

            // hook3：GetTerrainObjectIconSprite postfix——谁在调、返回什么（含堆栈首帧）
            var gti = AccessTools.Method(typeof(GameController), "GetTerrainObjectIconSprite");
            if (gti != null)
            {
                h.Patch(gti, postfix: new HarmonyMethod(typeof(Patches).GetMethod(
                    nameof(Patches.OnIconSprite), System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)));
                L.LogInfo("[TIP] 已挂钩 GameController.GetTerrainObjectIconSprite（postfix）");
            }
            else L.LogWarning("[TIP] GetTerrainObjectIconSprite 挂钩失败");
        }
        catch (Exception e) { L.LogError($"[TIP] hook 异常: {e.Message.Split('\n')[0]}"); }

        AddComponent<IconProbe>();
        L.LogInfo("[TIP] TeleportIconProbe v0.1.0 已加载");
    }
}

public class IconProbe : MonoBehaviour
{
    private float _dumpAt = -1f;
    private bool _dumpPending;

    internal static void ScheduleDump()
    {
        var go = new GameObject("TeleportIconProbe");
        UnityEngine.Object.DontDestroyOnLoad(go);
        var p = go.AddComponent<IconProbe>();
        p._dumpPending = true;
        p._dumpAt = Time.unscaledTime + 0.5f;
    }

    private void Update()
    {
        if (!_dumpPending) return;
        if (Time.unscaledTime < _dumpAt) return;
        _dumpPending = false;
        try { DumpCards(); }
        catch (Exception e) { Plugin.L.LogWarning($"[TIP] dump 异常: {e.Message.Split('\n')[0]}"); }
        Destroy(gameObject);
    }

    private static void DumpCards()
    {
        var inst = typeof(ConstructionPanel).GetProperty("instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.GetValue(null) as Component;
        if (inst == null) { Plugin.L.LogInfo("[TIP] dump: ConstructionPanel.instance=null"); return; }
        if (inst.gameObject == null || !inst.gameObject.activeInHierarchy) { Plugin.L.LogInfo("[TIP] dump: panel 未激活"); return; }
        var gc = Reflect.Get(inst, "gridContent") as RectTransform;
        if (gc == null) { Plugin.L.LogInfo("[TIP] dump: gridContent=null"); return; }
        Plugin.L.LogInfo($"[TIP] ===== dump gridContent children={gc.childCount} =====");
        for (int i = 0; i < gc.childCount; i++)
        {
            var c = gc.GetChild(i);
            if (c == null) continue;
            var ui = c.GetComponent<ConstructionItemCardUI>();
            Image img = null;
            try { img = ui == null ? null : ui.iconImage; } catch { }
            string sName = "?";
            bool en = false; float a = -1f;
            if (img != null)
            {
                try
                {
                    sName = img.sprite == null ? "<null>" : img.sprite.name;
                    en = img.enabled;
                    a = img.color.a;
                }
                catch (Exception e2) { sName = "<异常:" + e2.Message.Split('\n')[0] + ">"; }
            }
            Plugin.L.LogInfo($"[TIP] Card '{c.name}': ui={(ui == null ? "null" : "OK")} icon={(img == null ? "null" : "OK")} sprite={sName} enabled={en} alpha={a:F2}");
        }
        // 详情框现状
        try
        {
            var di = Reflect.Get(inst, "detailIcon") as Image;
            if (di != null)
            {
                string dn = di.sprite == null ? "<null>" : di.sprite.name;
                Plugin.L.LogInfo($"[TIP] detailIcon: sprite={dn} enabled={di.enabled} alpha={di.color.a:F2} go={di.gameObject.name}");
            }
            else Plugin.L.LogInfo("[TIP] detailIcon: null");
        }
        catch (Exception e3) { Plugin.L.LogWarning($"[TIP] detailIcon dump 异常: {e3.Message.Split('\n')[0]}"); }
        Plugin.L.LogInfo("[TIP] ===== dump 结束 =====");
    }
}

public static class Patches
{
    public static void OnMenu()
    {
        Plugin.L.LogInfo("[TIP] LoadConstructionMenu 触发 → 调度 0.5s 后 dump");
        IconProbe.ScheduleDump();
    }

    /// <summary>只读观察 Image.set_sprite：仅对「卡片祖先链 Card_9001xx」或「名字含 detailIcon」的实例打日志（0.2s 节流）。</summary>
    public static bool OnSetSprite(Image __instance, Sprite value)
    {
        try
        {
            if (__instance == null) return true;
            float now = Time.unscaledTime;
            if (now - Plugin._lastSetLog < 0.2f) return true;
            bool target = false;
            string chain = "";
            int depth = 0;
            var t = __instance.transform;
            while (t != null && depth < 12)
            {
                string n = t.name;
                if (n != null)
                {
                    chain = n + ">" + chain;
                    if (n.StartsWith("Card_9001") || n.Contains("detailIcon") || n.Contains("DetailIcon")) { target = true; break; }
                }
                t = t.parent;
                depth++;
            }
            if (!target) return true;
            Plugin._lastSetLog = now;
            string vn = value == null ? "<null>" : value.name;
            Plugin.L.LogInfo($"[TIP] set_sprite: go={__instance.gameObject.name} chain={chain.TrimEnd('>')} value={vn}");
        }
        catch { }
        return true;
    }

    public static void OnIconSprite(int __0, Sprite __result)
    {
        try
        {
            Plugin.L.LogInfo($"[TIP] GetTerrainObjectIconSprite({__0}) → {(__result == null ? "<null>" : __result.name)}");
        }
        catch { }
    }
}