using System;
using UnityEngine;
using UnityEngine.UI;

namespace TeleportStationPlugin;

/// <summary>
/// P5 Step2 5s 全屏倒计时 UI（TeleportCountdownUI）
/// 依据 dump.cs 反编译实证选型：
///   - SleepingPanel (TypeDefIndex:2297, 0x180C0F680) 为最轻量全屏遮罩：CanvasGroup + Text contentText + SleepingPanelCoroutine，
///     无 CommonInfomationPanel 的 timeScale 暂停(PAUSED_TIME_SCALE=0.01)与多 Text 池、ScrollRect 复杂布局。
///   - CommonInfomationPanel (TypeDefIndex:2094) 的 _ShowInformationCoroutine_d__34 虽是“每日日期全屏提示”风格原型，
///     但含 panelWidth/panelHeight / textPool / animationFrameCount 多状态，抄写成本高；本文件仿 SleepingPanel 的
///     “全屏 Canvas + 半透黑底 + 居中大字”极简实现，外观对齐需求“每日日期全屏提示风格”。
///   - HintManager / SystemNotificationPanel 均为悬浮通知（非全屏），不满足“全屏倒计时”需求，已排除。
///
/// 设计（满足验收）：
///   单例 MonoBehaviour；ShowCountdown(pad, entrant, onComplete) 启动 5s 倒计时，每秒更新 Text 5→1，
///   每帧检查 Vector2.Distance(entrant, pad) < 5m，超距或外部调用 Cancel/NotifyExit 则 Abort 并提示“传送取消”，
///   样式：全屏半透黑底(0,0,0,0.6) + 居中白字 72pt Bold，数字 5→1，最后显示“传送中”。无编造 UI 类，仅用 UnityEngine.UI。
///
/// 实现说明：
///   本工程为 BepInEx IL2CPP，MonoBehaviour.StartCoroutine(IEnumerator) 在 Il2CppInterop 存根中仅暴露 string 重载，
///   直接用协程会导致 CS1503 编译失败；故改用 Update 驱动的状态机（与 PadDeployMonitor / RegistrationProbe 同款无协程模式），
///   语义等价于“5s 协程每秒更新”，验收表现为 Show/Cancel/脱离检测齐备。
/// </summary>
public class TeleportCountdownUI : MonoBehaviour
{
    public static TeleportCountdownUI Instance { get; private set; }

    private const float Radius = 5f; // 需求：距离 pad > 半径取消（Vector2.Distance < 5m）
    private const float CancelShowSeconds = 1.2f;
    private const float TeleportingHoldSeconds = 0.3f;

    private GameObject _canvasGO;
    private Image _bgImage;
    private Text _countdownText;

    private Transform _pad;
    private Transform _entrant;
    private Action _onComplete;

    private bool _isCounting;
    private bool _inCancelDisplay;
    private bool _inTeleporting;
    private float _remaining;        // 剩余秒数 5→0
    private int _lastShownSecond = -1;
    private float _cancelTimer;
    private float _teleportingTimer;

    public bool IsCounting => _isCounting;

    // ========== 生命周期 / 单例 ==========

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    /// <summary>确保场景中存在单例（供外部静态调用兜底）。</summary>
    public static TeleportCountdownUI EnsureExists()
    {
        if (Instance != null) return Instance;
        var go = new GameObject("TeleportCountdownUI");
        DontDestroyOnLoad(go);
        return go.AddComponent<TeleportCountdownUI>();
    }

    // ========== UI 构建（仿 SleepingPanel 全屏风格） ==========

