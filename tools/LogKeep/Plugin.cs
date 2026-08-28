using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using UnityEngine;

namespace LogKeep;

/// <summary>
/// LogKeep v0.2.0 —— BepInEx 日志「启动轮转」方案（配合 BepInEx.cfg [Logging.Disk] AppendLog=true）。
/// 机制：
///  1) 游戏启动（插件 Load）：
///     a. 读 LogOutput.log：若非空则说明是上一轮运行残留；
///     b. 尾部提取 [LogKeep] END &lt;ts&gt;（正常退出标记）；崩溃无 END 则取 RUN &lt;ts&gt;（上轮开始），再退而取文件 mtime；
///     c. 整份复制到 BepInEx\log-archive\LogOutput-&lt;ts&gt;.log（保留最近 60 份自动裁剪）；
///     d. 截断清空主文件（BepInEx DiskLogListener 为 FileMode.Append+FileShare.ReadWrite，截断后追加仍写 EOF，安全）；
///     e. 打 [LogKeep] RUN &lt;ts&gt; 横幅。
///  2) 游戏退出（OnApplicationQuit）：打 [LogKeep] END &lt;ts&gt; 行 —— 下次启动据此命名归档。
/// 效果：主日志永远约单轮大小（读日志 token 成本恒定）；每轮完整日志按日期时间独立存档于 log-archive/。
/// 失败兜底：截断被拒（句柄共享异常）→ 告警 + 主日志继续累积、下次启动重试，绝不丢日志。
/// </summary>
[BepInPlugin("com.zedzone.tool.logkeep", "LogKeep", "0.2.0")]
public class Plugin : BasePlugin
{
    public static ManualLogSource L;

    public override void Load()
    {
        L = Log;
        AddComponent<LogKeepComponent>();
        LogKeepComponent.RotateOnce(); // 启动轮转：归档旧轮 → 清空主文件
        Plugin.L.LogInfo($"[LogKeep] RUN {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Log.LogInfo("[LogKeep] v0.2.0 已加载（启动轮转：旧轮归档到 log-archive/，主日志保持单轮）");
    }
}

public class LogKeepComponent : MonoBehaviour
{
    private static readonly string LogDir = Path.Combine(Environment.CurrentDirectory, "BepInEx");
    private static readonly string LogFile = Path.Combine(LogDir, "LogOutput.log");
    private static readonly string ArchiveDir = Path.Combine(LogDir, "log-archive");

    private void Update() { } // v0.2.0 纯轮转，无周期任务

    /// <summary>Unity 退出回调：写 END 时间戳（供下次启动归档命名）；崩溃时缺失则退化用 RUN/mtime。</summary>
    private void OnApplicationQuit()
    {
        try { Plugin.L.LogInfo($"[LogKeep] END {DateTime.Now:yyyy-MM-dd HH:mm:ss}"); }
        catch { }
    }

    public static void RotateOnce()
    {
        try
        {
            string full = File.Exists(LogFile) ? File.ReadAllText(LogFile) : "";
            if (string.IsNullOrWhiteSpace(full)) return; // 首次/已由上一轮清空

            string ts = ExtractTime(full) ?? File.GetLastWriteTime(LogFile).ToString("yyyyMMdd-HHmmss");
            Directory.CreateDirectory(ArchiveDir);
            string dst = Path.Combine(ArchiveDir, $"LogOutput-{ts}.log");
            if (!File.Exists(dst)) File.WriteAllText(dst, full); // 去重：同名已归档则跳过

            // 只保留最近 60 份（按文件名时间戳排序）
            var olds = Directory.GetFiles(ArchiveDir, "LogOutput-*.log")
                .OrderByDescending(f => f).Skip(60).ToArray();
            foreach (var f in olds)
            {
                try { File.Delete(f); } catch { }
            }

            // 截断主文件：BepInEx 用 FileMode.Append+FileShare.ReadWrite，截断后其追加仍写 EOF，安全
            try
            {
                using (var fs = new FileStream(LogFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { }
                Plugin.L.LogInfo($"[LogKeep] 轮转完成: 旧日志({full.Length} B) → {Path.GetFileName(dst)}，主日志已清空");
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning($"[LogKeep] 截断失败（句柄共享限制）: {e.Message.Split('\n')[0]}；主日志继续累积，下次启动重试");
            }
        }
        catch (Exception e) { Plugin.L.LogWarning($"[LogKeep] 轮转异常: {e.Message.Split('\n')[0]}"); }
    }

    /// <summary>优先取最近的 END（正常退出）；全无 END 取最后一个 RUN（崩溃轮开始时间）；都无则 null（调用方退用 mtime）。</summary>
    private static string ExtractTime(string content)
    {
        var m = Regex.Matches(content, @"\[LogKeep\] (END|RUN) (\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})");
        string lastRun = null;
        foreach (Match x in m)
        {
            string t = x.Groups[2].Value.Replace("-", "").Replace(":", "").Replace(" ", "-");
            if (x.Groups[1].Value == "END") return t;
            lastRun = t;
        }
        return lastRun;
    }
}