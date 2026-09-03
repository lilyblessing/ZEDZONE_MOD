using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using BepInEx;
using HarmonyLib;
using Il2CppInterop.Runtime.Attributes;
using ZedZoneShared;

namespace TeleportStationPlugin;

/// <summary>
/// PDA 地图传送标：在原生 MapPanel 上为已绑定圆盘注入可点击图标。
/// 图标 48x48 小图优先（传送点标志-48.png → textures/marker48.png 系列），prefab 分支沿用
/// prefab 自带 rect（不硬编码 36），自建回退 36x36；localScale 恒 Vector3.one 保留
/// CreateSimpleMapMarker（基准×param_4）scale 语义；Button 保留可点（选点闭环不断）。
/// 参考 dump.cs:103696 MapPanel 字段 mapParent(0x30) mapIconPrefab_LocationMarker(0x118) mapScaleFloat(0x128)
/// centerPoint(0x12C) worldPositionOffset static(0x8) mapWidth(0x144) mapHeight(0x148)
/// 方法 CreateSimpleMapMarker VA0x180BE6390 Init 0x180BEB280 Update 0x180BF0690 LateUpdate 0x180BEBE70
/// PDAPanel.instance.OpenPanel(string) VA0x180BF3150
/// </summary>
public class TeleportMapManager : MonoBehaviour
{
    public static TeleportMapManager Instance { get; private set; }
    public static TerrainObject PendingConsole;
    // P6.3: 仅当 PendingConsole != null 时地图为传送选点模式，原生 M 不显示标记
    public static bool IsTeleportMapMode => PendingConsole != null;
    // 兼容 ComputerFix 的 pending
    public static bool IsTeleportActive => PendingConsole != null || TeleportConsoleComputerFix.PendingConsoleForMap != null;

    // v0.9.61 标记键改为字符串：活体 "i:{instanceId}"，持久补齐 "c:{x,y}"（实例ID只做运行时关联）。
    private Dictionary<string, GameObject> _markers = new();
    private Dictionary<string, Text> _labels = new();
    // v0.9.61 远站持久坐标表（复用木牌“存量数据而非活体”思想，自建表不污染原生木牌）：
    // coord "x,y" -> 站记录（静态坐标+上次实测名/在线态），存 TeleportMapStations.json。
    private readonly Dictionary<string, StationRec> _persisted = new();
    private bool _persistedLoaded = false;
    private float _lastPersistedSave = -999f;
    // v0.9.65 配对证据③：paired=观测到配对时写true+peer=对端console坐标；无paired字段=老文件缺信息；
    // paired=false(有字段)=解绑后显式清除。门控：paired→放行；无字段→fail-open放行；paired=false→拦。
    private class StationRec { public int x; public int y; public string name; public bool online; public bool paired; public string peer; public bool hasPairEvidence; }
    private Sprite _markerSprite;
    private float _nextRefresh = -1f;
    private bool _mapOpenLast = false;
    private float _pendingClearAt = -1f;
    private static bool _patched;
    // 缓存 Type 避免每帧反射触发 GetTypesFromAssembly 刷日志
    private static Type _mapPanelType;
    private static Type _pdaPanelType;
    private static Type _nameMgrType;
    private static Type _basicCharType;
    private static Type _humanCharType;
    // 缓存 FieldInfo 避免每 pad 每 0.5s GetField
    private static FieldInfo _fi_worldOffset;
    private static FieldInfo _fi_mapScale;
    private static FieldInfo _fi_center;
    private static FieldInfo _fi_mapWidth;
    private static FieldInfo _fi_mapHeight;
    private static FieldInfo _fi_mapParent;
    private static FieldInfo _fi_mapIconPrefab;
    private static bool _typeCacheDone = false;
    // worldPositionOffset 解析诊断（一次性日志，避免刷屏；RefreshMarkers 低频调用，单次日志无害）
    private static bool _offsetDiagLogged = false;
    private static void EnsureTypeCache()
    {
        if (_typeCacheDone) return;
        // 仅首次执行，避免每帧 AccessTools.TypeByName 刷屏（49k日志根因）
        _typeCacheDone = true;
        if (_mapPanelType == null) try { _mapPanelType = AccessTools.TypeByName("MapPanel"); } catch {}
        if (_pdaPanelType == null) try { _pdaPanelType = AccessTools.TypeByName("PDAPanel"); } catch {}
        if (_basicCharType == null) try { _basicCharType = AccessTools.TypeByName("BasicCharacterController"); } catch {}
        if (_humanCharType == null && _basicCharType == null) try { _humanCharType = AccessTools.TypeByName("HumanCharacterController"); } catch {}
        if (_nameMgrType == null) try { _nameMgrType = typeof(TeleportStationNameManager); } catch {}
        // FieldInfo 缓存改用静默反射（不走 AccessTools.Field 避免 HarmonyX 每0.5s 刷 Could not find field）
        // 直接保持 _fi_* 为 null，走 WorldToMapPos/GetMapParent 中的 Reflect.Get 回退（Il2Cpp 兼容）
    }

    // ===== 静态 patch =====
    public static void EnsurePatch(Harmony h)
    {
        if (_patched) return;
        _patched = true;
        try
        {
            EnsureTypeCache();
            var panelType = _mapPanelType;
            if (panelType == null) { Plugin.L.LogWarning("[TS][Map] MapPanel 类型未找到"); return; }
            var init = AccessTools.Method(panelType, "Init");
            if (init != null)
            {
                h.Patch(init, postfix: new HarmonyMethod(typeof(TeleportMapManager).GetMethod(nameof(MapInitPostfix), System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)));
                Plugin.L.LogInfo("[TS][Map] 已挂钩 MapPanel.Init (VA0x180BEB280 postfix RefreshMarkers)");
            }
            var gen = AccessTools.Method(panelType, "GenerateMap");
            if (gen != null)
            {
                h.Patch(gen, postfix: new HarmonyMethod(typeof(TeleportMapManager).GetMethod(nameof(MapGeneratePostfix), System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)));
                Plugin.L.LogInfo("[TS][Map] 已挂钩 MapPanel.GenerateMap (postfix RefreshMarkers)");
            }
        }
        catch (Exception ex) { Plugin.L.LogWarning($"[TS][Map] EnsurePatch 异常: {ex.Message.Split('\n')[0]}"); }
    }

    public static void MapInitPostfix(object __instance)
    {
        try { if (Instance != null) Instance._nextRefresh = 0f; } catch {}
        try { Instance?.RefreshMarkers(); } catch {}
    }
    public static void MapGeneratePostfix(object __instance)
    {
        try { if (Instance != null) Instance._nextRefresh = 0f; } catch {}
        try { Instance?.RefreshMarkers(); } catch {}
    }

    public static TeleportMapManager EnsureExists()
    {
        if (Instance != null) return Instance;
        var go = new GameObject("TeleportMapManager");
        UnityEngine.Object.DontDestroyOnLoad(go);
        return go.AddComponent<TeleportMapManager>();
    }