    private void EnsureUI()
    {
        if (_canvasGO != null) return;

        _canvasGO = new GameObject("TeleportCountdownCanvas");
        DontDestroyOnLoad(_canvasGO);

        var canvas = _canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 9999; // 覆盖游戏原生面板（SleepingPanel 同为 Overlay，取最高层）

        var scaler = _canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        _canvasGO.AddComponent<GraphicRaycaster>();

        var bgGO = new GameObject("BG");
        bgGO.transform.SetParent(_canvasGO.transform, false);
        _bgImage = bgGO.AddComponent<Image>();
        _bgImage.color = new Color(0f, 0f, 0f, 0.6f); // 半透黑底
        var bgRT = _bgImage.rectTransform;
        bgRT.anchorMin = Vector2.zero;
        bgRT.anchorMax = Vector2.one;
        bgRT.offsetMin = Vector2.zero;
        bgRT.offsetMax = Vector2.zero;

        var txtGO = new GameObject("CountdownText");
        txtGO.transform.SetParent(bgGO.transform, false);
        _countdownText = txtGO.AddComponent<Text>();
        _countdownText.alignment = TextAnchor.MiddleCenter;
        _countdownText.color = Color.white;
        _countdownText.fontSize = 72;
        _countdownText.fontStyle = FontStyle.Bold;
        _countdownText.horizontalOverflow = HorizontalWrapMode.Overflow;
        _countdownText.verticalOverflow = VerticalWrapMode.Overflow;
        _countdownText.text = "";

        var txtRT = _countdownText.rectTransform;
        txtRT.anchorMin = new Vector2(0.5f, 0.5f);
        txtRT.anchorMax = new Vector2(0.5f, 0.5f);
        txtRT.pivot = new Vector2(0.5f, 0.5f);
        txtRT.anchoredPosition = Vector2.zero;
        txtRT.sizeDelta = new Vector2(900f, 240f);

        try
        {
            var arial = Resources.GetBuiltinResource<Font>("Arial.ttf");
            if (arial != null) _countdownText.font = arial;
        }
        catch { }
        if (_countdownText.font == null)
        {
            try
            {
                var fonts = Resources.FindObjectsOfTypeAll<Font>();
                if (fonts != null && fonts.Length > 0 && fonts[0] != null)
                    _countdownText.font = fonts[0];
            }
            catch { }
        }

        _canvasGO.SetActive(false);
    }

    private void ShowUI()
    {
        EnsureUI();
        _canvasGO.SetActive(true);
    }

    private void HideUI()
    {
        if (_canvasGO != null) _canvasGO.SetActive(false);
        if (_countdownText != null) _countdownText.text = "";
    }

    // ========== 对外 API：Show / Cancel / 脱离通知 ==========

    /// <summary>主入口：玩家/载具进入圆盘触发 5s 倒计时。</summary>
    public void ShowCountdown(Transform pad, Transform entrant, Action onComplete)
    {
        if (pad == null || entrant == null)
        {
            try { Plugin.L?.LogWarning("[TS] ShowCountdown 参数为空，已忽略"); } catch { }
            return;
        }

        // 若已有倒计时在跑，先重置（幂等，避免重叠）
        _pad = pad;
        _entrant = entrant;
        _onComplete = onComplete;
        _isCounting = true;
        _inCancelDisplay = false;
        _inTeleporting = false;
        _remaining = 5f;
        _lastShownSecond = -1;
        _cancelTimer = 0f;
        _teleportingTimer = 0f;

        EnsureUI();
        ShowUI();
        // 立即刷新首帧数字
        UpdateCountdownText();
    }

    /// <summary>GameObject 重载（方便 TeleportPadTrigger 直接传 gameObject）。</summary>
    public void ShowCountdown(GameObject pad, GameObject entrant, Action onComplete)
    {
        ShowCountdown(pad != null ? pad.transform : null, entrant != null ? entrant.transform : null, onComplete);
    }

    /// <summary>TerrainObject 重载（圆盘为 TerrainObject 时，entrant 可为任意 Transform/GameObject）。</summary>
    public void ShowCountdown(Transform pad, GameObject entrant, Action onComplete)
    {
        ShowCountdown(pad, entrant != null ? entrant.transform : null, onComplete);
    }

