using System;

namespace NoteTagPlugin;

/// <summary>
/// 游戏语言检测（仅支持简体中文 / 英文两档）。
/// 不缓存——实时查询 LanguageRegistry.IsCurrentChinese()，支持游戏内切换语言即时生效。
/// </summary>
public static class Locale
{
    /// <summary>当前游戏是否为英文。</summary>
    public static bool IsEnglish()
    {
        try { return !LanguageRegistry.IsCurrentChinese(); }
        catch { return false; }
    }

    /// <summary>按语言选文本：英文返回 en，否则返回 zh。</summary>
    public static string T(string zh, string en) => IsEnglish() ? en : zh;
}
