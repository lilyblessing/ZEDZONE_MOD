using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Il2CppInterop.Runtime.Attributes;

namespace TeleportStationPlugin;

/// <summary>
/// P6 控制台选目的地 UI：全屏半透底 + 居中面板 + 滚动列表。
/// 已上线=亮白可点，离线=灰不可点。点击后写入 TeleportConsoleSelection 并气泡提示。
/// </summary>
public class TeleportConsoleUI : MonoBehaviour
{
    public static TeleportConsoleUI Instance { get; private set; }

    private GameObject _canvasGO;
    private GameObject _panelGO;
    private Transform _contentTr;
    private Text _titleText;
    private TerrainObject _currentConsole;
    private bool _isOpen = false;
    private float _openTime = -999f;

    public bool IsOpen => _isOpen;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
    }
    void OnDestroy() { if (Instance == this) Instance = null; }

    public static TeleportConsoleUI EnsureExists()
    {
        if (Instance != null) return Instance;
        var go = new GameObject("TeleportConsoleUI");
        UnityEngine.Object.DontDestroyOnLoad(go);
        return go.AddComponent<TeleportConsoleUI>();
    }

    private void EnsureUI()
    {
        if (_canvasGO != null) return;
        _canvasGO = new GameObject("TeleportConsoleCanvas");
        UnityEngine.Object.DontDestroyOnLoad(_canvasGO);
        var canvas = _canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 10000;
        var scaler = _canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        _canvasGO.AddComponent<GraphicRaycaster>();

        var bgGO = new GameObject("BG");
        bgGO.transform.SetParent(_canvasGO.transform, false);
        var bgImg = bgGO.AddComponent<Image>();
        bgImg.color = new Color(0f, 0f, 0f, 0.65f);
        var bgRT = bgImg.rectTransform;
        bgRT.anchorMin = Vector2.zero; bgRT.anchorMax = Vector2.one;
        bgRT.offsetMin = Vector2.zero; bgRT.offsetMax = Vector2.zero;
        // 点击背景关闭
        var bgBtn = bgGO.AddComponent<Button>();
        bgBtn.onClick.AddListener(new Action(() => Close()));

        _panelGO = new GameObject("Panel");
        _panelGO.transform.SetParent(_canvasGO.transform, false);
        var panelImg = _panelGO.AddComponent<Image>();
        panelImg.color = new Color(0.12f, 0.12f, 0.14f, 0.95f);
        var prt = panelImg.rectTransform;
        prt.anchorMin = new Vector2(0.5f, 0.5f); prt.anchorMax = new Vector2(0.5f, 0.5f);
        prt.pivot = new Vector2(0.5f, 0.5f);
        prt.anchoredPosition = Vector2.zero;
        prt.sizeDelta = new Vector2(900f, 620f);

        // Title
        var titleGO = new GameObject("Title");
        titleGO.transform.SetParent(_panelGO.transform, false);
        _titleText = titleGO.AddComponent<Text>();
        _titleText.alignment = TextAnchor.MiddleCenter;
        _titleText.color = Color.white;
        _titleText.fontSize = 28;
        _titleText.fontStyle = FontStyle.Bold;
        _titleText.text = "选择传送目的地";
        var trt = _titleText.rectTransform;
        trt.anchorMin = new Vector2(0f, 1f); trt.anchorMax = new Vector2(1f, 1f);
        trt.pivot = new Vector2(0.5f, 1f);
        trt.anchoredPosition = new Vector2(0f, -18f);
        trt.sizeDelta = new Vector2(0f, 40f);
        ApplyFont(_titleText);

        // Scroll
        var scrollGO = new GameObject("Scroll");
        scrollGO.transform.SetParent(_panelGO.transform, false);
        var scrollRT = scrollGO.AddComponent<RectTransform>();
        scrollRT.anchorMin = new Vector2(0f, 0f); scrollRT.anchorMax = new Vector2(1f, 1f);
        scrollRT.offsetMin = new Vector2(16f, 16f); scrollRT.offsetMax = new Vector2(-16f, -60f);
        var maskImg = scrollGO.AddComponent<Image>();
        maskImg.color = new Color(0, 0, 0, 0);
        var mask = scrollGO.AddComponent<Mask>();
        mask.showMaskGraphic = false;
        var scroll = scrollGO.AddComponent<ScrollRect>();
        scroll.horizontal = false; scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;

        var viewportGO = new GameObject("Viewport");
        viewportGO.transform.SetParent(scrollGO.transform, false);
        var vpRT = viewportGO.AddComponent<RectTransform>();
        vpRT.anchorMin = Vector2.zero; vpRT.anchorMax = Vector2.one;
        vpRT.offsetMin = Vector2.zero; vpRT.offsetMax = Vector2.zero;
        viewportGO.AddComponent<Image>().color = new Color(0, 0, 0, 0);
        viewportGO.AddComponent<Mask>().showMaskGraphic = false;

        var contentGO = new GameObject("Content");
        _contentTr = contentGO.transform;
        _contentTr.SetParent(viewportGO.transform, false);
        var contentRT = contentGO.AddComponent<RectTransform>();
        contentRT.anchorMin = new Vector2(0f, 1f); contentRT.anchorMax = new Vector2(1f, 1f);
        contentRT.pivot = new Vector2(0.5f, 1f);
        contentRT.anchoredPosition = Vector2.zero;
        contentRT.sizeDelta = new Vector2(0f, 0f);
        var vlg = contentGO.AddComponent<VerticalLayoutGroup>();
        vlg.spacing = 10f; vlg.padding = new RectOffset(8, 8, 8, 8);
        vlg.childControlHeight = false; vlg.childControlWidth = true;
        vlg.childForceExpandHeight = false; vlg.childForceExpandWidth = true;
        var fitter = contentGO.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

        scroll.viewport = vpRT;
        scroll.content = contentRT;

        // Close button
        var closeGO = new GameObject("CloseBtn");
        closeGO.transform.SetParent(_panelGO.transform, false);
        var closeImg = closeGO.AddComponent<Image>();
        closeImg.color = new Color(0.75f, 0.25f, 0.25f, 1f);
        var closeBtn = closeGO.AddComponent<Button>();
        var closeRT = closeImg.rectTransform;
        closeRT.anchorMin = new Vector2(1f, 1f); closeRT.anchorMax = new Vector2(1f, 1f);
        closeRT.pivot = new Vector2(1f, 1f);
        closeRT.anchoredPosition = new Vector2(-12f, -12f);
        closeRT.sizeDelta = new Vector2(90f, 36f);
        closeBtn.onClick.AddListener(new Action(() => Close()));
        var closeTxtGO = new GameObject("Text");
        closeTxtGO.transform.SetParent(closeGO.transform, false);
        var closeTxt = closeTxtGO.AddComponent<Text>();
        closeTxt.alignment = TextAnchor.MiddleCenter;
        closeTxt.color = Color.white;
        closeTxt.fontSize = 18;
        closeTxt.text = "关闭";
        ApplyFont(closeTxt);
        var ctRT = closeTxt.rectTransform;
        ctRT.anchorMin = Vector2.zero; ctRT.anchorMax = Vector2.one;
        ctRT.offsetMin = Vector2.zero; ctRT.offsetMax = Vector2.zero;

        _canvasGO.SetActive(false);
    }

    private void ApplyFont(Text txt)
    {
        try
        {
            var arial = Resources.GetBuiltinResource<Font>("Arial.ttf");
            if (arial != null) { txt.font = arial; return; }
        } catch {}
        try
        {
            var fonts = Resources.FindObjectsOfTypeAll<Font>();
            if (fonts != null && fonts.Length > 0 && fonts[0] != null) txt.font = fonts[0];
        } catch {}
    }

    [HideFromIl2Cpp]
    public void ShowForConsole(TerrainObject console)
    {
        if (console == null) return;
        _currentConsole = console;
        EnsureUI();
        RebuildList();
        _canvasGO.SetActive(true);
        _isOpen = true;
        _openTime = Time.unscaledTime;
        Plugin.L.LogInfo($"[TS][UI] 打开选点面板 console={GetInstanceKey(console)}");
    }

    public void Close()
    {
        if (_canvasGO != null) _canvasGO.SetActive(false);
        _isOpen = false;
        _currentConsole = null;
    }

    void Update()
    {
        if (!_isOpen) return;
        try
        {
            if (Time.unscaledTime - _openTime < 0.3f) return; // 防刚打开就 ESC 误关
            if (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.F10))
            {
                Close();
            }
        } catch {}
    }

    private void RebuildList()
    {
        try
        {
            // 清旧
            if (_contentTr != null)
            {
                for (int i = _contentTr.childCount - 1; i >= 0; i--)
                {
                    var ch = _contentTr.GetChild(i);
                    if (ch != null) UnityEngine.Object.Destroy(ch.gameObject);
                }
            }
            if (_titleText != null)
            {
                long ck = _currentConsole != null ? GetInstanceKey(_currentConsole) : 0;
                long selected = ck != 0 ? TeleportConsoleSelection.GetSelectedKey(ck) : 0;
                string selInfo = selected != 0 ? $"（已选 {selected}）" : "（未选择）";
                _titleText.text = $"选择传送目的地 {selInfo}";
            }

            var candidates = CollectCandidates();
            if (candidates.Count == 0)
            {
                CreateInfoRow("无可用传送台（请先放置并绑定圆盘）");
                return;
            }
            foreach (var pad in candidates)
            {
                bool online = TeleportConsoleSelection.IsOnline(pad);
                long pk = GetInstanceKey(pad);
                long ck = _currentConsole != null ? GetInstanceKey(_currentConsole) : 0;
                long boundPad = TeleportBindingManager.GetBoundPad(ck);
                bool isSelfPad = pk == boundPad;
                string suffix = isSelfPad ? " [本站]" : "";
                string status = online ? "在线" : "离线";
                string label = $"{pad.name} {status}{suffix}  id={pad.attr.id} pos={pad.transform.position.x:F0},{pad.transform.position.y:F0}";
                if (online && !isSelfPad) label += " ★可传送";
                else if (!online) label += " （不可选）";

                var btn = CreateRowButton(label, online && !isSelfPad, () =>
                {
                    if (_currentConsole == null) return;
                    // 仅在线非本站可点，按钮已拦截，但双保险
                    if (!TeleportConsoleSelection.IsOnline(pad)) { ShowBubble("目的地离线"); return; }
                    if (isSelfPad) { ShowBubble("不能选择本站"); return; }
                    TeleportConsoleSelection.SetSelected(_currentConsole, pad);
                    ShowBubble($"已选择 {pad.name}");
                    Close();
                });
                // 已选中的高亮
                long sel = ck != 0 ? TeleportConsoleSelection.GetSelectedKey(ck) : 0;
                if (sel == pk)
                {
                    var img = btn.GetComponent<Image>();
                    if (img != null) img.color = new Color(0.2f, 0.55f, 0.85f, 1f);
                }
            }
            // 底部 清除选择 按钮
            CreateClearRow();
        }
        catch (Exception e) { Plugin.L.LogWarning($"[TS][UI] 重建列表异常: {e.Message}"); }
    }

    private List<TerrainObject> CollectCandidates()
    {
        var list = new List<TerrainObject>();
        var seen = new HashSet<long>();
        try
        {
            var all = FindAllPads();
            // 按距离当前控制台排序
            Vector3 cPos = _currentConsole != null ? _currentConsole.transform.position : Vector3.zero;
            all.Sort((a,b) =>
            {
                if (a == null || b == null) return 0;
                float da = (a.transform.position - cPos).sqrMagnitude;
                float db = (b.transform.position - cPos).sqrMagnitude;
                return da.CompareTo(db);
            });
            foreach (var pad in all)
            {
                if (pad == null || pad.transform == null) continue;
                long k = GetInstanceKey(pad);
                if (seen.Add(k)) list.Add(pad);
            }
        } catch {}
        return list;
    }

    private GameObject CreateRowButton(string label, bool interactable, Action onClick)
    {
        var go = new GameObject("Row");
        go.transform.SetParent(_contentTr, false);
        var img = go.AddComponent<Image>();
        img.color = interactable ? new Color(0.22f, 0.22f, 0.26f, 1f) : new Color(0.16f, 0.16f, 0.16f, 1f);
        var btn = go.AddComponent<Button>();
        btn.interactable = interactable;
        var rt = img.rectTransform;
        rt.sizeDelta = new Vector2(0f, 48f);
        var le = go.AddComponent<LayoutElement>();
        le.preferredHeight = 48f; le.flexibleWidth = 1f;

        // 文字
        var txtGO = new GameObject("Label");
        txtGO.transform.SetParent(go.transform, false);
        var txt = txtGO.AddComponent<Text>();
        txt.alignment = TextAnchor.MiddleLeft;
        txt.color = interactable ? Color.white : new Color(0.55f, 0.55f, 0.55f, 1f);
        txt.fontSize = 18;
        txt.text = label;
        ApplyFont(txt);
        var trt = txt.rectTransform;
        trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
        trt.offsetMin = new Vector2(14f, 0f); trt.offsetMax = new Vector2(-14f, 0f);

        if (interactable && onClick != null)
        {
            btn.onClick.AddListener(onClick);
        }
        return go;
    }

    private void CreateInfoRow(string msg)
    {
        var go = new GameObject("Info");
        go.transform.SetParent(_contentTr, false);
        var img = go.AddComponent<Image>();
        img.color = new Color(0.18f, 0.18f, 0.20f, 1f);
        var rt = img.rectTransform; rt.sizeDelta = new Vector2(0f, 48f);
        var le = go.AddComponent<LayoutElement>(); le.preferredHeight = 48f; le.flexibleWidth = 1f;
        var txtGO = new GameObject("Label");
        txtGO.transform.SetParent(go.transform, false);
        var txt = txtGO.AddComponent<Text>();
        txt.alignment = TextAnchor.MiddleCenter;
        txt.color = new Color(0.8f, 0.8f, 0.7f, 1f);
        txt.fontSize = 18; txt.text = msg;
        ApplyFont(txt);
        var trt = txt.rectTransform; trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
        trt.offsetMin = Vector2.zero; trt.offsetMax = Vector2.zero;
    }

    private void CreateClearRow()
    {
        var go = new GameObject("ClearRow");
        go.transform.SetParent(_contentTr, false);
        var img = go.AddComponent<Image>();
        img.color = new Color(0.45f, 0.22f, 0.22f, 1f);
        var btn = go.AddComponent<Button>();
        var rt = img.rectTransform; rt.sizeDelta = new Vector2(0f, 42f);
        var le = go.AddComponent<LayoutElement>(); le.preferredHeight = 42f; le.flexibleWidth = 1f;
        var txtGO = new GameObject("Label");
        txtGO.transform.SetParent(go.transform, false);
        var txt = txtGO.AddComponent<Text>();
        txt.alignment = TextAnchor.MiddleCenter;
        txt.color = Color.white; txt.fontSize = 18; txt.text = "清除选择";
        ApplyFont(txt);
        var trt = txt.rectTransform; trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
        trt.offsetMin = Vector2.zero; trt.offsetMax = Vector2.zero;
        btn.onClick.AddListener(new Action(() =>
        {
            if (_currentConsole != null)
            {
                TeleportConsoleSelection.Clear(_currentConsole);
                ShowBubble("已清除选择");
                Close();
            }
        }));
    }

    // ===== 工具 =====
    private static long GetInstanceKey(TerrainObject t)
    {
        try { return (long)t.GetInstanceID(); } catch { try { return (long)t.Pointer; } catch { return t.GetHashCode(); } }
    }

    private static List<TerrainObject> FindAllPads()
    {
        var list = new List<TerrainObject>();
        var seen = new HashSet<long>();
        try
        {
            var all = UnityEngine.Resources.FindObjectsOfTypeAll<TerrainObject>();
            if (all != null) foreach (var t in all) if (t!=null && t.attr!=null && t.attr.id==900102){ long k=GetInstanceKey(t); if(seen.Add(k)) list.Add(t); }
        } catch {}
        try
        {
            var f = typeof(ChargerPadFix).GetField("_knownClones", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var clones = f?.GetValue(null) as List<object>;
            if (clones != null) foreach (var o in clones){ var c=o as Component; if(c==null) continue; var t2 = FindTerrainObject(c.transform) as TerrainObject; if(t2!=null && t2.attr!=null && t2.attr.id==900102){ long k=GetInstanceKey(t2); if(seen.Add(k)) list.Add(t2); } }
        } catch {}
        return list;
    }

    private static Component FindTerrainObject(Transform tr)
    {
        int d=0; while(tr!=null && d++<16){ foreach(var c in tr.GetComponents<Component>()) if(c!=null && c.GetType().Name.Contains("TerrainObject")) return c; tr=tr.parent; }
        return null;
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
            if (player is Component comp && m != null) m.Invoke(comp, new object[]{ msg, 4f });
        } catch {}
    }
}
