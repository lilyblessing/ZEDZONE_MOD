using System;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using UnityEngine;

namespace LogKeep;

/// <summary>
/// LogKeep v0.3.0 —— BepInEx 日志双写归档（tee 方案）。
/// 背景：v0.2.0 的「启动轮转」实测失败——BepInEx 打开 LogOutput.log 时不共享读权限（FileShare 不含 Read），
///       任何插件内读主日志文件都会 AccessDenied；故 v0.2.0 已回滚并放弃读文件思路。
/// 机制：Load() 时找到 DiskLogListener（BepInEx 写盘监听器），把它的 LogWriter 替换为 TeeWriter：
///   - 原 writer（主文件 LogOutput.log）继续正常写（由 BepInEx 管理，AppendLog=false 启动自清，主文件恒单轮）；
///   - 并行写一份完整副本到 BepInEx\log-archive\LogOutput-&lt;本轮启动时间&gt;.log —— 每轮一份独立完整日志，
///     崩溃时也完整（log 每行实时双写）。归档目录由本插件裁剪（保留最近 60 份）。
/// cfg 配套：BepInEx.cfg [Logging.Disk] AppendLog = false（主文件每轮覆写，token 成本恒定）。
/// </summary>
[BepInPlugin("com.zedzone.tool.logkeep", "LogKeep", "0.3.0")]
public class Plugin : BasePlugin
{
    public static ManualLogSource L;

    public override void Load()
    {
        L = Log;
        AddComponent<LogKeepComponent>();
        LogKeepComponent.Install();
        Log.LogInfo($"[LogKeep] ==================== 本轮运行开始 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====================");
        Log.LogInfo("[LogKeep] v0.3.0 已加载（tee 双写：主日志单轮 + log-archive 每轮时间戳归档）");
    }
}

public class LogKeepComponent : MonoBehaviour
{
    private static bool _installed;
    private static string _lastCleanup = "";

    private void Update()
    {
        // 轻量维护：归档目录 60 份上限（每轮启动 Install 后清一次即可；这里兜底定期）
        try
        {
            string now = DateTime.Now.ToString("yyyyMMddHH");
            if (_lastCleanup == now) return;
            _lastCleanup = now;
            string dir = ArchiveDir;
            if (!Directory.Exists(dir)) return;
            var olds = Directory.GetFiles(dir, "LogOutput-*.log")
                .OrderByDescending(f => f).Skip(60).ToArray();
            foreach (var f in olds)
            {
                try { File.Delete(f); } catch { }
            }
        }
        catch { }
    }

    private static string ArchiveDir => Path.Combine(Environment.CurrentDirectory, "BepInEx", "log-archive");

    internal static void Install()
    {
        if (_installed) return;
        try
        {
            foreach (var l in BepInEx.Logging.Logger.Listeners) // 全限定避免与 UnityEngine.Logger 歧义
            {
                if (l is DiskLogListener d && d.LogWriter != null)
                {
                    string ts = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    Directory.CreateDirectory(ArchiveDir);
                    string ark = Path.Combine(ArchiveDir, $"LogOutput-{ts}.log");
                    var fw = new StreamWriter(ark, true, Encoding.UTF8) { AutoFlush = true };
                    var tee = new TeeWriter(d.LogWriter, fw);
                    // LogWriter setter 非公开（ildump Public 显示有误），走反射设置
                    var prop = typeof(DiskLogListener).GetProperty("LogWriter", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (prop != null && prop.CanWrite) prop.SetValue(d, tee);
                    else
                    {
                        var m = typeof(DiskLogListener).GetMethod("set_LogWriter", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (m == null) { Plugin.L.LogWarning("[LogKeep] set_LogWriter 反射失败，双写未装"); fw.Dispose(); return; }
                        m.Invoke(d, new object[] { tee });
                    }
                    _installed = true;
                    Plugin.L.LogInfo($"[LogKeep] tee 双写已装: 本轮归档 → {Path.GetFileName(ark)}");
                    return;
                }
            }
            Plugin.L.LogWarning("[LogKeep] 未找到 DiskLogListener，双写未装");
        }
        catch (Exception e) { Plugin.L.LogWarning($"[LogKeep] 安装异常: {e.Message.Split('\n')[0]}"); }
    }
}

/// <summary>双写 TextWriter：同一内容同时写主日志与归档文件。Dispose 只关归档 writer（主 writer 归 BepInEx 管）。</summary>
public class TeeWriter : TextWriter
{
    private readonly TextWriter _main;
    private readonly TextWriter _archive;

    public TeeWriter(TextWriter main, TextWriter archive) { _main = main; _archive = archive; }

    public override Encoding Encoding => _main.Encoding;

    public override void Write(char value) { _main.Write(value); _archive.Write(value); }
    public override void Write(string value) { _main.Write(value); _archive.Write(value); }
    public override void WriteLine(string value) { _main.WriteLine(value); _archive.WriteLine(value); }
    public override void Flush() { _main.Flush(); _archive.Flush(); }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _archive.Flush(); _archive.Dispose(); } catch { }
        }
        base.Dispose(disposing);
    }
}