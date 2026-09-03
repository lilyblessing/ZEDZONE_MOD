using System;
using UnityEngine;
using UnityEngine.UI;
using Il2CppInterop.Runtime.Attributes;

namespace TeleportStationPlugin;

/// <summary>
/// 控制台命名弹窗：当 ComputerPanel 选择"给当前的传送站命名"时弹出，写入 TeleportStationNameManager。
/// </summary>
public class TeleportStationRenameUI : MonoBehaviour
{
    public static TeleportStationRenameUI Instance { get; private set; }

    private GameObject _canvasGO;
    private InputField _input;
    private Text _title;
    private Button _okBtn;
    private Button _cancelBtn;
    private TerrainObject _targetConsole;
    private bool _isOpen = false;
    private float _openTime = -999f;

    public bool IsOpen => _isOpen;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
    }

    void OnDestroy() { if (Instance == this) Instance = null; }

    public static TeleportStationRenameUI EnsureExists()
    {
        if (Instance != null) return Instance;
        var go = new GameObject("TeleportStationRenameUI");
        UnityEngine.Object.DontDestroyOnLoad(go);
        return go.AddComponent<TeleportStationRenameUI>();
    }

    [HideFromIl2Cpp]
    public void Show(TerrainObject console)
    {
        _targetConsole = console;
        _isOpen = true;
        _openTime = Time.unscaledTime;
        EnsureUI();
        try
        {
            string cur = TeleportStationNameManager.GetName(console);
            if (_input != null) _input.text = cur ?? string.Empty;
        }
        catch {}
        if (_canvasGO != null) _canvasGO.SetActive(true);
        try { _input?.ActivateInputField(); } catch {}
        try { _input?.Select(); } catch {}
        try { Plugin.L?.LogInfo($"[TS][RenameUI] Show console={(console != null ? console.GetInstanceID().ToString() : "null")} name={( _input != null ? _input.text : "")}"); } catch {}
    }

    public void Close()
    {
        if (_canvasGO != null) _canvasGO.SetActive(false);
        _isOpen = false;
        _targetConsole = null;
    }

    void Update()
    {
        if (!_isOpen) return;
        if (Time.unscaledTime - _openTime <= 0.3f) return;
        try
        {
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                Close();
                return;
            }
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
            {
                OnConfirm();
            }
        }
        catch {}
    }

    private void EnsureUI()
    {
        if (_canvasGO != null) return;

        _canvasGO = new GameObject("TeleportRenameCanvas");
        UnityEngine.Object.DontDestroyOnLoad(_canvasGO);
        var canvas = _canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 10001;
        var scaler = _canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        _canvasGO.AddComponent<GraphicRaycaster>();

        // BG 半透全屏
        var bgGO = new GameObject("BG");
        bgGO.transform.SetParent(_canvasGO.transform, false);
        var bgImg = bgGO.AddComponent<Image>();
        bgImg.color = new Color(0f, 0f, 0f, 0.6f);
        var bgRT = bgImg.rectTransform;
        bgRT.anchorMin = Vector2.zero;
        bgRT.anchorMax = Vector2.one;
        bgRT.offsetMin = Vector2.zero;
        bgRT.offsetMax = Vector2.zero;
        var bgBtn = bgGO.AddComponent<Button>();
        // 点击 BG 不关闭（移除则可改为 Close）；当前保持不关闭，按规格注释保留可切换
        bgBtn.onClick.AddListener(new Action(() => { /* 点击背景不关闭 */ }));

        // Panel 居中 600x280
        var panelGO = new GameObject("Panel");
        panelGO.transform.SetParent(_canvasGO.transform, false);
        var panelImg = panelGO.AddComponent<Image>();
        panelImg.color = new Color(0.15f, 0.15f, 0.17f, 0.97f);
        var panelRT = panelImg.rectTransform;
        panelRT.anchorMin = new Vector2(0.5f, 0.5f);
        panelRT.anchorMax = new Vector2(0.5f, 0.5f);
        panelRT.pivot = new Vector2(0.5f, 0.5f);
        panelRT.sizeDelta = new Vector2(600f, 280f);
        panelRT.anchoredPosition = Vector2.zero;

        // Title "命名传送站" 顶部居中 24 Bold
        var titleGO = new GameObject("Title");
        titleGO.transform.SetParent(panelGO.transform, false);
        _title = titleGO.AddComponent<Text>();
        _title.text = "命名传送站";
        _title.alignment = TextAnchor.MiddleCenter;
        _title.fontSize = 24;
        _title.fontStyle = FontStyle.Bold;
        _title.color = Color.white;
        ApplyFont(_title);
        var titleRT = _title.rectTransform;
        titleRT.anchorMin = new Vector2(0f, 1f);
        titleRT.anchorMax = new Vector2(1f, 1f);
        titleRT.pivot = new Vector2(0.5f, 1f);
        titleRT.anchoredPosition = new Vector2(0f, -18f);
        titleRT.sizeDelta = new Vector2(0f, 36f);

        // InputField 行：背景白0.9，居中 560x48
        var inputGO = new GameObject("InputField");
        inputGO.transform.SetParent(panelGO.transform, false);
        var inputBG = inputGO.AddComponent<Image>();
        inputBG.color = new Color(1f, 1f, 1f, 0.9f);
        var inputRT = inputBG.rectTransform;
        inputRT.anchorMin = new Vector2(0.5f, 0.5f);
        inputRT.anchorMax = new Vector2(0.5f, 0.5f);
        inputRT.pivot = new Vector2(0.5f, 0.5f);
        inputRT.sizeDelta = new Vector2(560f, 48f);
        inputRT.anchoredPosition = new Vector2(0f, 18f);

        // Text 组件（输入文本）
        var textGO = new GameObject("Text");
        textGO.transform.SetParent(inputGO.transform, false);
        var textComp = textGO.AddComponent<Text>();
        textComp.supportRichText = false;
        textComp.alignment = TextAnchor.MiddleLeft;
        textComp.color = new Color(0.1f, 0.1f, 0.1f, 1f);
        textComp.fontSize = 20;
        ApplyFont(textComp);
        var textRT = textComp.rectTransform;
        textRT.anchorMin = Vector2.zero;
        textRT.anchorMax = Vector2.one;
        textRT.offsetMin = new Vector2(12f, 6f);
        textRT.offsetMax = new Vector2(-12f, -6f);

        // Placeholder
        var phGO = new GameObject("Placeholder");
        phGO.transform.SetParent(inputGO.transform, false);
        var phText = phGO.AddComponent<Text>();
        phText.text = "输入站名（最多16字）";
        phText.alignment = TextAnchor.MiddleLeft;
        phText.color = new Color(0.5f, 0.5f, 0.5f, 0.85f);
        phText.fontSize = 18;
        phText.fontStyle = FontStyle.Italic;
        ApplyFont(phText);
        var phRT = phText.rectTransform;
        phRT.anchorMin = Vector2.zero;
        phRT.anchorMax = Vector2.one;
        phRT.offsetMin = new Vector2(12f, 6f);
        phRT.offsetMax = new Vector2(-12f, -6f);

        _input = inputGO.AddComponent<InputField>();
        _input.textComponent = textComp;
        _input.placeholder = phText;
        _input.contentType = InputField.ContentType.Standard;
        _input.lineType = InputField.LineType.SingleLine;
        _input.characterLimit = 16;

        // 底部按钮行：横向两个 Button 各 140x42，Anchor 底部居中
        var btnRowGO = new GameObject("ButtonRow");
        btnRowGO.transform.SetParent(panelGO.transform, false);
        var btnRowRT = btnRowGO.AddComponent<RectTransform>();
        btnRowRT.anchorMin = new Vector2(0f, 0f);
        btnRowRT.anchorMax = new Vector2(1f, 0f);
        btnRowRT.pivot = new Vector2(0.5f, 0f);
        btnRowRT.anchoredPosition = new Vector2(0f, 18f);
        btnRowRT.sizeDelta = new Vector2(0f, 52f);

        // 确定 绿 0.2,0.6,0.35
        var okGO = new GameObject("OkBtn");
        okGO.transform.SetParent(btnRowGO.transform, false);
        var okImg = okGO.AddComponent<Image>();
        okImg.color = new Color(0.2f, 0.6f, 0.35f, 1f);
        _okBtn = okGO.AddComponent<Button>();
        var okRT = okImg.rectTransform;
        okRT.anchorMin = new Vector2(0.5f, 0.5f);
        okRT.anchorMax = new Vector2(0.5f, 0.5f);
        okRT.pivot = new Vector2(0.5f, 0.5f);
        okRT.sizeDelta = new Vector2(140f, 42f);
        okRT.anchoredPosition = new Vector2(-90f, 0f);
        var okTxtGO = new GameObject("Text");
        okTxtGO.transform.SetParent(okGO.transform, false);
        var okTxt = okTxtGO.AddComponent<Text>();
        okTxt.text = "确定";
        okTxt.alignment = TextAnchor.MiddleCenter;
        okTxt.color = Color.white;
        okTxt.fontSize = 18;
        okTxt.fontStyle = FontStyle.Bold;
        ApplyFont(okTxt);
        var okTxtRT = okTxt.rectTransform;
        okTxtRT.anchorMin = Vector2.zero;
        okTxtRT.anchorMax = Vector2.one;
        okTxtRT.offsetMin = Vector2.zero;
        okTxtRT.offsetMax = Vector2.zero;
        _okBtn.onClick.AddListener(new Action(() => OnConfirm()));

        // 取消 灰 0.5,0.5,0.5
        var cancelGO = new GameObject("CancelBtn");
        cancelGO.transform.SetParent(btnRowGO.transform, false);
        var cancelImg = cancelGO.AddComponent<Image>();
        cancelImg.color = new Color(0.5f, 0.5f, 0.5f, 1f);
        _cancelBtn = cancelGO.AddComponent<Button>();
        var cancelRT = cancelImg.rectTransform;
        cancelRT.anchorMin = new Vector2(0.5f, 0.5f);
        cancelRT.anchorMax = new Vector2(0.5f, 0.5f);
        cancelRT.pivot = new Vector2(0.5f, 0.5f);
        cancelRT.sizeDelta = new Vector2(140f, 42f);
        cancelRT.anchoredPosition = new Vector2(90f, 0f);
        var cancelTxtGO = new GameObject("Text");
        cancelTxtGO.transform.SetParent(cancelGO.transform, false);
        var cancelTxt = cancelTxtGO.AddComponent<Text>();
        cancelTxt.text = "取消";
        cancelTxt.alignment = TextAnchor.MiddleCenter;
        cancelTxt.color = Color.white;
        cancelTxt.fontSize = 18;
        ApplyFont(cancelTxt);
        var cancelTxtRT = cancelTxt.rectTransform;
        cancelTxtRT.anchorMin = Vector2.zero;
        cancelTxtRT.anchorMax = Vector2.one;
        cancelTxtRT.offsetMin = Vector2.zero;
        cancelTxtRT.offsetMax = Vector2.zero;
        _cancelBtn.onClick.AddListener(new Action(() => Close()));

        _canvasGO.SetActive(false);
    }

    private void OnConfirm()
    {
        string name = (_input != null ? _input.text : string.Empty);
        try { name = name.Trim(); } catch { name = string.Empty; }
        if (string.IsNullOrEmpty(name))
        {
            ShowBubble("名称不能为空");
            return;
        }
        if (name.Length > 16) name = name.Substring(0, 16);
        try
        {
            if (_targetConsole != null)
            {
                TeleportStationNameManager.SetName(_targetConsole, name);
                // v0.9.61 改名即时同步已建标记文本/存量表（根因①）
                try { TeleportMapManager.NotifyRenamed(_targetConsole, name); } catch {}
            }
        }
        catch (Exception ex)
        {
            try { Plugin.L?.LogWarning($"[TS][RenameUI] SetName 异常: {ex.Message}"); } catch {}
        }
        ShowBubble($"已命名: {name}");
        Close();
    }

    private void ApplyFont(Text txt)
    {
        try
        {
            var arial = Resources.GetBuiltinResource<Font>("Arial.ttf");
            if (arial != null) { txt.font = arial; return; }
        }
        catch {}
        try
        {
            var fonts = Resources.FindObjectsOfTypeAll<Font>();
            if (fonts != null && fonts.Length > 0 && fonts[0] != null) txt.font = fonts[0];
        }
        catch {}
    }

    private static void ShowBubble(string msg)
    {
        try
        {
            var t = HarmonyLib.AccessTools.TypeByName("BasicCharacterController");
            var m = t?.GetMethod("ShowDialogueBubble", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            var gc = GameController.instance;
            object player = null;
            if (gc != null) player = gc.playerCharacter;
            if (player is Component comp && m != null) m.Invoke(comp, new object[] { msg, 4f });
        }
        catch {}
    }
}
