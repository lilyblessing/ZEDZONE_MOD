using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using UnityEngine;

/// <summary>
/// 建筑贴图导出探针 v0.8.0（一次性工具，非发布物；用完即 .disabled）。
/// v0.7 实证：建筑 sprite 所在 Runtime 图集 GetPixels 读回全透明（GPU→CPU 未回传），CPU 路径失效。
/// v0.8 方案：【整建筑相机拍照】——把目标建筑的完整 GameObject 克隆到远离玩家的空旷位置
///   （y+500 高空），用只看 Default 层的正交相机拍摄整个建筑（含全部子 SpriteRenderer 叠加效果，
///   即玩家实际看到的样子），输出整机照 + 各子件单独照。
/// </summary>
[BepInPlugin("com.zedzone.tool.itemsweepprobe", "ItemSweepProbe", "0.8.0")]
public class Plugin : BasePlugin
{
    internal static Plugin Instance;
    internal static ManualLogSource L;

    public override void Load()
    {
        Instance = this;
        L = Log;

        SharedLog.Initialize(
            (m) => Log.LogError(m),
            (m) => Log.LogWarning(m),
            (m) => Log.LogInfo(m));

        AddComponent<Sweeper>();
        Log.LogInfo("[ItemSweepProbe] 已加载 (v0.4.0)，F6 = 导出建筑贴图（需场景内有目标建筑）");
    }
}

