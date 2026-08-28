using System;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using UnityEngine;

namespace LogKeep;

/// <summary>
/// LogKeep v0.1.0 —— BepInEx 日志自动归档（配合 BepInEx.cfg [Logging.Disk] AppendLog=true）。
/// 无 Harmony、无 detour、纯文件 IO：每 60s 把 LogOutput.log 快照到 BepInEx\log-archive\LogOutput-<时间戳>.log，保留最近 30 份。
/// 目的：每次游戏启动都会覆写/追加 LogOutput.log，留多份时间戳日志便于多轮问题的对比取证（崩溃前也有近况）。
/// </summary>
[BepInPlugin("com.zedzone.tool.logkeep", "LogKeep", "0.1.0")]
public class Plugin : BasePlugin
{
    public static ManualLogSource L;

    public override void Load()
    {
        L = Log;
        // 醒目分隔横幅：多轮日志共存时可辨识本轮起点（与 [LogKeep] ===== 对应）
        Log.LogInfo($"[LogKeep] ==================== 本轮运行开始 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====================");
        AddComponent<LogKeepComponent>();
        Log.LogInfo("[LogKeep] v0.1.0 已加载（每 60s 归档 LogOutput.log 快照，保留最近 30 份）");
    }
}

public class LogKeepComponent : MonoBehaviour
{
    private static readonly string LogDir = Path.Combine(Environment.CurrentDirectory, "BepInEx");
    private static readonly string LogFile = Path.Combine(LogDir, "LogOutput.log");
    private static readonly string ArchiveDir = Path.Combine(LogDir, "log-archive");
    private float _next = 30f; // 启动 30s 后首次归档

    private void Update()
    {
        _next -= Time.unscaledDeltaTime;
        if (_next > 0f) return;
        _next = 60f;
        try { ArchiveOnce(); }
        catch (Exception e) { Plugin.L.LogWarning($"[LogKeep] 归档异常: {e.Message.Split('\n')[0]}"); }
    }

    private void ArchiveOnce()
    {
        if (!File.Exists(LogFile)) return;
        Directory.CreateDirectory(ArchiveDir);
        string ts = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string dst = Path.Combine(ArchiveDir, $"LogOutput-{ts}.log");
        File.Copy(LogFile, dst, true);
        // 只保留最近 30 份（按文件名时间戳排序）
        var olds = Directory.GetFiles(ArchiveDir, "LogOutput-*.log")
            .OrderByDescending(f => f).Skip(30).ToArray();
        foreach (var f in olds)
        {
            try { File.Delete(f); } catch { }
        }
        int total = Directory.GetFiles(ArchiveDir, "LogOutput-*.log").Length;
        Plugin.L.LogInfo($"[LogKeep] 已归档: {Path.GetFileName(dst)}（共 {total} 份）");
    }
}