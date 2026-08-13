using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace NoteTagPlugin;

[BepInPlugin("com.zedzone.notetag", "NoteTag", "0.5.4")]
public class Plugin : BasePlugin
{
    internal static Plugin Instance;
    internal static ManualLogSource L;

    public override void Load()
    {
        Instance = this;
        L = Log;
        ZedZoneShared.SharedLog.Initialize(
            m => Log.LogError(m),
            m => Log.LogWarning(m),
            m => Log.LogInfo(m));
        NoteTagUI.Instance = AddComponent<NoteTagUI>();
        AddComponent<NameTagRegistrar>();

        try
        {
            NameTagItem.Initialize(System.IO.Path.GetDirectoryName(typeof(Plugin).Assembly.Location));
        }
        catch (Exception e)
        {
            Log.LogError($"初始化插件目录失败: {e}");
        }

        try
        {
            var harmony = new Harmony("com.zedzone.notetag");
            TooltipPatcher.Apply(harmony);
            DropPatch.Apply(harmony);
        }
        catch (Exception e)
        {
            Log.LogError($"Harmony 初始化失败: {e}");
        }

        Log.LogInfo("命名牌插件已加载 (v0.5.4)");
    }
}