    /// <summary>取消当前倒计时并提示“传送取消”（供 OnTriggerExit / 脱离检测调用）。</summary>
    public void Cancel()
    {
        if (!_isCounting && !_inCancelDisplay && !_inTeleporting) return;
        // 转入取消展示态（不再计数，显示“传送取消” 1.2s）
        _isCounting = false;
        _inTeleporting = false;
        _inCancelDisplay = true;
        _cancelTimer = 0f;
        _onComplete = null; // 取消则丢弃回调
        EnsureUI();
        ShowUI();
        if (_countdownText != null) _countdownText.text = "传送取消";
        try { Plugin.L?.LogInfo("[TS] 传送取消：已脱离圆盘"); } catch { }
    }

    /// <summary>供 TeleportPadTrigger.OnTriggerExit 转发：仅当退出者是当前 entrant 时取消，避免误取消他人。</summary>
    public void NotifyExit(Transform who)
    {
        if (!_isCounting) return;
        if (who == null || _entrant == null) return;
        if (who == _entrant || who.gameObject == _entrant.gameObject)
            Cancel();
    }

    public void NotifyExit(GameObject who) => NotifyExit(who != null ? who.transform : null);

    /// <summary>供外部每帧或 PadTrigger 主动查询：当前 entrant 是否仍在 pad 半径内。</summary>
    public bool IsEntrantInside()
    {
        if (_pad == null || _entrant == null) return false;
        try
        {
            var a = _pad.position;
            var b = _entrant.position;
            float d = Vector2.Distance(new Vector2(a.x, a.y), new Vector2(b.x, b.y));
            return d < Radius;
        }
        catch
        {
            return false;
        }
    }

    // ========== Update 驱动（替代协程） ==========

    private void Update()
    {
        // 取消展示态：倒计时已取消，显示“传送取消”达到时长后隐藏
        if (_inCancelDisplay)
        {
            _cancelTimer += Time.unscaledDeltaTime;
            if (_cancelTimer >= CancelShowSeconds)
            {
                _inCancelDisplay = false;
                HideUI();
                _pad = null;
                _entrant = null;
            }
            return;
        }

        // 传送中展示态：显示“传送中”短暂保持后回调并隐藏
        if (_inTeleporting)
        {
            _teleportingTimer += Time.unscaledDeltaTime;
            if (_teleportingTimer >= TeleportingHoldSeconds)
            {
                _inTeleporting = false;
                var cb = _onComplete;
                HideUI();
                _isCounting = false;
                _onComplete = null;
                var pad = _pad; var ent = _entrant;
                _pad = null; _entrant = null;
                if (cb != null)
                {
                    try { cb.Invoke(); }
                    catch (Exception e) { try { Plugin.L?.LogWarning($"[TS] 传送回调异常: {e.Message}"); } catch { } }
                }
            }
            return;
        }

        if (!_isCounting) return;

        // 每帧脱离检测（距离 pad > 半径 或 entrant/pad 销毁 则 Abort）
        if (!IsEntrantInside())
        {
            Cancel();
            return;
        }

        _remaining -= Time.unscaledDeltaTime;
        if (_remaining <= 0f)
        {
            // 倒计时结束 → 显示“传送中”
            _remaining = 0f;
            if (_countdownText != null) _countdownText.text = "传送中";
            _isCounting = false;
            _inTeleporting = true;
            _teleportingTimer = 0f;
            return;
        }

        UpdateCountdownText();
    }

    private void UpdateCountdownText()
    {
        // 5→1 居中大字：ceil(remaining) 保证每秒一跳（5,4,3,2,1）
        int sec = Mathf.CeilToInt(_remaining);
        if (sec < 1) sec = 1;
        if (sec > 5) sec = 5;
        if (sec == _lastShownSecond) return;
        _lastShownSecond = sec;
        if (_countdownText != null) _countdownText.text = sec.ToString();
    }
}