    // ===== MonoBehaviour =====
    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
        try { UnityEngine.Object.DontDestroyOnLoad(gameObject); } catch {}
        try { LoadMarkerSprite(); } catch (Exception ex) { Plugin.L.LogWarning($"[TS][Map] LoadMarkerSprite Awake 异常: {ex.Message.Split('\n')[0]}"); }
    }

    void OnDestroy() { if (Instance == this) Instance = null; }

    void Update()
    {
        try
        {
            var mp = GetMapPanelInstance();
            bool isOpen = mp != null && IsMapOpen(mp);
            if (isOpen && !_mapOpenLast)
            {
                // 事件驱动：地图刚打开时刷新一次
                try { RefreshMarkers(); } catch {}
            }
            bool wasOpen = _mapOpenLast;
            _mapOpenLast = isOpen;
            if (!isOpen)
            {
                // 离开地图：始终清理标记（P6.4 允许 M 显示，但关闭时仍需清理避免残留）
                if (_markers.Count > 0)
                {
                    ClearAllMarkers();
                }
                if (wasOpen && IsTeleportMapMode)
                {
                    // 传送地图刚刚关闭：若用户是按 ESC/关闭按钮且未选点，3s 后自动取消 Pending
                    try { _pendingClearAt = Time.unscaledTime + 3f; } catch { _pendingClearAt = -1f; }
                }
                // 处理 Pending 超时清理（地图已关 + 未选）
                if (IsTeleportMapMode && _pendingClearAt > 0f)
                {
                    float now2 = 0f; try { now2 = Time.unscaledTime; } catch { now2 = Time.realtimeSinceStartup; }
                    if (now2 >= _pendingClearAt)
                    {
                        PendingConsole = null;
                        try { TeleportConsoleComputerFix.PendingConsoleForMap = null; TeleportConsoleComputerFix.CurrentConsole = null; } catch {}
                        _pendingClearAt = -1f;
                        Plugin.L.LogInfo("[TS][Map] 传送地图未选点超时，已自动取消选点模式");
                    }
                }
                if (!IsTeleportMapMode) _pendingClearAt = -1f;
                return;
            }
            // 地图打开时：P6.4 始终显示已绑 pad 的标记（不再按 IsTeleportMapMode 栅栏）
            // 事件驱动：不再每 0.5s 轮询 Refresh，仅在 MapInit/Generate 或首次打开时刷新
            _pendingClearAt = -1f;
            // 保留 _nextRefresh 字段但不再用于高频轮询
        }
        catch (Exception ex) { Plugin.L.LogWarning($"[TS][Map] Update 异常: {ex.Message.Split('\n')[0]}"); }
    }

    private void ClearAllMarkers()
    {
        try
        {
            foreach (var kv in _markers) try { if (kv.Value != null) UnityEngine.Object.Destroy(kv.Value); } catch {}
            _markers.Clear();
            _labels.Clear();
        } catch {}
    }

    // ===== RefreshMarkers =====
    [HideFromIl2Cpp]
    public void RefreshMarkers()
    {
        try
        {
            if (_markerSprite == null) LoadMarkerSprite();
            else { /* 已缓存跳过 */ }
            var mp = GetMapPanelInstance();
            if (mp == null) return;
            var mapParent = GetMapParent(mp);
            if (mapParent == null) return;

            var pads = CollectBoundPads();
            var alive = new HashSet<string>();
            var liveCoords = new HashSet<string>();

            // 尝试获取原生 prefab 仅一次
            GameObject prefab = null;
            try
            {
                if (_fi_mapIconPrefab != null) prefab = _fi_mapIconPrefab.GetValue(mp) as GameObject;
                if (prefab == null) prefab = Reflect.Get(mp, "mapIconPrefab_LocationMarker") as GameObject;
            } catch {}

            foreach (var pad in pads)
            {
                if (pad == null || pad.transform == null) continue;
                string padKey = "i:" + GetInstanceKey(pad);
                alive.Add(padKey);
                Vector2 worldPos = new Vector2(pad.transform.position.x, pad.transform.position.y);
                Vector2 anchoredPos = WorldToMapPos(worldPos);
                bool online = TeleportConsoleSelection.IsOnline(pad);
                string name = GetNameForPad(pad);
                // v0.9.61 活体实测写入持久坐标表（远站补齐的数据源）
                // v0.9.65：pads 来自 CollectBoundPads（已配对），同步写入配对证据③（对端 console 坐标）。
                try
                {
                    string ck = TeleportStationNameManager.CoordKey(pad);
                    if (!string.IsNullOrEmpty(ck))
                    {
                        liveCoords.Add(ck);
                        string peer = "";
                        try
                        {
                            long pk2 = GetInstanceKey(pad);
                            long ck2 = TeleportBindingManager.GetBoundConsole(pk2);
                            if (ck2 != 0)
                            {
                                var cobj = FindByKey(ck2) as TerrainObject;
                                if (cobj != null) peer = TeleportStationNameManager.CoordKey(cobj);
                            }
                        } catch {}
                        RecordPersisted(ck, worldPos, name, online, peer);
                    }
                } catch {}

                if (!_markers.TryGetValue(padKey, out var go) || go == null)
                {
                    if (prefab != null)
                    {
                        try
                        {
                            go = UnityEngine.Object.Instantiate(prefab, mapParent);
                            go.name = $"TS_Marker_{padKey}";
                            var rt = go.GetComponent<RectTransform>();
                            if (rt != null)
                            {
                                // 原生 0x118 路径仅 SetParent(mapParent)+写anchoredPosition，
                                // anchor/pivot沿用 prefab 自带（反编译未见逐marker改写），故不覆盖；
                                // 覆盖成 0.5/0.5 会在 prefab 非中心锚点时引入半个父级尺寸的系统性偏移。
                                rt.anchoredPosition = anchoredPos;
                                // 沿用 prefab 自带 rect（无实测证据不硬编码 sizeDelta；scale 语义保留 One，
                                // 对应 CreateSimpleMapMarker 基准×param_4 由原生 prefab 侧决定）。
                                rt.localScale = Vector3.one;
                            }
                            var img = go.GetComponent<Image>();
                            if (img == null) try { img = go.GetComponentInChildren<Image>(true); } catch {}
                            if (img != null)
                            {
                                if (_markerSprite != null) img.sprite = _markerSprite;
                                img.preserveAspect = true;
                                img.raycastTarget = true;
                                img.color = online ? Color.white : new Color(0.55f, 0.55f, 0.55f, 1f);
                            }
                            // 可点击：保留 Button 并接选点闭环（不再 Destroy，保证选点不断）。
                            try
                            {
                                var btn = go.GetComponent<Button>();
                                if (btn == null) btn = go.AddComponent<Button>();
                                btn.interactable = true;
                                var capturedPad = pad;
                                btn.onClick.RemoveAllListeners();
                                btn.onClick.AddListener(new System.Action(() => { try { Instance?.OnMarkerClick(capturedPad); } catch {} }));
                            } catch {}
                            var txt = go.GetComponentInChildren<Text>(true);
                            if (txt != null)
                            {
                                txt.text = $"{name}\n{(online ? "<color=#7CFF7C>在线</color>" : "<color=#FF6B6B>离线</color>")}";
                                txt.alignment = TextAnchor.UpperCenter;
                                txt.horizontalOverflow = HorizontalWrapMode.Overflow;
                                txt.verticalOverflow = VerticalWrapMode.Overflow;
                                txt.fontSize = 12;
                                txt.fontStyle = FontStyle.Bold;
                                txt.color = Color.white;
                                ApplyFont(txt);
                                // 去掉双特效：仅保留 Outline 或无，移除 Shadow
                                try { var shadows = txt.GetComponents<Shadow>(); if (shadows != null) foreach (var s in shadows) if (s != null) UnityEngine.Object.Destroy(s); } catch {}
                                try { var shadows2 = go.GetComponentsInChildren<Shadow>(true); if (shadows2 != null) foreach (var s in shadows2) if (s != null && s.gameObject == txt.gameObject) UnityEngine.Object.Destroy(s); } catch {}
                                // 确保有 Outline（若无则添加一个），若已有则保留
                                try
                                {
                                    var ol = txt.GetComponent<Outline>();
                                    if (ol == null) { ol = txt.gameObject.AddComponent<Outline>(); ol.effectColor = new Color(0f, 0f, 0f, 0.5f); ol.effectDistance = new Vector2(1f, -1f); }
                                    else { ol.effectColor = new Color(0f, 0f, 0f, 0.5f); ol.effectDistance = new Vector2(1f, -1f); }
                                } catch {}
                                _labels[padKey] = txt;
                            }
                            else
                            {
                                // prefab 无 Text，回退创建一个 Label 子物体
                                var labelGO = new GameObject("Label");
                                labelGO.transform.SetParent(go.transform, false);
                                var ntxt = labelGO.AddComponent<Text>();
                                ntxt.alignment = TextAnchor.UpperCenter;
                                ntxt.horizontalOverflow = HorizontalWrapMode.Overflow;
                                ntxt.verticalOverflow = VerticalWrapMode.Overflow;
                                ntxt.fontSize = 12;
                                ntxt.fontStyle = FontStyle.Bold;
                                ntxt.color = Color.white;
                                ApplyFont(ntxt);
                                var lrt = ntxt.rectTransform;
                                lrt.anchorMin = new Vector2(0.5f, 0.5f);
                                lrt.anchorMax = new Vector2(0.5f, 0.5f);
                                lrt.pivot = new Vector2(0.5f, 1f);
                                lrt.anchoredPosition = new Vector2(0f, -18f);
                                lrt.sizeDelta = new Vector2(120f, 36f);
                                ntxt.text = $"{name}\n{(online ? "<color=#7CFF7C>在线</color>" : "<color=#FF6B6B>离线</color>")}";
                                try { var ol = labelGO.AddComponent<Outline>(); ol.effectColor = new Color(0f, 0f, 0f, 0.5f); ol.effectDistance = new Vector2(1f, -1f); } catch {}
                                _labels[padKey] = ntxt;
                            }
                            _markers[padKey] = go;
                            Plugin.L.LogInfo($"[TS][Map] 创建标记(prefab) pad={padKey} world={worldPos.x:F0},{worldPos.y:F0} anchored={anchoredPos.x:F0},{anchoredPos.y:F0} online={online}");
                        }
                        catch (Exception exPrefab)
                        {
                            Plugin.L.LogWarning($"[TS][Map] prefab Instantiate 失败回退自建: {exPrefab.Message.Split('\n')[0]}");
                            prefab = null;
                            // inline fallback（原 goto 跨 try 非法，改为内联自建）
                            go = new GameObject($"TS_Marker_{padKey}");
                            var rt2 = go.AddComponent<RectTransform>();
                            rt2.sizeDelta = new Vector2(36f, 36f);
                            rt2.anchorMin = new Vector2(0.5f, 0.5f); rt2.anchorMax = new Vector2(0.5f, 0.5f); rt2.pivot = new Vector2(0.5f, 0.5f); rt2.anchoredPosition = anchoredPos; rt2.localScale = Vector3.one;
                            var img2 = go.AddComponent<Image>(); img2.sprite = _markerSprite; img2.preserveAspect = true; img2.raycastTarget = true; img2.color = online ? Color.white : new Color(0.55f,0.55f,0.55f,1f);
                            go.transform.SetParent(mapParent, false); rt2.anchoredPosition = anchoredPos; rt2.localScale = Vector3.one;
                            try { var btnFb = go.AddComponent<Button>(); btnFb.interactable = true; var capFb = pad; btnFb.onClick.AddListener(new System.Action(() => { try { Instance?.OnMarkerClick(capFb); } catch {} })); } catch {}
                            _markers[padKey] = go;
                            var labelGO2 = new GameObject("Label"); labelGO2.transform.SetParent(go.transform, false);
                            var txt2 = labelGO2.AddComponent<Text>(); txt2.alignment = TextAnchor.UpperCenter; txt2.horizontalOverflow = HorizontalWrapMode.Overflow; txt2.verticalOverflow = VerticalWrapMode.Overflow; txt2.fontSize = 12; txt2.fontStyle = FontStyle.Bold; txt2.supportRichText = true; txt2.color = Color.white; ApplyFont(txt2);
                            var lrt2 = txt2.rectTransform; lrt2.anchorMin = new Vector2(0.5f,0.5f); lrt2.anchorMax = new Vector2(0.5f,0.5f); lrt2.pivot = new Vector2(0.5f,1f); lrt2.anchoredPosition = new Vector2(0f,-18f); lrt2.sizeDelta = new Vector2(120f,36f);
                            txt2.text = $"{name}\n{(online ? "<color=#7CFF7C>在线</color>" : "<color=#FF6B6B>离线</color>")}";
                            try { var ol = labelGO2.AddComponent<Outline>(); ol.effectColor = new Color(0f,0f,0f,0.5f); ol.effectDistance = new Vector2(1f,-1f); } catch {}
                            _labels[padKey] = txt2;
                            Plugin.L.LogInfo($"[TS][Map] 创建标记 pad={padKey} world={worldPos.x:F0},{worldPos.y:F0} anchored={anchoredPos.x:F0},{anchoredPos.y:F0} online={online} (prefab回退)");
                        }
                    }
                    else
                    {
                        go = new GameObject($"TS_Marker_{padKey}");
                        var rt = go.AddComponent<RectTransform>();
                        rt.sizeDelta = new Vector2(36f, 36f);
                        rt.anchorMin = new Vector2(0.5f, 0.5f);
                        rt.anchorMax = new Vector2(0.5f, 0.5f);
                        rt.pivot = new Vector2(0.5f, 0.5f);
                        rt.anchoredPosition = anchoredPos;
                        rt.localScale = Vector3.one;

                        var img = go.AddComponent<Image>();
                        img.sprite = _markerSprite;
                        img.preserveAspect = true;
                        img.raycastTarget = true;
                        img.color = online ? Color.white : new Color(0.55f, 0.55f, 0.55f, 1f);

                        // 可点击：自建标记同样保留 Button（选点闭环不断）。
                        try { var btnNew = go.AddComponent<Button>(); btnNew.interactable = true; var capNew = pad; btnNew.onClick.AddListener(new System.Action(() => { try { Instance?.OnMarkerClick(capNew); } catch {} })); } catch {}

                        go.transform.SetParent(mapParent, false);
                        rt.anchoredPosition = anchoredPos;
                        rt.localScale = Vector3.one;

                        _markers[padKey] = go;

                        var labelGO = new GameObject("Label");
                        labelGO.transform.SetParent(go.transform, false);
                        var txt = labelGO.AddComponent<Text>();
                        txt.alignment = TextAnchor.UpperCenter;
                        txt.horizontalOverflow = HorizontalWrapMode.Overflow;
                        txt.verticalOverflow = VerticalWrapMode.Overflow;
                        txt.fontSize = 12;
                        txt.fontStyle = FontStyle.Bold;
                        txt.supportRichText = true;
                        txt.color = Color.white;
                        ApplyFont(txt);
                        var lrt = txt.rectTransform;
                        lrt.anchorMin = new Vector2(0.5f, 0.5f);
                        lrt.anchorMax = new Vector2(0.5f, 0.5f);
                        lrt.pivot = new Vector2(0.5f, 1f);
                        lrt.anchoredPosition = new Vector2(0f, -18f);
                        lrt.sizeDelta = new Vector2(120f, 36f);
                        txt.text = $"{name}\n{(online ? "<color=#7CFF7C>在线</color>" : "<color=#FF6B6B>离线</color>")}";
                        // 仅保留 Outline，不加 Shadow
                        try { var ol = labelGO.AddComponent<Outline>(); ol.effectColor = new Color(0f, 0f, 0f, 0.5f); ol.effectDistance = new Vector2(1f, -1f); } catch {}
                        _labels[padKey] = txt;
                        Plugin.L.LogInfo($"[TS][Map] 创建标记 pad={padKey} world={worldPos.x:F0},{worldPos.y:F0} anchored={anchoredPos.x:F0},{anchoredPos.y:F0} online={online}");
                    }
                }
                else
                {
                    var rt = go.GetComponent<RectTransform>();
                    if (rt != null) rt.anchoredPosition = anchoredPos;
                    var img = go.GetComponent<Image>();
                    if (img == null) try { img = go.GetComponentInChildren<Image>(true); } catch {}
                    if (img != null)
                    {
                        if (_markerSprite != null && img.sprite != _markerSprite) img.sprite = _markerSprite;
                        img.color = online ? Color.white : new Color(0.55f, 0.55f, 0.55f, 1f);
                    }
                    Text txt = null;
                    if (_labels.TryGetValue(padKey, out var cachedTxt) && cachedTxt != null) txt = cachedTxt;
                    else try { txt = go.GetComponentInChildren<Text>(true); if (txt != null) _labels[padKey] = txt; } catch {}
                    if (txt != null)
                    {
                        string t = $"{name}\n{(online ? "<color=#7CFF7C>在线</color>" : "<color=#FF6B6B>离线</color>")}";
                        if (txt.text != t) txt.text = t;
                    }
                    if (go.transform.parent != mapParent) go.transform.SetParent(mapParent, false);
                }
            }
            // v0.9.61 远站补齐：持久坐标表中有、活体未画的站（未绑定/未加载/玩家在远处）同样建标记。
            // v0.9.66 配对门控已拆（入表即已配对，门控零收益且自造 paired=false 误拦）。
            try
            {
                LoadPersisted();
                foreach (var kv in _persisted)
                {
                    if (liveCoords.Contains(kv.Key)) continue;
                    string mkey = "c:" + kv.Key;
                    if (alive.Contains(mkey)) continue;
                    var rec = kv.Value;
                    if (rec == null || string.IsNullOrEmpty(rec.name)) continue;
                    Vector2 worldPos = new Vector2(rec.x, rec.y);
                    Vector2 anchoredPos = WorldToMapPos(worldPos);
                    alive.Add(mkey);
                    if (!_markers.TryGetValue(mkey, out var pgo) || pgo == null)
                    {
                        pgo = BuildOfflineMarker(mkey, anchoredPos, rec.name, rec.online, mapParent);
                        if (pgo != null) Plugin.L.LogInfo($"[TS][Map] 补齐标记(存量) coord={kv.Key} world={rec.x},{rec.y} online={rec.online}");
                    }
                    else
                    {
                        var rt = pgo.GetComponent<RectTransform>();
                        if (rt != null) rt.anchoredPosition = anchoredPos;
                        Text txt = null;
                        if (_labels.TryGetValue(mkey, out var cachedTxt) && cachedTxt != null) txt = cachedTxt;
                        else try { txt = pgo.GetComponentInChildren<Text>(true); if (txt != null) _labels[mkey] = txt; } catch {}
                        if (txt != null)
                        {
                            string t = $"{rec.name}\n{(rec.online ? "<color=#7CFF7C>在线</color>" : "<color=#FF6B6B>离线</color>")}";
                            if (txt.text != t) txt.text = t;
                        }
                        if (pgo.transform.parent != mapParent) pgo.transform.SetParent(mapParent, false);
                    }
                }
                try { SavePersistedThrottled(); } catch {}
            } catch {}
            var toRemove = new List<string>();
            foreach (var kv in _markers) if (!alive.Contains(kv.Key)) toRemove.Add(kv.Key);
            foreach (var k in toRemove)
            {
                try { if (_markers.TryGetValue(k, out var go2) && go2 != null) UnityEngine.Object.Destroy(go2); } catch {}
                _markers.Remove(k);
                _labels.Remove(k);
            }
            if (toRemove.Count > 0) Plugin.L.LogInfo($"[TS][Map] 清理标记 {toRemove.Count} 余 {_markers.Count}");
        }
        catch (Exception ex) { Plugin.L.LogWarning($"[TS][Map] RefreshMarkers 异常: {ex.Message.Split('\n')[0]}"); }
    }

    // ===== WorldToMapPos =====
    // 原生实证（InitMapIcons 0x180BE8790 反编译）：
    //  - 0x118 木牌路径：anchoredPos = worldPosition-get返回值 + 静态偏移(+8/+0xc，加法），
    //    无×scale、无+center，挂mapParent(0x30)后仅写anchoredPosition；
    //  - 0x120 路径同形：world + 静态偏移后直传 CreateSimpleMapMarker(Sprite, anchoredPos, scale, ...)；
    //  - mapScaleFloat(0x128)=0.5f缺省、centerPoint(0x12C)均在父级由 RefreshMapTransformExecute
    //    (0x180BED0B0) 统一应用；RefreshMapTransform(0x180BED340)本体仅写 refreshMapTransformFrameFlag(0x140)。
    // 结论：逐marker公式只能是加法 world+offset。我方旧公式 (world-offset)*scale+center
    // 把父级变换在子级又应用了一次（方向还反了：减 vs 加），即严重偏离根因。
    // worldPositionOffset身份（高置信，三重一致，静态反编译实证；运行时数值待日志实证）：
    //  1) dump.cs:103746 `public static Vector2 worldPositionOffset; // 0x8`（MapPanel静态区，Vector2占双float）；
    //  2) InitMapIcons 木牌路径 decompiled_map_3VA.c L1893-1903：uVar9=FUN_18054f940(lVar10,0)
    //     （=TerrainObjecLocationMarkerData.get_worldPosition，dump.cs:76126-76127 VA0x18054F940），
    //     anchored=CONCAT44(*(单例+0xc)+y, *(单例+8)+x)，即 world+offset，单例+8/+0xc恰为静态Vector2布局；
    //  3) 加法形状有0x120路径 L1846-1857 交叉印证（world+DAT_1837bb228单例+8/+0xc后直传CreateSimpleMapMarker）。
    //  DAT_1837bb228 token未解析到类名，但字段名+static+偏移+类型四者全对，错了日志会暴露（见下）。
    public Vector2 WorldToMapPos(Vector2 world)
    {
        // v0.9.58 根因定位=offset错（值错，非形状错）：
        //  InitMapIcons 0x118原生逐marker只做 anchored=world+S（decompiled_map_3VA.c L1893-1903），
        //  parent=mapParent、坐标XY、anchor沿prefab均已对齐；S=DAT_1837bb228静态区+8/+0xc，
        //  归属=MapPanel.worldPositionOffset（dump.cs:103746 public static Vector2 // 0x8；
        //  另两同布局静态Vector2 InGameController.playerBornPositionOffset(dump.cs:35455) /
        //  PlayerInventoryPanel.positionOffset(dump.cs:52176)均无地图定位语义，排除）。
        //  v0.9.57字符串反射读静态（TypeByName+GetField）在Il2Cpp代理类型上静默失败恒回零
        //  （铁律：编译期直访GameController.instance等先例；ChargerPadFix:496-501 typeof+Static读写先例），
        //  原生S非零时全标整体平移=用户实测“严重偏离”。故改编译期直访，异常零回退+一次性诊断。
        try
        {
            Vector2 offset = MapPanel.worldPositionOffset;
            if (!_offsetDiagLogged)
            {
                _offsetDiagLogged = true;
                Plugin.L.LogInfo($"[TS][Map] worldPositionOffset 直读成功 offset={offset.x:F1},{offset.y:F1}（编译期直访MapPanel，公式world+offset）");
            }
            return world + offset;
        }
        catch (Exception ex)
        {
            if (!_offsetDiagLogged)
            {
                _offsetDiagLogged = true;
                Plugin.L.LogWarning($"[TS][Map] worldPositionOffset 直读失败，回退零向量: {ex.Message.Split('\n')[0]}（先看此行再量截图向量）");
            }
            return world;
        }
    }

    // 兼容 spec 命名：WorldToMapPos(Vector2) 的包装
    private Vector2 WorldToMap(Vector2 world, object mapPanel) => WorldToMapPos(world);

    // ===== v0.9.61 改名即时同步 + 存量表持久化 =====
    // 改名回调：即时改已建标记文本（根因①：创建时文本只写一次，改名后无更新路径）。
    // 由 TeleportStationRenameUI.OnConfirm 在 SetName 后调用；地图关闭时仅更新存量表，下次打开即新名。
    public static void NotifyRenamed(TerrainObject console, string newName)
    {
        try
        {
            if (console == null || string.IsNullOrEmpty(newName)) return;
            var inst = Instance;
            if (inst == null) return;
            var keys = new List<string>();
            try
            {
                long ck = GetInstanceKey(console);
                long pk = TeleportBindingManager.GetBoundPad(ck);
                if (pk != 0) keys.Add("i:" + pk);
                string cck = TeleportStationNameManager.CoordKey(console);
                if (!string.IsNullOrEmpty(cck)) keys.Add("c:" + cck);
                if (pk != 0)
                {
                    var pad = FindByKey(pk) as TerrainObject;
                    if (pad != null)
                    {
                        string pck = TeleportStationNameManager.CoordKey(pad);
                        if (!string.IsNullOrEmpty(pck)) keys.Add("c:" + pck);
                    }
                }
            } catch {}
            foreach (var k in keys)
            {
                try
                {
                    if (inst._labels.TryGetValue(k, out var txt) && txt != null)
                    {
                        string cur = txt.text ?? "";
                        int nl = cur.IndexOf('\n');
                        string suffix = nl >= 0 ? cur.Substring(nl) : "\n<color=#7CFF7C>在线</color>";
                        txt.text = newName + suffix;
                    }
                } catch {}
                try
                {
                    string bare = k.StartsWith("c:") ? k.Substring(2) : null;
                    if (bare != null && inst._persisted.TryGetValue(bare, out var rec) && rec != null) rec.name = newName;
                } catch {}
            }
            try { inst.SavePersistedThrottled(force: true); } catch {}
        } catch {}
    }

    [HideFromIl2Cpp]
    private GameObject BuildOfflineMarker(string mkey, Vector2 anchoredPos, string name, bool online, Transform mapParent)
    {
        try
        {
            var go = new GameObject(mkey.StartsWith("TS_Marker_") ? mkey : $"TS_Marker_{mkey}");
            var rt = go.AddComponent<RectTransform>();
            rt.sizeDelta = new Vector2(36f, 36f);
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = anchoredPos;
            rt.localScale = Vector3.one;
            var img = go.AddComponent<Image>();
            img.sprite = _markerSprite;
            img.preserveAspect = true;
            img.raycastTarget = true;
            img.color = online ? Color.white : new Color(0.55f, 0.55f, 0.55f, 1f);
            try
            {
                var btn = go.AddComponent<Button>();
                btn.interactable = true;
                // v0.9.63 存量标记可点：persisted-online=true 即按 UID 选点（在线即传，无走近门控）；
                // 从未在线的站仅气泡说明原因。mkey 形如 "c:x,y"。
                string coordCap = mkey.StartsWith("c:") ? mkey.Substring(2) : "";
                btn.onClick.AddListener(new System.Action(() => { try { Instance?.OnOfflineMarkerClick(coordCap); } catch {} }));
            } catch {}
            go.transform.SetParent(mapParent, false);
            rt.anchoredPosition = anchoredPos;
            rt.localScale = Vector3.one;
            _markers[mkey] = go;
            var labelGO = new GameObject("Label");
            labelGO.transform.SetParent(go.transform, false);
            var txt = labelGO.AddComponent<Text>();
            txt.alignment = TextAnchor.UpperCenter;
            txt.horizontalOverflow = HorizontalWrapMode.Overflow;
            txt.verticalOverflow = VerticalWrapMode.Overflow;
            txt.fontSize = 12;
            txt.fontStyle = FontStyle.Bold;
            txt.supportRichText = true;
            txt.color = Color.white;
            ApplyFont(txt);
            var lrt = txt.rectTransform;
            lrt.anchorMin = new Vector2(0.5f, 0.5f);
            lrt.anchorMax = new Vector2(0.5f, 0.5f);
            lrt.pivot = new Vector2(0.5f, 1f);
            lrt.anchoredPosition = new Vector2(0f, -18f);
            lrt.sizeDelta = new Vector2(120f, 36f);
            txt.text = $"{name}\n{(online ? "<color=#7CFF7C>在线</color>" : "<color=#FF6B6B>离线</color>")}";
            try { var ol = labelGO.AddComponent<Outline>(); ol.effectColor = new Color(0f, 0f, 0f, 0.5f); ol.effectDistance = new Vector2(1f, -1f); } catch {}
            _labels[mkey] = txt;
            return go;
        } catch { return null; }
    }

    // v0.9.65：peerCoord=对端 console 坐标（配对证据③；调用方仅传已绑定 pad，故 paired恒true）。
    private void RecordPersisted(string coord, Vector2 worldPos, string name, bool online, string peerCoord)
    {
        try
        {
            if (string.IsNullOrEmpty(coord) || string.IsNullOrEmpty(name)) return;
            if (_persisted.TryGetValue(coord, out var rec) && rec != null)
            {
                rec.x = Mathf.RoundToInt(worldPos.x);
                rec.y = Mathf.RoundToInt(worldPos.y);
                rec.name = name;
                rec.online = online;
                rec.paired = true;
                rec.hasPairEvidence = true;
                if (!string.IsNullOrEmpty(peerCoord)) rec.peer = peerCoord;
            }
            else _persisted[coord] = new StationRec { x = Mathf.RoundToInt(worldPos.x), y = Mathf.RoundToInt(worldPos.y), name = name, online = online, paired = true, peer = peerCoord ?? "", hasPairEvidence = true };
        } catch {}
    }

    // v0.9.66 no-op：配对门控已拆，门控不再读 paired（字段读写保留防文件断层）。
    // 保留签名供 TryUnbind（死代码，未来接线不断）；TryUnbind 仍可调用，无副作用。
    public static void MarkStationUnpaired(string padCoord, string consoleCoord)
    {
        try { Plugin.L.LogInfo($"[TS][Map] MarkStationUnpaired no-op pad={padCoord} peer={consoleCoord}（门控已拆）"); } catch {}
    }

    private static string PersistedPath()
    {
        try { return System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "TeleportMapStations.json"); }
        catch { return null; }
    }

    private void LoadPersisted()
    {
        if (_persistedLoaded) return;
        _persistedLoaded = true;
        try
        {
            string path = PersistedPath();
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return;
            string txt = System.IO.File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(txt)) return;
            // {"coord":{"x":1,"y":2,"name":"..","online":1},...} 极简解析
            int i = 0;
            while (i < txt.Length)
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
                    var rec = new StationRec
                    {
                        x = ParseIntField(body, "\"x\""),
                        y = ParseIntField(body, "\"y\""),
                        name = ParseStrField(body, "\"name\""),
                        online = ParseIntField(body, "\"online\"") != 0,
                        // v0.9.65 老文件无 paired 字段 → hasPairEvidence=false（缺信息，门控 fail-open）
                        paired = ParseIntField(body, "\"paired\"") != 0,
                        peer = ParseStrField(body, "\"peer\""),
                        hasPairEvidence = body.Contains("\"paired\"")
                    };
                    if (!string.IsNullOrEmpty(coord) && !string.IsNullOrEmpty(rec.name)) _persisted[coord] = rec;
                } catch {}
                i = be + 1;
            }
            if (_persisted.Count > 0) Plugin.L.LogInfo($"[TS][Map] 存量站载入 {_persisted.Count} 条");
        } catch {}
    }

    private static int ParseIntField(string body, string key)
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

    private static string ParseStrField(string body, string key)
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

    private void SavePersistedThrottled(bool force = false)
    {
        try
        {
            float now = 0f;
            try { now = Time.unscaledTime; } catch { now = Time.realtimeSinceStartup; }
            if (!force && now - _lastPersistedSave < 5f) return;
            _lastPersistedSave = now;
            string path = PersistedPath();
            if (string.IsNullOrEmpty(path) || _persisted.Count == 0) return;
            var sb = new System.Text.StringBuilder("{");
            bool first = true;
            foreach (var kv in _persisted)
            {
                if (kv.Value == null || string.IsNullOrEmpty(kv.Value.name)) continue;
                if (!first) sb.Append(",");
                string en = kv.Value.name.Replace("\\", "\\\\").Replace("\"", "\\\"");
                string epeer = (kv.Value.peer ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
                sb.Append($"\"{kv.Key}\":{{\"x\":{kv.Value.x},\"y\":{kv.Value.y},\"name\":\"{en}\",\"online\":{(kv.Value.online ? 1 : 0)},\"paired\":{(kv.Value.paired ? 1 : 0)},\"peer\":\"{epeer}\"}}");
                first = false;
            }
            sb.Append("}");
            System.IO.File.WriteAllText(path, sb.ToString());
        } catch {}
    }

    // ===== OnMarkerClick =====
    [HideFromIl2Cpp]
    public void OnMarkerClick(TerrainObject pad)
    {
        try
        {
            if (pad == null) { ShowBubble("该站当前离线"); return; }
            if (PendingConsole == null) { ShowBubble("请先在控制台选择传送"); return; }
            long ck = GetInstanceKey(PendingConsole);
            string cuid = TeleportStationUid.UidFor(PendingConsole);
            long pendingPadKey = TeleportBindingManager.GetBoundPad(ck);
            TerrainObject pendingPadObj = pendingPadKey != 0 ? FindByKey(pendingPadKey) as TerrainObject : null;
            if (pendingPadKey == 0 || pendingPadObj == null) { ShowBubble("本站未绑定圆盘"); return; }
            // 发送方需 IsSenderReady(供电+电池≥10000)；接收方无门控（用户定案 v0.9.64）
            if (!TeleportConsoleSelection.IsSenderReady(pendingPadObj))
            {
                if (!TeleportConsoleSelection.IsOnline(pendingPadObj)) ShowBubble("本站离线（未通电或未绑定）");
                else ShowBubble("本站电量不足（需≥10000）");
                return;
            }
            string targetUid = TeleportStationUid.UidFor(pad);
            if (pad == pendingPadObj) { ShowBubble("不能选择本站"); return; }
            long targetKey = GetInstanceKey(pad);
            if (targetKey == pendingPadKey) { ShowBubble("不能选择本站"); return; }

            TeleportConsoleSelection.SetSelected(PendingConsole, pad);
            string name = TeleportStationUid.DisplayForPad(pad);
            ShowBubble($"已选择 {name}");
            Plugin.L.LogInfo($"[TS][Map] 选点 {cuid}({TeleportStationUid.DisplayForConsole(PendingConsole)}) -> {targetUid}({name})");

            CloseMapPanel();
        }
        catch (Exception ex) { Plugin.L.LogWarning($"[TS][Map] OnMarkerClick 异常: {ex.Message.Split('\n')[0]}"); }
    }

    // v0.9.63 存量标记选点：无活体对象，以 UID 为身份；persisted-online=true 即选点（在线即传）。
    [HideFromIl2Cpp]
    public void OnOfflineMarkerClick(string coord)
    {
        try
        {
            if (string.IsNullOrEmpty(coord) || PendingConsole == null)
            {
                if (PendingConsole == null) ShowBubble("请先在控制台选择传送");
                return;
            }
            string cuid = TeleportStationUid.UidFor(PendingConsole);
            string targetUid = TeleportStationUid.UidFromCoord(coord);
            if (!TeleportStationUid.IsUid(cuid) || !TeleportStationUid.IsUid(targetUid)) return;
            long ck = GetInstanceKey(PendingConsole);
            long pendingPadKey = TeleportBindingManager.GetBoundPad(ck);
            TerrainObject pendingPadObj = pendingPadKey != 0 ? FindByKey(pendingPadKey) as TerrainObject : null;
            if (pendingPadKey == 0 || pendingPadObj == null) { ShowBubble("本站未绑定圆盘"); return; }
            if (!TeleportConsoleSelection.IsSenderReady(pendingPadObj))
            {
                if (!TeleportConsoleSelection.IsOnline(pendingPadObj)) ShowBubble("本站离线（未通电或未绑定）");
                else ShowBubble("本站电量不足（需≥10000）");
                return;
            }
            // v0.9.64 接收方无门控：persisted-online 仅显示，不拒绝。
            string selfUid = TeleportStationUid.UidFor(pendingPadObj);
            if (targetUid == selfUid) { ShowBubble("不能选择本站"); return; }
            TeleportConsoleSelection.SetSelectedByUid(cuid, targetUid);
            string disp = TeleportStationUid.DisplayForUid(targetUid);
            if (disp == targetUid)
            {
                // UID 直查/活体自愈均未中 → 存量名兜底（保证“家”类命名跨站显示）
                int qx, qy; string pn; bool pon;
                if (QueryPersistedStation(TeleportStationUid.CoordFromUid(targetUid), out qx, out qy, out pn, out pon)
                    && !string.IsNullOrWhiteSpace(pn)) disp = pn;
            }
            ShowBubble($"已选择 {disp}");
            Plugin.L.LogInfo($"[TS][Map] 存量选点 {cuid} -> {targetUid}({disp})");
            CloseMapPanel();
        }
        catch (Exception ex) { Plugin.L.LogWarning($"[TS][Map] OnOfflineMarkerClick 异常: {ex.Message.Split('\n')[0]}"); }
    }

    private void CloseMapPanel()
    {
        try
        {
            EnsureTypeCache();
            var pdaType = _pdaPanelType;
            var instProp = pdaType?.GetProperty("instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var pda = instProp?.GetValue(null);
            if (pda != null)
            {
                var close = pda.GetType().GetMethod("ClosePanel", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (close != null) close.Invoke(pda, null);
                else { var close2 = AccessTools.Method(pda.GetType(), "Close"); close2?.Invoke(pda, null); }
            }
        }
        catch (Exception ex2) { Plugin.L.LogWarning($"[TS][Map] 关闭地图异常: {ex2.Message.Split('\n')[0]}"); }
        PendingConsole = null;
        try { TeleportConsoleComputerFix.PendingConsoleForMap = null; TeleportConsoleComputerFix.CurrentConsole = null; } catch {}
        try { if (Instance != null) Instance._pendingClearAt = -1f; } catch {}
    }

    // ===== v0.9.63 持久在线态静态查询（文件直读，不碰标记绘制，供选点/触发两路在线判） =====
    // TeleportMapStations.json: {"x,y":{"x":..,"y":..,"name":"..","online":0/1}}
    public static bool QueryPersistedStation(string coord, out int x, out int y, out string name, out bool online)
    {
        x = 0; y = 0; name = ""; online = false;
        if (string.IsNullOrEmpty(coord)) return false;
        try
        {
            // 实例表优先（本帧即时性），文件兜底（实例未就绪/跨会话）
            var inst = Instance;
            if (inst != null && inst._persisted.TryGetValue(coord, out var rec) && rec != null)
            {
                x = rec.x; y = rec.y; name = rec.name ?? ""; online = rec.online;
                return !string.IsNullOrEmpty(name);
            }
            string path = PersistedPath();
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return false;
            string txt = System.IO.File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(txt)) return false;
            string key = "\"" + coord + "\"";
            int ki = txt.IndexOf(key, StringComparison.Ordinal);
            if (ki < 0) return false;
            int bo = txt.IndexOf('{', ki + key.Length);
            if (bo < 0) return false;
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
            if (be < 0) return false;
            string body = txt.Substring(bo, be - bo + 1);
            x = ParseIntFieldStatic(body, "\"x\"");
            y = ParseIntFieldStatic(body, "\"y\"");
            name = ParseStrFieldStatic(body, "\"name\"");
            online = ParseIntFieldStatic(body, "\"online\"") != 0;
            return !string.IsNullOrEmpty(name);
        }
        catch { return false; }
    }

    public static bool QueryPersistedOnline(string coord)
    {
        try
        {
            int x, y; string n; bool on;
            if (QueryPersistedStation(coord, out x, out y, out n, out on)) return on;
        }
        catch { }
        return false;
    }

    private static int ParseIntFieldStatic(string body, string key)
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
            int v;
            if (int.TryParse(body.Substring(s, e - s), out v)) return v;
        }
        catch { }
        return 0;
    }

    private static string ParseStrFieldStatic(string body, string key)
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
        }
        catch { return ""; }
    }

    // ===== LoadMarkerSprite =====
    private void LoadMarkerSprite()
    {
        if (_markerSprite != null) return;
        // 48x48 小图优先（源文件：传送点标志-48.png，用户手动小图），部署落点 textures/marker48.png 系列；
        // 旧 730 大图仅作回退。实际尺寸以贴图本身为准，显示侧不硬编码缩放。
        string p0a = Path.Combine(Plugin.PluginDir, "textures/marker48.png");
        string p0b = Path.Combine(Plugin.PluginDir, "textures/marker-48.png");
        string p0c = Path.Combine(Plugin.PluginDir, "textures/teleport-marker-48.png");
        string p0d = Path.Combine(Plugin.PluginDir, "marker48.png");
        string p1 = Path.Combine(Plugin.PluginDir, "textures/marker.png");
        string p2 = Path.Combine(Plugin.PluginDir, "textures/mapmarker.png");
        string p3 = Path.Combine(Plugin.PluginDir, "marker.png");
        string chosen = null;
        foreach (var p in new[] { p0a, p0b, p0c, p0d, p1, p2, p3 }) { try { if (File.Exists(p)) { chosen = p; break; } } catch {} }
        if (chosen != null)
        {
            try
            {
                var bytes = File.ReadAllBytes(chosen);
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (ImageConversion.LoadImage(tex, bytes))
                {
                    tex.filterMode = FilterMode.Bilinear;
                    tex.wrapMode = TextureWrapMode.Clamp;
                    var sp = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
                    sp.name = "TP_Marker";
                    _markerSprite = sp;
                    Plugin.L.LogInfo($"[TS][Map] 贴图载入 {chosen} {tex.width}x{tex.height} -> {sp.name}（48小图优先，显示沿 prefab rect / 自建36）");
                    return;
                }
                else Plugin.L.LogWarning($"[TS][Map] LoadImage 失败 {chosen}");
            } catch (Exception ex) { Plugin.L.LogWarning($"[TS][Map] 贴图读取异常 {chosen}: {ex.Message.Split('\n')[0]}"); }
        }
        try
        {
            if (SpriteInjector.Cache.TryGetValue(900102, out var sp2) && sp2 != null) { _markerSprite = sp2; Plugin.L.LogInfo($"[TS][Map] 贴图 fallback SpriteInjector 900102 {sp2.name}"); return; }
        } catch {}
        try
        {
            var all = Resources.FindObjectsOfTypeAll<Sprite>();
            if (all != null)
            {
                foreach (var s in all) if (s != null && s.name != null && s.name.ToLower().Contains("marker")) { _markerSprite = s; Plugin.L.LogInfo($"[TS][Map] 贴图 fallback Resources {s.name}"); return; }
                if (all.Length > 0 && all[0] != null) _markerSprite = all[0];
            }
        } catch {}
        if (_markerSprite == null) Plugin.L.LogWarning("[TS][Map] 贴图全部 fallback 失败");
    }

    // ===== RequestOpenMap =====
    public static void RequestOpenMap(TerrainObject console)
    {
        PendingConsole = console;
        try { TeleportConsoleComputerFix.PendingConsoleForMap = console; TeleportConsoleComputerFix.CurrentConsole = console; } catch {}
        EnsureExists();
        try { if (Instance != null) Instance._pendingClearAt = -1f; } catch {}
        try
        {
            EnsureTypeCache();
            var pdaType = _pdaPanelType;
            if (pdaType == null) { ShowBubbleStatic("请按 M 手动打开地图选点"); return; }
            var instProp = pdaType.GetProperty("instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var pda = instProp?.GetValue(null);
            if (pda == null) { ShowBubbleStatic("请按 M 手动打开地图选点"); return; }
            var open = pda.GetType().GetMethod("OpenPanel", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance, null, new Type[] { typeof(string) }, null);
            bool opened = false;
            if (open != null)
            {
                foreach (var arg in new[] { "Map", "map", "MAP" })
                {
                    try { open.Invoke(pda, new object[] { arg }); opened = true; Plugin.L.LogInfo($"[TS][Map] PDAPanel.OpenPanel({arg}) 已调用 VA0x180BF3150"); break; } catch {}
                }
            }
            else
            {
                var anyOpen = AccessTools.Method(pda.GetType(), "OpenPanel");
                if (anyOpen != null) try { anyOpen.Invoke(pda, new object[] { "Map" }); opened = true; } catch {}
            }
            if (!opened) ShowBubbleStatic("请按 M 手动打开地图选点");
        }
        catch (Exception ex) { Plugin.L.LogWarning($"[TS][Map] RequestOpenMap 异常: {ex.Message.Split('\n')[0]}"); ShowBubbleStatic("请按 M 手动打开地图"); }
    }

    // ===== helpers =====
    private static object GetMapPanelInstance()
    {
        EnsureTypeCache();
        try
        {
            var t = _mapPanelType;
            if (t == null) return null;
            var prop = t.GetProperty("instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            if (prop != null) return prop.GetValue(null);
            var field = t.GetField("instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            if (field != null) return field.GetValue(null);
        } catch {}
        return null;
    }

    private static bool IsMapOpen(object mp)
    {
        EnsureTypeCache();
        try
        {
            var comp = mp as Component;
            if (comp != null)
            {
                if (!comp.gameObject.activeInHierarchy) return false;
                try
                {
                    var pdaType = _pdaPanelType;
                    var instProp = pdaType?.GetProperty("instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    var inst = instProp?.GetValue(null);
                    if (inst != null)
                    {
                        var name = RGet(inst, "currentPanelName") as string;
                        if (!string.IsNullOrEmpty(name)) return name.IndexOf("Map", StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                } catch {}
                return true;
            }
        } catch {}
        return false;
    }

    private static Transform GetMapParent(object mp)
    {
        EnsureTypeCache();
        try
        {
            if (_fi_mapParent != null)
            {
                var v = _fi_mapParent.GetValue(mp);
                if (v is RectTransform rtr) return rtr;
                if (v is Transform tr) return tr;
            }
            var rt = RGet(mp, "mapParent") as RectTransform;
            if (rt != null) return rt;
            var tr2 = RGet(mp, "mapParent") as Transform;
            if (tr2 != null) return tr2;
            var t = mp.GetType();
            var f = t.GetField("mapParent", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (f != null) return f.GetValue(mp) as Transform;
        } catch {}
        return null;
    }

    private static List<TerrainObject> CollectBoundPads()
    {
        var res = new List<TerrainObject>();
        try
        {
            var all = TeleportObjectCache.FindAllById(900102);
            foreach (var pad in all)
            {
                if (pad == null) continue;
                long pk = GetInstanceKey(pad);
                if (!TeleportBindingManager.IsPadBound(pk)) continue;
                long ck = TeleportBindingManager.GetBoundConsole(pk);
                if (ck == 0) continue;
                var c = FindByKey(ck) as TerrainObject;
                if (c == null) continue;
                res.Add(pad);
            }
        } catch {}
        return res;
    }

    private static string GetNameForPad(TerrainObject pad)
    {
        EnsureTypeCache();
        if (pad == null) return "未知站点";
        // v0.9.61 改名写在 console 键下，标记按 pad 查必须先走对端活体 console（根因①修复）。
        try { var r = TeleportStationNameManager.GetNameForPadObject(pad); if (!string.IsNullOrWhiteSpace(r)) return r; } catch {}
        // v0.9.63 未命名回退 UID（永不返回 GO 名/实例ID派生名）。
        return TeleportStationUid.DisplayForPad(pad);
    }

    private void ApplyFont(Text txt)
    {
        try { var arial = Resources.GetBuiltinResource<Font>("Arial.ttf"); if (arial != null) { txt.font = arial; txt.supportRichText = true; return; } } catch {}
        try { var fonts = Resources.FindObjectsOfTypeAll<Font>(); if (fonts != null && fonts.Length > 0 && fonts[0] != null) txt.font = fonts[0]; } catch {}
        try { txt.supportRichText = true; } catch {}
    }

    private static long GetInstanceKey(TerrainObject t)
    {
        try { return (long)t.GetInstanceID(); } catch { try { return (long)t.Pointer; } catch { return t.GetHashCode(); } }
    }

    private static Component FindTerrainObject(Transform tr)
    {
        int d = 0;
        while (tr != null && d++ < 16)
        {
            foreach (var c in tr.GetComponents<Component>()) if (c != null && c.GetType().Name.Contains("TerrainObject")) return c;
            tr = tr.parent;
        }
        return null;
    }

    private static TerrainObject FindByKey(long key)
    {
        try
        {
            var f = typeof(ChargerPadFix).GetField("_knownClones", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var list = f?.GetValue(null) as List<object>;
            if (list != null) foreach (var o in list) { var comp = o as Component; if (comp == null) continue; var t = FindTerrainObject(comp.transform) as TerrainObject; if (t != null && GetInstanceKey(t) == key) return t; }
            var prods = TerrainObject_Production.ActiveObjects_Production;
            if (prods != null) for (int i = 0; i < prods.Count; i++) { var g = prods[i]; if (g == null) continue; var t = FindTerrainObject(g.transform) as TerrainObject; if (t != null && GetInstanceKey(t) == key) return t; }
            var all = Resources.FindObjectsOfTypeAll<TerrainObject>();
            if (all != null) foreach (var t in all) if (t != null && GetInstanceKey(t) == key) return t;
        } catch {}
        return null;
    }

    private static void ShowBubble(string msg)
    {
        EnsureTypeCache();
        try
        {
            var t = _basicCharType ?? _humanCharType;
            var m = t?.GetMethod("ShowDialogueBubble", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            object player = null;
            try { var gc = GameController.instance; if (gc != null) { player = RGet(gc, "player"); if (player == null) player = RGet(gc, "localPlayer"); if (player == null) player = gc.playerCharacter; } } catch {}
            if (player == null) { var go = GameObject.FindWithTag("Player"); if (go != null && t != null) foreach (var c in go.GetComponents<Component>()) if (c != null && c.GetType().Name == t.Name) { player = c; break; } }
            if (player is Component comp && m != null) m.Invoke(comp, new object[] { msg, 4f });
            else Plugin.L.LogInfo($"[TS][Map][Bubble] {msg}");
        } catch { Plugin.L.LogInfo($"[TS][Map][Bubble] {msg}"); }
    }
    private static void ShowBubbleStatic(string msg) => ShowBubble(msg);

    // 内部 Reflect 简易别名，避免与 ZedZoneShared.Reflect 命名冲突的歧义
    private static object RGet(object obj, string name)
    {
        if (obj == null) return null;
        try { return ZedZoneShared.Reflect.Get(obj, name); } catch { return null; }
    }
}
