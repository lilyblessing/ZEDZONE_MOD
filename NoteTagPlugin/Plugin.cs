using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace NoteTagPlugin;

[BepInPlugin("com.zedzone.notetag", "NoteTag", "0.5.2")]
public class Plugin : BasePlugin
{
    internal static Plugin Instance;
    internal static ManualLogSource L;

    public override void Load()
    {
        Instance = this;
        L = Log;
        NoteTagUI.Instance = AddComponent<NoteTagUI>();

        try
        {
            NameTagItem.Initialize(System.IO.Path.GetDirectoryName(typeof(Plugin).Assembly.Location));
        }
        catch (Exception e)
        {
            Log.LogError($"[NoteTag] 初始化插件目录失败: {e}");
        }

        try
        {
            var harmony = new Harmony("com.zedzone.notetag");
            TooltipPatcher.Apply(harmony);
            DropPatch.Apply(harmony);
        }
        catch (Exception e)
        {
            Log.LogError($"[NoteTag] Harmony 初始化失败: {e}");
        }

        Log.LogInfo("[NoteTag] 命名牌插件已加载 (v0.4.0)");
    }
}
