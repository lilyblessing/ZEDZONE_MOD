using System;

namespace ZedZoneShared;

/// <summary>
/// 共享库日志注入：各 mod Plugin.Load 时调 Initialize 绑定到自身日志源。
/// BepInEx 6 日志已带插件名，消息内无需再带 mod 前缀。
/// </summary>
public static class SharedLog
{
    public static Action<string> Error = _ => { };
    public static Action<string> Warning = _ => { };
    public static Action<string> Info = _ => { };

    public static void Initialize(Action<string> error, Action<string> warning, Action<string> info)
    {
        Error = error ?? (_ => { });
        Warning = warning ?? (_ => { });
        Info = info ?? (_ => { });
    }
}
