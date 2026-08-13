using System;
using UnityEngine;

namespace NoteTagPlugin;

/// <summary>
/// 命名牌注册调度与语言轮询：
/// - 延迟等待 ItemManager 就绪后注册命名牌（带重试，最多 MaxRegisterTries 次）
/// - 自检（ActiveObjects 数量，启动诊断）
/// - 语言切换轮询（游戏切语言后重设物品文本与 tooltip marker）
/// </summary>
public class NameTagRegistrar : MonoBehaviour
{
    private const float RegisterDelay = 10f;
    private const float RetryInterval = 5f;
    private const int MaxRegisterTries = 6;
    private const float LangCheckInterval = 2f;

    private float _registerTimer = RegisterDelay;
    private int _registerTries;
    private bool _selfChecked;
    private float _langCheckTimer = LangCheckInterval;

    private void Update()
    {
        if (!_selfChecked)
        {
            _selfChecked = true;
            SelfCheck();
        }

        // 语言切换检测：游戏内切语言后重设命名牌物品文本 + 重建 tooltip marker
        _langCheckTimer -= Time.deltaTime;
        if (_langCheckTimer <= 0f)
        {
            _langCheckTimer = LangCheckInterval;
            if (Locale.Refresh())
            {
                NameTagItem.ReapplyLanguage();
                TooltipPatcher.InvalidateLanguage();
            }
        }

        // 延迟注册命名牌（等 ItemManager 初始化，最多重试 MaxRegisterTries 次）
        if (NameTagItem.Registered) return;
        _registerTimer -= Time.deltaTime;
        if (_registerTimer > 0f) return;
        if (NameTagItem.Register())
        {
            // 注册完成（Registered 已置 true，下次 Update 直接返回）
        }
        else if (++_registerTries >= MaxRegisterTries)
        {
            Plugin.L.LogError("[NoteTag] 命名牌注册多次尝试仍失败");
        }
        else
        {
            _registerTimer = RetryInterval;
        }
    }

    private void SelfCheck()
    {
        try
        {
            var list = BasicItemUI.ActiveObjects;
            int count = list != null ? list.Count : -1;
            Plugin.L.LogInfo($"[NoteTag] 自检: ActiveObjects={count}");
        }
        catch (Exception e)
        {
            Plugin.L.LogError($"[NoteTag] 自检失败: {e}");
        }
    }
}
