using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Il2CppInterop.Runtime.Attributes;

namespace TeleportStationPlugin;

/// <summary>
/// P6 控制台选目的地 UI：全屏半透底 + 居中面板 + 滚动列表。
/// v0.9.64：目的地无在线门控（在线态仅显示：在线/上次在线/状态未知，全行可点即选）；
/// 候选仅限已配对（活体绑定或坐标对回链成功），未配对 console/孤 pad 不进列表。
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
        // v0.9.62 可见性修复：双 legacy Mask（stencil）把 Content 子树行像素整体裁掉
        // （行对象存在且父链正确、未被遮罩的标题/关闭按钮均可见、连静态清除行同样不可见）。
        // 换 RectMask2D（纯矩形裁剪，无 stencil 依赖；视口无旋转，视觉等价）。
        scrollGO.AddComponent<RectMask2D>();
        var scroll = scrollGO.AddComponent<ScrollRect>();
        scroll.horizontal = false; scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;

        var viewportGO = new GameObject("Viewport");
        viewportGO.transform.SetParent(scrollGO.transform, false);
        var vpRT = viewportGO.AddComponent<RectTransform>();
        vpRT.anchorMin = Vector2.zero; vpRT.anchorMax = Vector2.one;
        vpRT.offsetMin = Vector2.zero; vpRT.offsetMax = Vector2.zero;
        viewportGO.AddComponent<Image>().color = new Color(0, 0, 0, 0);
        viewportGO.AddComponent<RectMask2D>(); // v0.9.62 同上：Mask→RectMask2D

        var contentGO = new GameObject("Content");
        // v0.9.61 修空列表：先加 RectTransform（会替换掉默认 Transform），再缓存 _contentTr。
        // 旧顺序先缓存后替换，_contentTr 成 stale 引用(fake-null)，行 SetParent 到空→场景根，
        // 列表恒空且全程无异常（与“候选俱全、无重建异常、界面空”逐项吻合）。
        var contentRT = contentGO.AddComponent<RectTransform>();
        _contentTr = contentGO.transform;
        _contentTr.SetParent(viewportGO.transform, false);
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
        // v0.9.63 active=False 根因修复：行级诊断采 activeInHierarchy，而旧顺序先 Rebuild 后 SetActive(true)，
        // 采样时整棵 Canvas 尚未激活 → 所有行 active=False（用户仍可点选成功即实证：激活发生在采样之后）。
        // 现先激活 Canvas 再重建列表，诊断同时记录 activeSelf/activeInHierarchy。
        _canvasGO.SetActive(true);
        RebuildList();
        _isOpen = true;
        _openTime = Time.unscaledTime;
        string cuid = TeleportStationUid.UidFor(console);
        Plugin.L.LogInfo($"[TS][UI] 打开选点面板 {cuid}({TeleportStationUid.DisplayForConsole(console)})");
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
            if (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.F) || Input.GetKeyDown(KeyCode.F10))
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
            string consoleUid0 = _currentConsole != null ? TeleportStationUid.UidFor(_currentConsole) : "";
            string consoleDisp0 = _currentConsole != null ? TeleportStationUid.DisplayForConsole(_currentConsole) : "?";
            if (_titleText != null)
            {
                string selUid = !string.IsNullOrEmpty(consoleUid0) ? TeleportConsoleSelection.GetSelectedUid(consoleUid0) : "";
                string selInfo = !string.IsNullOrEmpty(selUid) ? $"（已选 {TeleportStationUid.DisplayForUid(selUid)}）" : "（未选择）";
                _titleText.text = $"选择传送目的地 {selInfo}";
            }

            var candidates = CollectCandidates();
            Plugin.L.LogInfo($"[TS][UI] 选点候选 {consoleUid0}({consoleDisp0}) 共 {candidates.Count} 个圆盘");
            // v0.9.62 存量回退：活体去重键 + 本站坐标键（跨读档实例ID必变，坐标稳定）
            var livePadCoords = new HashSet<string>();
            var seenLiveUid = new HashSet<string>();
            string selfPadUid = "";
            try
            {
                long ckSelf = _currentConsole != null ? GetInstanceKey(_currentConsole) : 0;
                if (ckSelf != 0)
                {
                    long boundSelf = TeleportBindingManager.GetBoundPad(ckSelf);
                    if (boundSelf != 0)
                    {
                        var selfPad = TeleportBindingManager.FindConsoleByKey(boundSelf);
                        if (selfPad != null) selfPadUid = TeleportStationUid.UidFor(selfPad);
                    }
                }
            }
            catch { }
            if (candidates.Count == 0)
            {
                Plugin.L.LogInfo($"[TS][UI] 候选为空 {consoleUid0}（已直扫兜底仍为0，继续走存量回退）");
                CreateInfoRow("无可用传送台（请先放置并绑定圆盘）");
                // 不 return：读档后活体为空时仍可列出存量站
            }
            foreach (var pad in candidates)
            {
                bool online = TeleportConsoleSelection.IsOnline(pad);
                string padUid = TeleportStationUid.UidFor(pad);
                if (string.IsNullOrEmpty(padUid)) continue;
                if (!seenLiveUid.Add(padUid)) continue; // UID 去重（实例ID只做运行时关联）
                bool isSelfPad = !string.IsNullOrEmpty(selfPadUid) && padUid == selfPadUid;
                string displayName = TeleportStationUid.DisplayForPad(pad);
                string distStr = FormatDistXY(_currentConsole, pad);
                // 取证日志：UID 身份键 + 显示名（日志可保留 UID+名）
                Plugin.L.LogInfo($"[TS][UI] 候选 {consoleUid0}({consoleDisp0}) -> {padUid}({displayName}) online={online} dist={distStr} self={isSelfPad}");
                // v0.9.62 活体坐标键（存量去重 + 本站坐标比对；公开方法调用，无反射）
                string padCoord = "";
                try { padCoord = TeleportBindingManager.CoordKey(pad); } catch {}
                if (!string.IsNullOrEmpty(padCoord)) livePadCoords.Add(padCoord);
                if (isSelfPad)
                {
                    Plugin.L.LogInfo($"[TS][UI] 跳过本站行 {padUid}({displayName})");
                    continue; // 定案需求：仅显示除本机外的其他传送站
                }
                try
                {
                    // v0.9.64 目的地无门控：在线态仅显示（在线/上次在线/状态未知），全行可点即选。
                    string padCoord2 = "";
                    try { padCoord2 = TeleportBindingManager.CoordKey(pad); } catch {}
                    bool persistedOn = !string.IsNullOrEmpty(padCoord2) && TeleportConsoleSelection.QueryPersistedOnline(padCoord2);
                    string state = online ? "在线" : (persistedOn ? "上次在线" : "状态未知");
                    string label = $"{displayName} {state} 距{distStr} {padUid}";
                    if (online) label += " ★可传送";

                    var padCap = pad;
                    var padUidCap = padUid;
                    var dispCap = displayName;
                    var distCap = distStr;
                    var btn = CreateRowButton(label, true, () =>
                    {
                        if (_currentConsole == null) return;
                        string cuid = TeleportStationUid.UidFor(_currentConsole);
                        string cdisp = TeleportStationUid.DisplayForConsole(_currentConsole);
                        Plugin.L.LogInfo($"[TS][Sel] 点选 {cuid}({cdisp}) -> {padUidCap}({dispCap}) dist={distCap} online={online}");
                        TeleportConsoleSelection.SetSelected(_currentConsole, padCap);
                        ShowBubble($"已选择 {dispCap}");
                        Close();
                    }, greyLook: !online);
                    // 已选中的高亮（UID 比对）
                    string selUid2 = !string.IsNullOrEmpty(consoleUid0) ? TeleportConsoleSelection.GetSelectedUid(consoleUid0) : "";
                    if (!string.IsNullOrEmpty(selUid2) && selUid2 == padUid)
                    {
                        var img = btn.GetComponent<Image>();
                        if (img != null) img.color = new Color(0.2f, 0.55f, 0.85f, 1f);
                    }
                    bool parentOk = false;
                    try { parentOk = btn != null && btn.transform != null && btn.transform.parent == _contentTr; } catch {}
                    int cc = -1;
                    try { if (_contentTr != null) cc = _contentTr.childCount; } catch {}
                    // v0.9.63 可见几何诊断：行高/activeSelf+activeInHierarchy/文本长
                    float rh = -1f; bool actSelf = false; bool actHier = false; int tlen = 0;
                    try { var rrt = btn != null ? btn.GetComponent<RectTransform>() : null; if (rrt != null) rh = rrt.rect.height; } catch {}
                    try { actSelf = btn != null && btn.activeSelf; } catch {}
                    try { actHier = btn != null && btn.activeInHierarchy; } catch {}
                    try { tlen = label != null ? label.Length : 0; } catch {}
                    Plugin.L.LogInfo($"[TS][UI] 行渲染成功 {padUid}({displayName}) parentOk={parentOk} childCount={cc} h={rh:F0} activeSelf={actSelf} active={actHier} tlen={tlen}");
                }
                catch (Exception re) { Plugin.L.LogWarning($"[TS][UI] 行渲染失败 {padUid}({displayName}) ex={re}"); }
            }
            // v0.9.63 读档/远站补齐：活体缺失的站按持久坐标直接可选（无走近门控）。
            // v0.9.64：目的地无在线门控（在线态仅显示）；存量行同样执行配对前置
            // （pad 坐标须在绑定坐标对中有记录，否则不进列表）。
            string selfPadCoord = "";
            try
            {
                if (!string.IsNullOrEmpty(selfPadUid))
                {
                    string c = TeleportStationUid.CoordFromUid(selfPadUid);
                    if (!string.IsNullOrEmpty(c)) selfPadCoord = c;
                }
            }
            catch { }
            try { AppendStaleRows(consoleUid0, consoleDisp0, livePadCoords, selfPadCoord); }
            catch (Exception se) { Plugin.L.LogWarning($"[TS][UI] 存量补行异常: {se}"); }
            // 底部 清除选择 按钮
            CreateClearRow();
            try
            {
                int total = -1;
                if (_contentTr != null) total = _contentTr.childCount;
                Plugin.L.LogInfo($"[TS][UI] 列表重建完成 {consoleUid0} 行数={total}");
            } catch {}
        }
        catch (Exception e) { Plugin.L.LogWarning($"[TS][UI] 重建列表异常: {e}"); }
    }

    // P6.4 修空列表：先逐项清洗（坏项跳过，不毒化全表），排序独立 try（失败保序返回）。
    // 旧版 all.Sort 比较器直访 a.transform.position 且外层空 catch{}，任一坏项抛异常即整表清空且无日志。
    private List<TerrainObject> CollectCandidates()
    {
        var list = new List<TerrainObject>();
        var seen = new HashSet<long>();
        try
        {
            var all = FindAllPads();
            // 缓存 TTL/时序空窗兜底：缓存为空时直扫一次 Resources（只读，不轮询不常驻）
            if (all == null || all.Count == 0)
            {
                try
                {
                    var direct = UnityEngine.Resources.FindObjectsOfTypeAll<TerrainObject>();
                    if (direct != null)
                    {
                        all = new List<TerrainObject>();
                        foreach (var t in direct)
                        {
                            try { if (t != null && t.attr != null && t.attr.id == 900102) all.Add(t); } catch {}
                        }
                        Plugin.L.LogInfo($"[TS][UI] 缓存为空，直扫兜底得 {all.Count} 个圆盘");
                    }
                } catch {}
            }
            if (all == null) return list;
            var clean = new List<TerrainObject>();
            foreach (var pad in all)
            {
                try
                {
                    if (pad == null) continue;
                    if (pad.transform == null) continue;
                    if (pad.attr == null || pad.attr.id != 900102) continue;
                    long k = GetInstanceKey(pad);
                    if (!seen.Add(k)) continue;
                    clean.Add(pad);
                } catch {}
            }
            try
            {
                Vector3 cc = Vector3.zero;
                try { if (_currentConsole != null && _currentConsole.transform != null) cc = _currentConsole.transform.position; } catch {}
                Vector3 cPos = cc;
                clean.Sort((a,b) =>
                {
                    try
                    {
                        if (a == null || b == null) return 0;
                        if (a.transform == null || b.transform == null) return 0;
                        float da = (a.transform.position - cPos).sqrMagnitude;
                        float db = (b.transform.position - cPos).sqrMagnitude;
                        return da.CompareTo(db);
                    } catch { return 0; }
                });
            } catch {}
            // v0.9.64 配对前置：仅保留“console+pad 已配对”（活体绑定或坐标对回链成功）；
            // 未配对 console 的 pad / 孤 pad 不进选点列表（各补一行 debug 说明排除原因）。
            list = new List<TerrainObject>();
            foreach (var pad in clean)
            {
                try
                {
                    long k = GetInstanceKey(pad);
                    string pu = TeleportStationUid.UidFor(pad);
                    if (TeleportBindingManager.IsPadBound(k)) { list.Add(pad); continue; }
                    string pck0 = "";
                    try { pck0 = TeleportBindingManager.CoordKey(pad); } catch {}
                    if (!string.IsNullOrEmpty(pck0) && TeleportBindingManager.IsPadCoordPaired(pck0))
                    {
                        list.Add(pad);
                        Plugin.L.LogInfo($"[TS][UI] 候选(坐标对回链) {pu} coord={pck0}（活体未绑但配对文件有记录）");
                        continue;
                    }
                    Plugin.L.LogInfo($"[TS][UI] 排除未配对圆盘 {pu} coord={pck0}（未绑定且坐标对无记录，不进列表）");
                } catch {}
            }
        } catch {}
        return list;
    }

    private GameObject CreateRowButton(string label, bool interactable, Action onClick, bool greyLook = false)
    {
        var go = new GameObject("Row");
        go.transform.SetParent(_contentTr, false);
        var img = go.AddComponent<Image>();
        // v0.9.62 灰显与可点解耦：离线/存量行 greyLook=true（灰色外观）但仍可点（点击给气泡，不静默）
        bool lookOn = interactable && !greyLook;
        img.color = lookOn ? new Color(0.22f, 0.22f, 0.26f, 1f) : new Color(0.16f, 0.16f, 0.16f, 1f);
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
        txt.color = lookOn ? Color.white : new Color(0.55f, 0.55f, 0.55f, 1f);
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

    // ===== v0.9.62 存量回退（读档/远站）：只读地图存量文件，不改其他文件 =====
    // v0.9.65：解析配对证据③（paired/peer；老文件无 paired 字段=缺信息）。
    private class StaleStation
    {
        public string coord;
        public int x;
        public int y;
        public string name;
        public bool online;
        public bool paired;
        public string peer;
        public bool hasPairEvidence;
    }

    // 存量表与地图侧同文件同格式：Config/TeleportMapStations.json {"x,y":{"x":..,"y":..,"name":"..","online":0/1,"paired":0/1,"peer":".."}}（后两字段可选）
    private static List<StaleStation> LoadStaleStations()
    {
        var res = new List<StaleStation>();
        try
        {
            string path = null;
            try { path = System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "TeleportMapStations.json"); } catch {}
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return res;
            string txt = System.IO.File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(txt)) return res;
            int i = 0;
            while (i < txt.Length && res.Count < 128)
            {
                int q1 = txt.IndexOf('"', i);
                if (q1 < 0) break;
                int q2 = txt.IndexOf('"', q1 + 1);
                if (q2 < 0) break;
                string coord = txt.Substring(q1 + 1, q2 - q1 - 1);
                int bo = txt.IndexOf('{', q2);
                if (bo < 0) break;
                int depth = 0;
                bool inStr = false;
                int be = -1;
                for (int j = bo; j < txt.Length; j++)
                {
                    char ch = txt[j];
                    if (inStr) { if (ch == '\\') j++; else if (ch == '"') inStr = false; continue; }
                    if (ch == '"') inStr = true;
                    else if (ch == '{') depth++;
                    else if (ch == '}') { depth--; if (depth == 0) { be = j; break; } }
                }
                if (be < 0) break;
                string body = txt.Substring(bo, be - bo + 1);
                try
                {
                    var st = new StaleStation
                    {
                        coord = coord,
                        x = ParseStaleInt(body, "\"x\""),
                        y = ParseStaleInt(body, "\"y\""),
                        name = ParseStaleStr(body, "\"name\""),
                        online = ParseStaleInt(body, "\"online\"") != 0,
                        paired = ParseStaleInt(body, "\"paired\"") != 0,
                        peer = ParseStaleStr(body, "\"peer\""),
                        hasPairEvidence = body.Contains("\"paired\"")
                    };
                    if (!string.IsNullOrEmpty(coord) && !string.IsNullOrEmpty(st.name)) res.Add(st);
                } catch {}
                i = be + 1;
            }
        } catch {}
        return res;
    }

    private static int ParseStaleInt(string body, string key)
    {
        try
        {
            int ki = body.IndexOf(key, StringComparison.Ordinal);
            if (ki < 0) return 0;
            int ci = body.IndexOf(':', ki + key.Length);
            if (ci < 0) return 0;
            int s = ci + 1;
            while (s < body.Length && (char.IsWhiteSpace(body[s]) || body[s] == '"')) s++;
            int e = s;
            while (e < body.Length && (char.IsDigit(body[e]) || body[e] == '-')) e++;
            if (int.TryParse(body.Substring(s, e - s), out var v)) return v;
        } catch {}
        return 0;
    }

    private static string ParseStaleStr(string body, string key)
    {
        try
        {
            int ki = body.IndexOf(key, StringComparison.Ordinal);
            if (ki < 0) return "";
            int ci = body.IndexOf(':', ki + key.Length);
            if (ci < 0) return "";
            int q1 = body.IndexOf('"', ci);
            if (q1 < 0) return "";
            var sb = new System.Text.StringBuilder();
            for (int j = q1 + 1; j < body.Length; j++)
            {
                char ch = body[j];
                if (ch == '\\' && j + 1 < body.Length)
                {
                    char n = body[j + 1];
                    if (n == '"') sb.Append('"');
                    else if (n == '\\') sb.Append('\\');
                    else if (n == 'n') sb.Append('\n');
                    else sb.Append(n);
                    j++;
                }
                else if (ch == '"') break;
                else sb.Append(ch);
            }
            return sb.ToString();
        } catch { return ""; }
    }

    // A方案距离（坐标版，与 FormatDistXY 同口径：XY 平面，忽略 z）
    private static string FormatDistFromXY(Vector3 c, int x, int y)
    {
        try
        {
            float dx = x - c.x;
            float dy = y - c.y;
            return $"{Mathf.Sqrt(dx * dx + dy * dy):F0}m";
        } catch { return "未知"; }
    }

    private void AppendStaleRows(string consoleUid0, string consoleDisp0, HashSet<string> livePadCoords, string selfPadCoord)
    {
        try
        {
            if (_currentConsole == null || _currentConsole.transform == null) return;
            Vector3 cc = _currentConsole.transform.position;
            var stale = LoadStaleStations();
            if (stale.Count == 0) { Plugin.L.LogInfo($"[TS][UI] 存量站载入 0 条 {consoleUid0}"); return; }
            // 按距离排序（失败保序）
            try
            {
                stale.Sort((a, b) =>
                {
                    try
                    {
                        if (a == null || b == null) return 0;
                        float da = (a.x - cc.x) * (a.x - cc.x) + (a.y - cc.y) * (a.y - cc.y);
                        float db = (b.x - cc.x) * (b.x - cc.x) + (b.y - cc.y) * (b.y - cc.y);
                        return da.CompareTo(db);
                    } catch { return 0; }
                });
            } catch {}
            int added = 0;
            foreach (var st in stale)
            {
                try
                {
                    if (st == null || string.IsNullOrEmpty(st.coord)) continue;
                    if (livePadCoords != null && livePadCoords.Contains(st.coord)) continue; // 活体已列，不重复
                    if (!string.IsNullOrEmpty(selfPadCoord) && st.coord == selfPadCoord)
                    {
                        Plugin.L.LogInfo($"[TS][UI] 跳过本站存量行 coord={st.coord}({st.name})");
                        continue; // 本站（坐标比对，跨读档稳定）
                    }
                    // v0.9.66 配对门控已拆（入表即已配对，门控零收益且自造 paired=false 误拦）；
                    // 存量行只按坐标/自站过滤保留。
                    string distStr = FormatDistFromXY(cc, st.x, st.y);
                    // v0.9.64 显示名优先（UID直查→活体自愈→存量名→UID）；在线态仅显示，无门控。
                    string staleUid = TeleportStationUid.UidFromCoord(st.coord);
                    string staleDisp = TeleportStationUid.DisplayForUid(staleUid);
                    if (staleDisp == staleUid && !string.IsNullOrWhiteSpace(st.name)) staleDisp = st.name;
                    bool staleOnline = st.online;
                    string staleState = staleOnline ? "在线（存量）" : "状态未知";
                    string label = $"{staleDisp} {staleState} 距{distStr} {staleUid}";
                    if (staleOnline) label += " ★可传送";
                    var staleUidCap = staleUid;
                    var staleDispCap = staleDisp;
                    var staleCoordCap = st.coord;
                    var btn = CreateRowButton(label, true, () =>
                    {
                        if (_currentConsole == null) return;
                        string cuid = TeleportStationUid.UidFor(_currentConsole);
                        string cdisp = TeleportStationUid.DisplayForConsole(_currentConsole);
                        if (string.IsNullOrEmpty(cuid)) return;
                        Plugin.L.LogInfo($"[TS][Sel] 点选存量 {cuid}({cdisp}) -> {staleUidCap}({staleDispCap}) coord={staleCoordCap} dist={distStr} lastOnline={staleOnline}");
                        TeleportConsoleSelection.SetSelectedByUid(cuid, staleUidCap);
                        ShowBubble($"已选择 {staleDispCap}");
                        Close();
                    }, greyLook: !staleOnline);
                    if (btn == null) continue;
                    added++;
                    Plugin.L.LogInfo($"[TS][UI] 存量补行 {staleUid}({staleDisp}) dist={distStr} persistedOnline={st.online}");
                } catch (Exception re) { Plugin.L.LogWarning($"[TS][UI] 存量补行失败 coord={st?.coord} ex={re}"); }
            }
            Plugin.L.LogInfo($"[TS][UI] 存量站载入 {stale.Count} 条，补行 {added} 个 {consoleUid0}");
        } catch {}
    }

    // ===== 工具 =====
    private static long GetInstanceKey(TerrainObject t)
    {
        try { return (long)t.GetInstanceID(); } catch { try { return (long)t.Pointer; } catch { return t.GetHashCode(); } }
    }

    private static List<TerrainObject> FindAllPads()
    {
        try { return TeleportObjectCache.FindAllById(900102); } catch {}
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

    // v0.9.63 UID 显示解析（命名优先，无名用UID；永不返回 GO 名）。保留签名供旧调用兼容。
    private static string ResolvePadDisplayName(TerrainObject pad, long padKey)
    {
        return TeleportStationUid.DisplayForPad(pad);
    }

    // A方案距离公式（与绑定 BindRangeSqr 同口径：XY 平面欧氏距离，忽略 z 高度）：
    //   dist = sqrt((px-cx)^2 + (py-cy)^2)；任一端 transform 缺失 → "未知"（不编造坐标）
    private static string FormatDistXY(TerrainObject console, TerrainObject pad)
    {
        try
        {
            if (console == null || console.transform == null || pad == null || pad.transform == null) return "未知";
            var a = console.transform.position;
            var b = pad.transform.position;
            float dx = b.x - a.x; float dy = b.y - a.y;
            return $"{Mathf.Sqrt(dx * dx + dy * dy):F0}m";
        } catch { return "未知"; }
    }

    // A方案：按实例键找控制台（走 TeleportObjectCache 缓存，不自建扫描、不轮询）
    private static TerrainObject FindConsoleByKey(long key)
    {
        try
        {
            var all = TeleportObjectCache.FindAllById(900101);
            if (all != null) foreach (var t in all) if (t != null && GetInstanceKey(t) == key) return t;
        } catch {}
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
