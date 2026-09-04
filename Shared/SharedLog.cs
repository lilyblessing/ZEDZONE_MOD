using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace ZedZoneShared;

/// <summary>
/// 共享库日志注入：各 mod Plugin.Load 时调 Initialize 绑定到自身日志源。
/// BepInEx 6 日志已带插件名，消息内无需再带 mod 前缀。
/// P1-11B（2026-09-04）：同文本节流——同级别同文本 2 秒内只透一条，其余计 suppressed；
/// 窗口结束时若 suppressed&gt;0 补一条“原文（x条同类已抑制）”。日志内容本身不变，只降频。
/// Error/Warning/Info 对外仍是 Action&lt;string&gt;（读即取节流包装、写即换底层源），
/// 现有赋值与调用方式不变，Initialize 签名不变。
/// </summary>
public static class SharedLog
{
    private static Action<string> _errorImpl = _ => { };
    private static Action<string> _warningImpl = _ => { };
    private static Action<string> _infoImpl = _ => { };

    // 原 public 字段改属性（类型仍是 Action<string>）：原“读字段即调用”变为“取包装再调用”，
    // 快照语义与原来一致（调用点 SharedLog.Info("...") 与 Initialize 赋值均无需改动）。
    public static Action<string> Error
    {
        get { var impl = _errorImpl; return msg => Emit(impl, "E", msg); }
        set { _errorImpl = value ?? (_ => { }); }
    }

    public static Action<string> Warning
    {
        get { var impl = _warningImpl; return msg => Emit(impl, "W", msg); }
        set { _warningImpl = value ?? (_ => { }); }
    }

    public static Action<string> Info
    {
        get { var impl = _infoImpl; return msg => Emit(impl, "I", msg); }
        set { _infoImpl = value ?? (_ => { }); }
    }

    public static void Initialize(Action<string> error, Action<string> warning, Action<string> info)
    {
        Error = error ?? (_ => { });
        Warning = warning ?? (_ => { });
        Info = info ?? (_ => { });
    }

    private const float ThrottleWindowSec = 2f;

    // key = 级别前缀 + 分隔符 + 原文（同级别同文本才算同类，跨级别不互抑）；
    // value =（窗口起点秒，窗口内已抑制条数）。窗口滚动且无抑制时清记录，一次性消息不留痕。
    private static readonly ConcurrentDictionary<string, (float windowStart, int suppressed)> Throttle =
        new ConcurrentDictionary<string, (float windowStart, int suppressed)>();

    // 秒级时钟：TickCount64 无实际回绕之忧（float 精度对 2 秒窗口足够），异常时回 0（= 全抑制，直通靠外层 catch）。
    private static float NowSec()
    {
        try { return (float)(Environment.TickCount64 / 1000.0); }
        catch { return 0f; }
    }

    private static void Emit(Action<string> impl, string level, string msg)
    {
        try
        {
            string text = msg ?? "<null>";
            string key = level + "\0" + text;
            float now = NowSec();
            bool pass = false;
            string flush = null;
            while (true)
            {
                if (!Throttle.TryGetValue(key, out var st))
                {
                    if (Throttle.TryAdd(key, (now, 0))) { pass = true; break; }
                    continue;
                }
                if (now - st.windowStart >= ThrottleWindowSec)
                {
                    var fresh = (windowStart: now, suppressed: 0);
                    if (Throttle.TryUpdate(key, fresh, st))
                    {
                        pass = true;
                        if (st.suppressed > 0) flush = text + $"（{st.suppressed}条同类已抑制）";
                        else ((ICollection<KeyValuePair<string, (float, int)>>)Throttle).Remove(new KeyValuePair<string, (float, int)>(key, fresh));
                        break;
                    }
                    continue;
                }
                if (Throttle.TryUpdate(key, (st.windowStart, st.suppressed + 1), st)) break;
            }
            if (!pass) return;
            try { impl(msg); }
            catch { }
            if (flush != null)
            {
                try { impl(flush); }
                catch { }
            }
        }
        catch
        {
            // 节流层自身异常时直通原文，保证日志不丢、内容与原来一致。
            try { impl(msg); }
            catch { }
        }
    }
}
