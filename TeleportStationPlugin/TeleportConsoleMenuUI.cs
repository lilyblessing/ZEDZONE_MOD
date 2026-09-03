using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using Il2CppInterop.Runtime.Attributes;

namespace TeleportStationPlugin;

/// <summary>
/// A方案自建面板栈三项菜单：重命名 / 选择传送目的地(站列表) / 退出（不碰DOS单例，不调OpenDOSPanel/QuitDOS）
/// 接管 900101 (克隆自 108 Furniture_Commu) 的原生雇佣交互（按F）改为传送专用菜单。
/// 另提供靠近提示“按 [F] 打开传送控制台”。
/// </summary>
public class TeleportConsoleMenuUI : MonoBehaviour
{
    private static Type _mapPanelType;
    private static Type _pdaPanelType;
    private static bool _typeCacheInit;
        private static bool _menuCacheDone = false;
    private static Type SafeTypeByName2(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        try { var asm = typeof(TerrainObject).Assembly; var t = asm.GetType(name); if (t!=null) return t; } catch {}
        try { foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) { if (a.FullName.StartsWith("UnityEngine.")) continue; try { var t=a.GetType(name); if(t!=null) return t; } catch {} } } catch {}
        return null;
    }
    private static void EnsureTypeCache() { if(_menuCacheDone) return; _menuCacheDone=true; try{_mapPanelType=SafeTypeByName2("MapPanel");}catch{} try{_pdaPanelType=SafeTypeByName2("PDAPanel");}catch{} }

    public static TeleportConsoleMenuUI Instance { get; private set; }

    private GameObject _canvasGO;
    private GameObject _promptGO;
    private Text _promptText;
    private TerrainObject _currentConsole;
    private bool _isOpen = false;
    private float _openTime = -999f;
    private float _nextPromptCheck = -1f;
    private TerrainObject _nearestConsole = null;

    public bool IsOpen => _isOpen;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
        UnityEngine.Object.DontDestroyOnLoad(gameObject);
    }
    void OnDestroy() { if (Instance == this) Instance = null; }

    public static TeleportConsoleMenuUI EnsureExists()
    {
        if (Instance != null) return Instance;
        var go = new GameObject("TeleportConsoleMenuUI");
        UnityEngine.Object.DontDestroyOnLoad(go);
        return go.AddComponent<TeleportConsoleMenuUI>();
    }

    private void EnsureUI()
    {
        if (_canvasGO != null) return;
        _canvasGO = new GameObject("TSConsoleMenuCanvas");
        UnityEngine.Object.DontDestroyOnLoad(_canvasGO);
        var canvas = _canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 10002;
        var scaler = _canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        _canvasGO.AddComponent<GraphicRaycaster>();

        var bgGO = new GameObject("BG");
        bgGO.transform.SetParent(_canvasGO.transform, false);
        var bgImg = bgGO.AddComponent<Image>();
        bgImg.color = new Color(0f, 0f, 0f, 0.55f);
        var bgRT = bgImg.rectTransform;
        bgRT.anchorMin = Vector2.zero; bgRT.anchorMax = Vector2.one;
        bgRT.offsetMin = Vector2.zero; bgRT.offsetMax = Vector2.zero;
        var bgBtn = bgGO.AddComponent<Button>();
        bgBtn.onClick.AddListener(new Action(() => Close()));

        var panelGO = new GameObject("Panel");
        panelGO.transform.SetParent(_canvasGO.transform, false);
        var panelImg = panelGO.AddComponent<Image>();
        panelImg.color = new Color(0.13f, 0.13f, 0.15f, 0.96f);
        var prt = panelImg.rectTransform;
        prt.anchorMin = new Vector2(0.5f, 0.5f); prt.anchorMax = new Vector2(0.5f, 0.5f);
        prt.pivot = new Vector2(0.5f, 0.5f);
        prt.anchoredPosition = Vector2.zero;
        prt.sizeDelta = new Vector2(520f, 380f);

        var titleGO = new GameObject("Title");
        titleGO.transform.SetParent(panelGO.transform, false);
        var title = titleGO.AddComponent<Text>();
        title.alignment = TextAnchor.MiddleCenter;
        title.color = Color.white;
        title.fontSize = 24;
        title.fontStyle = FontStyle.Bold;
        title.text = "传送站控制台";
        ApplyFont(title);
        var trt = title.rectTransform;
        trt.anchorMin = new Vector2(0f, 1f); trt.anchorMax = new Vector2(1f, 1f);
        trt.pivot = new Vector2(0.5f, 1f);
        trt.anchoredPosition = new Vector2(0f, -18f);
        trt.sizeDelta = new Vector2(0f, 36f);

        // Subtitle shows current name / selected
        var subGO = new GameObject("Subtitle");
        subGO.transform.SetParent(panelGO.transform, false);
        var sub = subGO.AddComponent<Text>();
        sub.alignment = TextAnchor.MiddleCenter;
        sub.color = new Color(0.8f, 0.85f, 0.9f, 1f);
        sub.fontSize = 14;
        sub.text = "";
        ApplyFont(sub);
        var srt = sub.rectTransform;
        srt.anchorMin = new Vector2(0f, 1f); srt.anchorMax = new Vector2(1f, 1f);
        srt.pivot = new Vector2(0.5f, 1f);
        srt.anchoredPosition = new Vector2(0f, -54f);
        srt.sizeDelta = new Vector2(0f, 20f);
        sub.gameObject.name = "SubtitleText";

        float y = -90f;
        CreateMenuButton(panelGO.transform, "重命名传送站", new Vector2(0f, y), () =>
        {
            var c = _currentConsole;
            Close();
            try { TeleportStationRenameUI.EnsureExists().Show(c); } catch (Exception e) { Plugin.L.LogWarning($"[TS][Menu] Rename fail {e.Message.Split('\n')[0]}"); }
        });
        y -= 62f;
        CreateMenuButton(panelGO.transform, "选择传送目的地", new Vector2(0f, y), () =>
        {
            var c = _currentConsole;
            Close();
            if (c == null) return;
            try
            {
                TeleportConsoleUI.EnsureExists().ShowForConsole(c);
            } catch (Exception e) { Plugin.L.LogWarning($"[TS][Menu] List fail {e.Message.Split('\n')[0]}"); }
        });
        y -= 62f;
        CreateMenuButton(panelGO.transform, "退出", new Vector2(0f, y), () => Close(), new Color(0.55f, 0.22f, 0.22f, 1f));

        _canvasGO.SetActive(false);

        // Prompt canvas (small bottom hint)
        _promptGO = new GameObject("TSConsolePrompt");
        UnityEngine.Object.DontDestroyOnLoad(_promptGO);
        var pCanvas = _promptGO.AddComponent<Canvas>();
        pCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        pCanvas.sortingOrder = 9999;
        _promptGO.AddComponent<GraphicRaycaster>();
        var pScaler = _promptGO.AddComponent<CanvasScaler>();
        pScaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        pScaler.referenceResolution = new Vector2(1920f, 1080f);
        var txtGO = new GameObject("PromptText");
        txtGO.transform.SetParent(_promptGO.transform, false);
        _promptText = txtGO.AddComponent<Text>();
        _promptText.alignment = TextAnchor.MiddleCenter;
        _promptText.color = Color.white;
        _promptText.fontSize = 20;
        _promptText.fontStyle = FontStyle.Bold;
        _promptText.text = "按 [F] 打开传送控制台";
        ApplyFont(_promptText);
        var outline = txtGO.AddComponent<Outline>();
        outline.effectColor = new Color(0f, 0f, 0f, 0.9f);
        outline.effectDistance = new Vector2(2f, -2f);
        var pr = _promptText.rectTransform;
        pr.anchorMin = new Vector2(0.5f, 0f); pr.anchorMax = new Vector2(0.5f, 0f);
        pr.pivot = new Vector2(0.5f, 0f);
        pr.anchoredPosition = new Vector2(0f, 110f);
        pr.sizeDelta = new Vector2(600f, 32f);
        _promptGO.SetActive(false);
    }

    private void CreateMenuButton(Transform parent, string label, Vector2 anchored, Action onClick, Color? col = null)
    {
        var go = new GameObject("Btn_" + label);
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.color = col ?? new Color(0.22f, 0.42f, 0.75f, 1f);
        var btn = go.AddComponent<Button>();
        var rt = img.rectTransform;
        rt.anchorMin = new Vector2(0.5f, 1f); rt.anchorMax = new Vector2(0.5f, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.anchoredPosition = anchored;
        rt.sizeDelta = new Vector2(420f, 48f);
        var txtGO = new GameObject("Text");
        txtGO.transform.SetParent(go.transform, false);
        var txt = txtGO.AddComponent<Text>();
        txt.alignment = TextAnchor.MiddleCenter;
        txt.color = Color.white;
        txt.fontSize = 18;
        txt.text = label;
        ApplyFont(txt);
        var trt = txt.rectTransform;
        trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
        trt.offsetMin = Vector2.zero; trt.offsetMax = Vector2.zero;
        if (onClick != null) btn.onClick.AddListener(onClick);
    }

    private void ApplyFont(Text txt)
    {
        try { var arial = Resources.GetBuiltinResource<Font>("Arial.ttf"); if (arial != null) { txt.font = arial; return; } } catch {}
        try { var fonts = Resources.FindObjectsOfTypeAll<Font>(); if (fonts != null && fonts.Length > 0 && fonts[0] != null) txt.font = fonts[0]; } catch {}
    }

    [HideFromIl2Cpp]
    public void ShowForConsole(TerrainObject console)
    {
        if (console == null) return;
        _currentConsole = console;
        EnsureUI();
        // update subtitle with current name / selected
        try
        {
            var trans = _canvasGO.transform.Find("Panel/SubtitleText");
            if (trans != null)
            {
                var txt = trans.GetComponent<Text>();
                if (txt != null)
                {
                    string name = TeleportStationNameManager.GetName(console);
                    long ck = GetInstanceKey(console);
                    long sel = TeleportConsoleSelection.GetSelectedKey(ck);
                    string selName = "";
                    if (sel != 0)
                    {
                        var pad = FindByKey(sel) as TerrainObject;
                        if (pad != null) selName = TeleportStationNameManager.GetName(pad) ?? pad.name;
                        else selName = sel.ToString();
                    }
                    else selName = "未选择";
                    string display = string.IsNullOrWhiteSpace(name) ? console.name : name;
                    txt.text = $"{display}  |  目的地: {selName}";
                }
            }
        } catch {}
        _canvasGO.SetActive(true);
        _isOpen = true;
        _openTime = Time.unscaledTime;
        if (_promptGO != null) _promptGO.SetActive(false);
        Plugin.L.LogInfo($"[TS][Menu] 打开控制台菜单 console={GetInstanceKey(console)}");
    }

    public void Close()
    {
        if (_canvasGO != null) _canvasGO.SetActive(false);
        _isOpen = false;
        _currentConsole = null;
    }

    void Update()
    {
        try
        {
            EnsureTypeCache();
            EnsureUI();
            // Menu ESC handling
            if (_isOpen)
            {
                if (Time.unscaledTime - _openTime > 0.25f && Input.GetKeyDown(KeyCode.Escape))
                {
                    Close();
                    return;
                }
                if (Time.unscaledTime - _openTime > 0.25f && Input.GetKeyDown(KeyCode.F))
                {
                    // F again closes
                    Close();
                    return;
                }
                if (_promptGO != null && _promptGO.activeSelf) _promptGO.SetActive(false);
                return;
            }

            // P6.9 prompt disabled - native InteractManager handles external F, MenuUI only for internal delegate
            if (_promptGO != null && _promptGO.activeSelf) _promptGO.SetActive(false);
            return;
        } catch (Exception e) { Plugin.L.LogWarning($"[TS][Menu] Update 异常: {e.Message.Split('\n')[0]}"); }
    }

    private bool IsMapOpen()
    {
        try
        {
            EnsureTypeCache();
            var t = _mapPanelType;
            if (t == null) return false;
            var prop = t.GetProperty("instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var mp = prop?.GetValue(null) as Component;
            if (mp == null) return false;
            if (!mp.gameObject.activeInHierarchy) return false;
            // also check PDA currentPanelName contains Map
            try
            {
                var pdaType = _pdaPanelType;
                var instProp = pdaType?.GetProperty("instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                var inst = instProp?.GetValue(null);
                if (inst != null)
                {
                    var name = Reflect.Get(inst, "currentPanelName") as string;
                    if (!string.IsNullOrEmpty(name)) return name.IndexOf("Map", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            } catch {}
            return true;
        } catch { return false; }
    }

    private TerrainObject FindNearestConsole(float maxDist)
    {
        try
        {
            var playerTr = GetPlayerTransform();
            if (playerTr == null) return null;
            float best = maxDist * maxDist;
            TerrainObject bestObj = null;
            var list = TeleportObjectCache.FindAllById(900101);
            foreach (var t in list)
            {
                if (t == null || t.transform == null) continue;
                var d = t.transform.position - playerTr.position;
                float d2 = d.x * d.x + d.y * d.y;
                if (d2 < best) { best = d2; bestObj = t; }
            }
            return bestObj;
        } catch { return null; }
    }

    private Transform GetPlayerTransform()
    {
        try
        {
            var gc = GameController.instance;
            if (gc != null)
            {
                var p = gc.playerCharacter as Component;
                if (p != null) return p.transform;
                var pp = Reflect.Get(gc, "player") as Component;
                if (pp != null) return pp.transform;
            }
            var go = GameObject.FindWithTag("Player");
            if (go != null) return go.transform;
        } catch {}
        return null;
    }

    private static long GetInstanceKey(TerrainObject t)
    {
        try { return (long)t.GetInstanceID(); } catch { try { return (long)t.Pointer; } catch { return t.GetHashCode(); } }
    }

    private static TerrainObject FindByKey(long key)
    {
        try
        {
            var all = Resources.FindObjectsOfTypeAll<TerrainObject>();
            if (all != null) foreach (var t in all) if (t != null && GetInstanceKey(t) == key) return t;
            var f = typeof(ChargerPadFix).GetField("_knownClones", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var list = f?.GetValue(null) as System.Collections.Generic.List<object>;
            if (list != null) foreach (var o in list) { var c = o as Component; if (c == null) continue; var tr = c.transform; int d = 0; Component found = null; while (tr != null && d++ < 8) { foreach (var comp in tr.GetComponents<Component>()) if (comp != null && comp.GetType().Name.Contains("TerrainObject")) { found = comp; break; } if (found != null) break; tr = tr.parent; } var tt = found as TerrainObject; if (tt != null && GetInstanceKey(tt) == key) return tt; }
        } catch {}
        return null;
    }
}
