using System;

namespace ZedZoneShared;

/// <summary>
/// 游戏语言检测（仅支持简体中文 / 英文两档）。
/// 语言状态缓存：IsEnglish()/T() 读缓存（零 native 调用，供 OnGUI/tooltip 每帧热路径使用）；
/// 语言切换由轮询调 Refresh() 更新缓存并返回是否变化。
/// </summary>
public static class GameLocale
{
    private static bool _isEnglish;
    private static bool _known;

    /// <summary>重新检测当前语言并更新缓存；返回语言是否真的发生变化（首次不算变化）。</summary>
    public static bool Refresh()
    {
        bool now;
        try { now = !LanguageRegistry.IsCurrentChinese(); }
        catch { now = false; }
        bool changed = _known && now != _isEnglish;
        _isEnglish = now;
        _known = true;
        return changed;
    }

    /// <summary>当前游戏是否为英文（读缓存，首次调用时自动检测一次）。</summary>
    public static bool IsEnglish()
    {
        if (!_known) Refresh();
        return _isEnglish;
    }

    /// <summary>按语言选文本：英文返回 en，否则返回 zh。</summary>
    public static string T(string zh, string en) => IsEnglish() ? en : zh;
}