/// <summary>导出执行器：F6 触发。</summary>
public class Sweeper : MonoBehaviour
{
    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.F6))
        {
            Plugin.L.LogInfo("[ItemSweepProbe] F6 触发建筑贴图导出");
            try { ExportBuildings(); }
            catch (Exception e) { Plugin.L.LogError($"[ItemSweepProbe] 顶层异常: {e}"); }
        }
    }

    private void ExportBuildings()
    {
        ExportOne<TerrainObject_Furniture_Commu>("commu");
        ExportOne<TerrainObject_Computer>("computer");
        ExportOne<TerrainObject_Production_StirlingGenerator>("stirling");
        Plugin.L.LogInfo("[ItemSweepProbe] 建筑贴图导出完毕");
    }

    /// <summary>按类型找全部场景实例，dump + 导出其 SpriteRenderer 贴图。</summary>
    private void ExportOne<T>(string tag) where T : UnityEngine.Object
    {
        var objs = Resources.FindObjectsOfTypeAll<T>();
        int count = objs != null ? objs.Length : 0;
        Plugin.L.LogInfo($"[ItemSweepProbe] ── {tag}: 实例数={count}");

        if (objs == null) return;
        int exported = 0;
        for (int i = 0; i < count; i++)
        {
            var obj = objs[i];
            if (obj == null) continue;

            Component comp = obj as Component;
            if (comp == null) { Plugin.L.LogInfo($"[ItemSweepProbe] {tag}[{i}] 非 Component（可能是 prefab/asset），跳过"); continue; }
            var go = comp.gameObject;

            // 只导出场景中的激活实例（hideFlags/DontSave 的跳过 prefab 资产）
            if (!go.scene.IsValid())
            {
                Plugin.L.LogInfo($"[ItemSweepProbe] {tag}[{i}] 不在场景中（asset），跳过: {go.name}");
                continue;
            }

            Plugin.L.LogInfo($"[ItemSweepProbe] {tag}[{i}] name={go.name} pos={comp.transform.position}");

            // v0.8：整建筑相机拍照（克隆到高空空旷处，正交相机拍整机）
            try
            {
                string file = $"{tag}_{i}_FULL_{Sanitize(go.name)}.png";
                PhotographWholeBuilding(go, Path.Combine(GetOutDir(), file));
                Plugin.L.LogInfo($"[ItemSweepProbe]   ✅ 整机照 → {file}");
                exported++;
            }
            catch (Exception e) { Plugin.L.LogWarning($"[ItemSweepProbe]   整机照异常: {e.Message}"); }

            var srs = go.GetComponentsInChildren<SpriteRenderer>(true);
            Plugin.L.LogInfo($"[ItemSweepProbe] {tag}[{i}] SpriteRenderer 数={srs.Length}");
            foreach (var sr in srs)
            {
                if (sr == null || sr.sprite == null)
                {
                    Plugin.L.LogInfo($"[ItemSweepProbe]   {GetPath(sr)} sprite=null");
                    continue;
                }
                var sp = sr.sprite;
                string objName = Sanitize(sr.gameObject.name);
                string sprName = Sanitize(sp.name);
                string file = $"{tag}_{i}_{objName}_{sprName}.png";
                try
                {
                    SaveSpriteCpu(sp, Path.Combine(GetOutDir(), file));
                    Plugin.L.LogInfo($"[ItemSweepProbe]   ✅ {GetPath(sr)} sprite={sp.name} rect=({sp.rect.x:F0},{sp.rect.y:F0},{sp.rect.width:F0}x{sp.rect.height:F0}) enabled={sr.enabled} active={sr.gameObject.activeInHierarchy} → {file}");
                    exported++;
                }
                catch (Exception e)
                {
                    Plugin.L.LogWarning($"[ItemSweepProbe]   导出 {sp.name} 异常: {e.Message}");
                }
            }

            // Image 组件（UI 类建筑可能有）
            var images = go.GetComponentsInChildren<UnityEngine.UI.Image>(true);
            foreach (var img in images)
            {
                if (img == null || img.sprite == null) continue;
                try
                {
                    string objName = Sanitize(img.gameObject.name);
                    string file = $"{tag}_{i}_IMG_{objName}.png";
                    SaveSpriteCpu(img.sprite, Path.Combine(GetOutDir(), file));
                    Plugin.L.LogInfo($"[ItemSweepProbe]   ✅ IMG {GetPath(img)} sprite={img.sprite.name} → {file}");
                    exported++;
                }
                catch { }
            }
        }
        Plugin.L.LogInfo($"[ItemSweepProbe] {tag} 导出 {exported} 个文件");
    }

    private static string GetOutDir()
    {
        string dir = Path.Combine(Environment.CurrentDirectory, "ItemSweepExport", "buildings");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// v0.8 整建筑拍照：克隆目标建筑 GameObject 到玩家上方 500m 空旷处（避开地形遮挡），
    /// 用正交相机拍摄整机（含全部子 SpriteRenderer 叠加效果 = 玩家实际看到的样子）。
    /// 拍摄后立即销毁克隆体。
    /// </summary>
    private static void PhotographWholeBuilding(GameObject source, string path)
    {
        GameObject clone = null, camGo = null;
        Camera cam = null;
        RenderTexture rt = null;
        var prevActive = RenderTexture.active;
        try
        {
            // 1) 克隆到高空
            clone = UnityEngine.Object.Instantiate(source);
            clone.name = "__probe_building__";
            var basePos = new Vector3(source.transform.position.x, source.transform.position.y + 500f, 0f);
            clone.transform.position = basePos;
            clone.transform.rotation = Quaternion.identity;

            // 计算包围盒
            var renders = clone.GetComponentsInChildren<SpriteRenderer>(true);
            if (renders == null || renders.Length == 0) throw new Exception("无 SpriteRenderer 可拍");
            Bounds bounds = renders[0].bounds;
            foreach (var r in renders)
            {
                if (r != null && r.sprite != null && r.gameObject.activeInHierarchy)
                    bounds.Encapsulate(r.bounds);
            }

            // 2) 正交相机
            camGo = new GameObject("__probe_cam__");
            cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            float size = Mathf.Max(bounds.size.x, bounds.size.y) * 0.6f + 0.1f;
            cam.orthographicSize = size;
            cam.cullingMask = ~0; // 全部层：建筑子件可能分布多 layer（19/18/0）
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            camGo.transform.position = bounds.center - new Vector3(0f, 0f, 10f);

            // 3) 渲染（512x512 足够改图用）
            rt = new RenderTexture(512, 512, 24);
            cam.targetTexture = rt;
            cam.Render();

            RenderTexture.active = rt;
            var full = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            full.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            full.Apply();
            File.WriteAllBytes(path, full.EncodeToPNG());
            UnityEngine.Object.Destroy(full);
        }
        finally
        {
            RenderTexture.active = prevActive;
            if (rt != null) { rt.Release(); UnityEngine.Object.Destroy(rt); }
            if (camGo != null) UnityEngine.Object.Destroy(camGo);
            if (clone != null) UnityEngine.Object.Destroy(clone);
        }
    }

    /// <summary>
    /// v0.7 CPU 直读图集裁剪：tex.GetPixels(rect) 纯 CPU（v0.3 物品贴图同路径已验证）。
    /// 全透明时自动 Y 翻转重试；输出前统计非透明像素打日志。
    /// </summary>
    private static void SaveSpriteCpu(Sprite sprite, string path)
    {
        var tex = sprite.texture;
        var r = sprite.rect;
        int x = Mathf.RoundToInt(r.x), y = Mathf.RoundToInt(r.y);
        int w = Mathf.RoundToInt(r.width), h = Mathf.RoundToInt(r.height);

        // 图集可能未可读：Blit 到临时 RT 再读回
        Texture2D readable = tex;
        var prevActive = RenderTexture.active;
        bool created = false;
        try
        {
            if (!tex.isReadable)
            {
                var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                readable = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
                readable.Apply();
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(rt);
                created = true;
            }

            var pixels = readable.GetPixels(x, y, w, h);
            int opaque = CountOpaque(pixels);

            // Y 翻转重试（图集 Y 系差异时 rect 的 y 从顶部算）
            if (opaque == 0 && y + h <= tex.height && tex.height - (y + h) >= 0)
            {
                var flipped = readable.GetPixels(x, tex.height - (y + h), w, h);
                int opaqueFlipped = CountOpaque(flipped);
                if (opaqueFlipped > 0)
                {
                    pixels = flipped;
                    opaque = opaqueFlipped;
                    Plugin.L.LogInfo($"[ItemSweepProbe]   {path} 使用 Y 翻转坐标命中");
                }
            }

            var outTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            outTex.SetPixels(pixels);
            outTex.Apply();
            File.WriteAllBytes(path, outTex.EncodeToPNG());
            UnityEngine.Object.Destroy(outTex);
            if (created) UnityEngine.Object.Destroy(readable);
            Plugin.L.LogInfo($"[ItemSweepProbe]   ✅ 已保存 {Path.GetFileName(path)} 非透明像素={opaque}/{w * h}");
        }
        finally
        {
            RenderTexture.active = prevActive;
        }
    }

    /// <summary>统计非透明像素数（alpha > 0.05）。</summary>
    private static int CountOpaque(Color[] pixels)
    {
        int n = 0;
        foreach (var px in pixels) if (px.a > 0.05f) n++;
        return n;
    }

    private static string GetPath(Component c)
    {
        try
        {
            var t = c.transform;
            var path = t.name;
            while (t.parent != null) { t = t.parent; path = t.name + "/" + path; }
            return path;
        }
        catch { return "?"; }
    }

    private static string Sanitize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "unnamed";
        foreach (var ch in new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|', ' ' })
            s = s.Replace(ch, '_');
        return s;
    }

    /// <summary>按 sprite 区域裁剪保存 PNG。</summary>
    private static void SaveSprite(Sprite sprite, string path)
    {
        var tex = sprite.texture;
        var rect = sprite.rect;
        var copy = new Texture2D((int)rect.width, (int)rect.height, TextureFormat.RGBA32, false);
        var prevActive = RenderTexture.active;
        try
        {
            var tempRt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32);
            try
            {
                Graphics.Blit(tex, tempRt);
                var prev = RenderTexture.active;
                RenderTexture.active = tempRt;
                copy.ReadPixels(new Rect(rect.x, rect.y, rect.width, rect.height), 0, 0);
                copy.Apply();
                RenderTexture.active = prev;
            }
            finally { RenderTexture.ReleaseTemporary(tempRt); }
        }
        finally { RenderTexture.active = prevActive; }
        File.WriteAllBytes(path, copy.EncodeToPNG());
        UnityEngine.Object.Destroy(copy);
    }
}