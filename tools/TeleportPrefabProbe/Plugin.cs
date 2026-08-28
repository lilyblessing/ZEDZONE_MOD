using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;

namespace TeleportPrefabProbe;

/// <summary>
/// TeleportPrefabProbe v0.1.0 —— P2 实体 prefab 取证（只读，零 UI 写入）。
/// 目标：确定三建筑（900101-3）实体的 prefab 结构与贴图替换参数：
///   a. GetTerrainObjectPrefabById 返回的 prefab 组件树（根组件/SpriteRenderer/子物体层级）；
///   b. SpriteRenderer.sprite 名 / 尺寸(rect) / pixelsPerUnit / 材质 / 排序层 —— 判断贴图替换的可行性与缩放处理；
///   c. 对照参照 prefab（108 通讯终端 / 120 斯特林）与我们的 9001xx（字典镜像/兜底是否生效、PrefabByIdPostfix 是否工作）。
/// 录入日志：全部 [TPP]。触发点：GetTerrainObjectPrefabById postfix（对 108/120/9001xx 记录一次）。
/// </summary>
[BepInPlugin("com.zedzone.tool.teleportprefabprobe", "TeleportPrefabProbe", "0.1.0")]
public class Plugin : BasePlugin
{
    public static ManualLogSource L;

    public override void Load()
    {
        L = Log;
        try
        {
            var h = new Harmony("com.zedzone.tool.teleportprefabprobe");
            var m = AccessTools.Method(typeof(GameController), "GetTerrainObjectPrefabById");
            if (m != null)
            {
                h.Patch(m, postfix: new HarmonyMethod(typeof(Patches).GetMethod(
                    nameof(Patches.Postfix), System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)));
                L.LogInfo("[TPP] 已挂钩 GameController.GetTerrainObjectPrefabById（postfix 只读）");
            }
            else L.LogWarning("[TPP] GetTerrainObjectPrefabById 挂钩失败");
        }
        catch (Exception e) { L.LogError($"[TPP] hook 异常: {e.Message.Split('\n')[0]}"); }
        L.LogInfo("[TPP] TeleportPrefabProbe v0.1.0 已加载");
    }
}

public static class Patches
{
    private static int _reported;

    public static void Postfix(int __0, GameObject __result)
    {
        try
        {
            if (__0 != 108 && __0 != 120 && (__0 < 900101 || __0 > 900103)) return;
            _reported++;
            Plugin.L.LogInfo($"[TPP] ===== prefab {__0} #report={_reported} =====");
            if (__result == null) { Plugin.L.LogInfo("[TPP]   __result=null"); return; }
            Plugin.L.LogInfo($"[TPP]   name={__result.name} active={__result.activeSelf} layer={__result.layer}");
            // 根组件
            foreach (var c in __result.GetComponents<Component>())
            {
                if (c == null) continue;
                Plugin.L.LogInfo($"[TPP]   组件: {c.GetType().Name}");
            }
            // SpriteRenderer 详情
            var sr = __result.GetComponentInChildren<UnityEngine.SpriteRenderer>(true);
            if (sr != null)
            {
                var s = sr.sprite;
                string sInfo = "null";
                if (s != null)
                {
                    sInfo = s.name;
                    try { sInfo += $" rect=({s.rect.width:F1}x{s.rect.height:F1}) ppu={s.pixelsPerUnit} pivot={s.pivot.x:F1},{s.pivot.y:F1}"; } catch { }
                }
                Plugin.L.LogInfo($"[TPP]   SpriteRenderer: {sr.name} sprite={sInfo} sortingLayer={sr.sortingLayerName}({sr.sortingLayerID}) order={sr.sortingOrder} enabled={sr.enabled}");
                try
                {
                    var mat = sr.sharedMaterial;
                    if (mat != null) Plugin.L.LogInfo($"[TPP]     material={mat.name} shader={(mat.shader != null ? mat.shader.name : "null")}");
                }
                catch { }
            }
            else Plugin.L.LogInfo("[TPP]   SpriteRenderer: 未找到");
            // 子物体树（2 层）
            Walk(__result.transform, "", 2);
            Plugin.L.LogInfo("[TPP] ===== 结束 =====");
        }
        catch (Exception e) { Plugin.L.LogWarning($"[TPP] 取证异常: {e.Message.Split('\n')[0]}"); }
    }

    private static void Walk(Transform t, string indent, int depth)
    {
        try
        {
            for (int i = 0; i < t.childCount; i++)
            {
                var c = t.GetChild(i);
                if (c == null) continue;
                var srs = c.GetComponentsInChildren<UnityEngine.SpriteRenderer>(true);
                string srInfo = "";
                if (srs != null && srs.Length > 0)
                {
                    try
                    {
                        var sp = srs[0].sprite;
                        srInfo = sp == null ? " [SR* null]" : $" [SR* {sp.name}]";
                    }
                    catch { srInfo = " [SR*?]"; }
                }
                Plugin.L.LogInfo($"[TPP]   {indent}{c.name}{srInfo}");
                if (depth > 1) Walk(c, indent + "  ", depth - 1);
            }
        }
        catch { }
    }
}