using System;
using UnityEngine;

namespace PortableFridgePlugin;

/// <summary>
/// 小冰箱注册调度与语言轮询：
/// - 延迟等待 ItemManager 就绪后注册（带重试，超过上限后降频 60s 直到注册成功）
/// - 语言切换轮询（游戏切语言后重设物品文本）
/// 保鲜/扣电逻辑见 FridgeMonitor。
/// </summary>
public class FridgeRegistrar : MonoBehaviour
{
    private const float RegisterDelay = 8f;
    private const float RetryInterval = 5f;
    private const int MaxRegisterTries = 10;
    private const float GiveUpInterval = 60f; // 超过重试上限后的降频间隔（直到成功）
    private const float LangCheckInterval = 2f;

    private float _registerTimer = RegisterDelay;
    private int _registerTries;
    private float _langCheckTimer = LangCheckInterval;

    private void Update()
    {
        // 语言切换检测：游戏内切语言后重设小冰箱物品文本
        _langCheckTimer -= Time.deltaTime;
        if (_langCheckTimer <= 0f)
        {
            _langCheckTimer = LangCheckInterval;
            if (Locale.Refresh())
            {
                PortableFridgeItem.ReapplyLanguage();
            }
        }

        // 延迟注册物品（等 ItemManager 初始化；上限后降频 60s 持续重试，覆盖游戏加载极慢场景）
        if (PortableFridgeItem.Registered) return;
        _registerTimer -= Time.deltaTime;
        if (_registerTimer > 0f) return;
        if (PortableFridgeItem.Register())
        {
            // 注册完成（Registered 已置 true，下次 Update 直接返回）
        }
        else if (++_registerTries >= MaxRegisterTries)
        {
            _registerTimer = GiveUpInterval;
        }
        else
        {
            _registerTimer = RetryInterval;
        }
    }
}
