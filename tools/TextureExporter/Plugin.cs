using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using UnityEngine;

namespace TextureExporter;

/// <summary>
/// 一次性工具：导出游戏内指定物品的贴图为 PNG。
/// 用法：修改 ExportIds 后部署，启动游戏等待 15 秒（ItemManager 就绪），
/// 输出到 游戏根目录\TextureExport\ 下，日志打印导出结果。
/// 未注册任何 Harmony patch，卸载即删 dll。
/// </summary>
[BepInPlugin("com.zedzone.tool.textexporter", "TextureExporter", "1.0.0")]
public class Plugin : BasePlugin
{
    internal static Plugin Instance;
    internal static ManualLogSource L;

    public override void Load()
    {
        Instance = this;
        L = Log;
        AddComponent<Exporter>();
        L.LogInfo("[TextureExporter] 已加载，15 秒后自动导出");
    }
}

public class Exporter : MonoBehaviour
{
    // 要导出的物品 ID 列表（肉 204、5.56 弹链 741）
    private static readonly int[] ExportIds = { 532 };
    private float _timer = 15f;
    private bool _done;

    private void Update()
    {
        if (_done) return;
        _timer -= Time.deltaTime;
        if (_timer > 0f) return;
        _done = true;
        try { Run(); }
        catch (System.Exception e) { Plugin.L.LogError($"[TextureExporter] 导出异常: {e}"); }
    }

    private void Run()
    {
        var mgr = ItemManager.instance;
        if (mgr == null) { Plugin.L.LogError("[TextureExporter] ItemManager 未就绪"); return; }

        string outDir = System.IO.Path.Combine(System.Environment.CurrentDirectory, "TextureExport");
        System.IO.Directory.CreateDirectory(outDir);
        Plugin.L.LogInfo($"[TextureExporter] 输出目录: {outDir}");

        foreach (int id in ExportIds)
        {
            try
            {
                string name = "?";
                try
                {
                    var attr = mgr.GetItemAttrById(id);
                    if (attr != null) name = attr.itemName_Runtime;
                }
                catch { }

                var sprite = mgr.GetItemSprite(id);
                if (sprite == null)
                {
                    Plugin.L.LogWarning($"[TextureExporter] id={id} ({name}) GetItemSprite 返回 null");
                    continue;
                }

                var tex = sprite.texture;
                var r = sprite.rect;
                Plugin.L.LogInfo($"[TextureExporter] id={id} ({name}) texture={tex.width}x{tex.height} rect=({r.x},{r.y},{r.width},{r.height})");

                // 1) 完整纹理
                SavePng(tex, System.IO.Path.Combine(outDir, $"item_{id}_full.png"), $"[TextureExporter] id={id} 完整纹理");

                // 2) 按 sprite.rect 裁剪（物品图标本体）
                int x = Mathf.Clamp((int)r.x, 0, tex.width - 1);
                int y = Mathf.Clamp((int)r.y, 0, tex.height - 1);
                int w = Mathf.Clamp((int)r.width, 1, tex.width - x);
                int h = Mathf.Clamp((int)r.height, 1, tex.height - y);

                var px = tex.GetPixels(x, y, w, h);
                var cropped = new Texture2D(w, h, TextureFormat.RGBA32, false);
                cropped.SetPixels(px);
                cropped.Apply();
                SavePng(cropped, System.IO.Path.Combine(outDir, $"item_{id}.png"), $"[TextureExporter] id={id} 裁剪图标");
            }
            catch (System.Exception e)
            {
                Plugin.L.LogError($"[TextureExporter] id={id} 导出失败: {e}");
            }
        }

        Plugin.L.LogInfo("[TextureExporter] 导出完成");
    }

    private static void SavePng(Texture2D tex, string path, string tag)
    {
        var bytes = UnityEngine.ImageConversion.EncodeToPNG(tex);
        var arr = new byte[bytes.Length];
        for (int i = 0; i < bytes.Length; i++) arr[i] = bytes[i];
        System.IO.File.WriteAllBytes(path, arr);
        Plugin.L.LogInfo($"{tag}: {path} ({arr.Length / 1024} KB)");
    }
}

