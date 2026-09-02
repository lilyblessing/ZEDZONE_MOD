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
/// 贴图 textures/marker.png 730x730(60864B) 缩至 36x36, scale 0.35-0.6 区间。
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

    private Dictionary<long, GameObject> _markers = new();
    private Dictionary<long, Text> _labels = new();
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
    private static void EnsureTypeCache()
    {
        if (_mapPanelType == null) try { _mapPanelType = AccessTools.TypeByName("MapPanel"); } catch {}
        if (_pdaPanelType == null) try { _pdaPanelType = AccessTools.TypeByName("PDAPanel"); } catch {}
        if (_basicCharType == null) try { _basicCharType = AccessTools.TypeByName("BasicCharacterController"); } catch {}
        if (_humanCharType == null && _basicCharType == null) try { _humanCharType = AccessTools.TypeByName("HumanCharacterController"); } catch {}
        if (_nameMgrType == null) try { _nameMgrType = typeof(TeleportStationNameManager); } catch {}
        try
        {
            if (_mapPanelType != null)
            {
                if (_fi_worldOffset == null) try { _fi_worldOffset = AccessTools.Field(_mapPanelType, "worldPositionOffset"); } catch {}
                if (_fi_mapScale == null) try { _fi_mapScale = AccessTools.Field(_mapPanelType, "mapScaleFloat"); } catch {}
                if (_fi_center == null) try { _fi_center = AccessTools.Field(_mapPanelType, "centerPoint"); } catch {}
                if (_fi_mapWidth == null) try { _fi_mapWidth = AccessTools.Field(_mapPanelType, "mapWidth"); } catch {}
                if (_fi_mapHeight == null) try { _fi_mapHeight = AccessTools.Field(_mapPanelType, "mapHeight"); } catch {}
                if (_fi_mapParent == null) try { _fi_mapParent = AccessTools.Field(_mapPanelType, "mapParent"); } catch {}
                if (_fi_mapIconPrefab == null) try { _fi_mapIconPrefab = AccessTools.Field(_mapPanelType, "mapIconPrefab_LocationMarker"); } catch {}
            }
        } catch {}
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
            var alive = new HashSet<long>();

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
                long padKey = GetInstanceKey(pad);
                alive.Add(padKey);
                Vector2 worldPos = new Vector2(pad.transform.position.x, pad.transform.position.y);
                Vector2 anchoredPos = WorldToMapPos(worldPos);
                bool online = TeleportConsoleSelection.IsOnline(pad);
                string name = GetNameForPad(pad);

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
                                rt.anchorMin = new Vector2(0.5f, 0.5f);
                                rt.anchorMax = new Vector2(0.5f, 0.5f);
                                rt.pivot = new Vector2(0.5f, 0.5f);
                                rt.anchoredPosition = anchoredPos;
                                rt.localScale = Vector3.one;
                                rt.sizeDelta = new Vector2(36f, 36f);
                            }
                            var img = go.GetComponent<Image>();
                            if (img == null) try { img = go.GetComponentInChildren<Image>(true); } catch {}
                            if (img != null)
                            {
                                if (_markerSprite != null) img.sprite = _markerSprite;
                                img.preserveAspect = true;
                                img.raycastTarget = false;
                                img.color = online ? Color.white : new Color(0.55f, 0.55f, 0.55f, 1f);
                            }
                            // 非交互：移除 Button
                            try { var btn = go.GetComponent<Button>(); if (btn != null) UnityEngine.Object.Destroy(btn); } catch {}
                            try { var btns = go.GetComponentsInChildren<Button>(true); if (btns != null) foreach (var b in btns) if (b != null) UnityEngine.Object.Destroy(b); } catch {}
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
                            var img2 = go.AddComponent<Image>(); img2.sprite = _markerSprite; img2.preserveAspect = true; img2.raycastTarget = false; img2.color = online ? Color.white : new Color(0.55f,0.55f,0.55f,1f);
                            go.transform.SetParent(mapParent, false); rt2.anchoredPosition = anchoredPos; rt2.localScale = Vector3.one;
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
                        img.raycastTarget = false;
                        img.color = online ? Color.white : new Color(0.55f, 0.55f, 0.55f, 1f);

                        // 非交互：不添加 Button

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
            var toRemove = new List<long>();
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
    public Vector2 WorldToMapPos(Vector2 world)
    {
        EnsureTypeCache();
        try
        {
            var mp = GetMapPanelInstance();
            if (mp == null) return world * 0.5f;
            Vector2 offset = Vector2.zero;
            try
            {
                if (_fi_worldOffset != null)
                {
                    var v = _fi_worldOffset.GetValue(null);
                    if (v is Vector2 vv) offset = vv;
                    else if (v is Vector3 v3) offset = new Vector2(v3.x, v3.y);
                    else try { offset = (Vector2)v; } catch {}
                }
            } catch {}
            float scale = 0f;
            Vector2 center = Vector2.zero;
            float mapWidth = 0f, mapHeight = 0f;
            try { if (_fi_mapScale != null) scale = Convert.ToSingle(_fi_mapScale.GetValue(mp)); else scale = Convert.ToSingle(RGet(mp, "mapScaleFloat")); } catch {}
            try
            {
                if (_fi_center != null)
                {
                    var c = _fi_center.GetValue(mp);
                    if (c is Vector2 cv2) center = cv2;
                    else if (c is Vector3 cv3) center = new Vector2(cv3.x, cv3.y);
                    else try { center = (Vector2)c; } catch {}
                }
                else
                {
                    var c = RGet(mp, "centerPoint");
                    if (c is Vector2 cv2) center = cv2;
                    else if (c is Vector3 cv3) center = new Vector2(cv3.x, cv3.y);
                }
            } catch {}
            try { if (_fi_mapWidth != null) mapWidth = Convert.ToSingle(_fi_mapWidth.GetValue(mp)); else mapWidth = Convert.ToSingle(RGet(mp, "mapWidth")); } catch {}
            try { if (_fi_mapHeight != null) mapHeight = Convert.ToSingle(_fi_mapHeight.GetValue(mp)); else mapHeight = Convert.ToSingle(RGet(mp, "mapHeight")); } catch {}
            if (scale == 0f || float.IsNaN(scale) || float.IsInfinity(scale))
            {
                if (mapWidth > 1f)
                {
                    scale = mapWidth / 2048f;
                    Plugin.L.LogWarning($"[TS][Map] mapScaleFloat==0 fallback mapWidth/2048={scale:F4}");
                }
                else
                {
                    Plugin.L.LogWarning($"[TS][Map] 反射失败 scale=0 center={center} mapSize={mapWidth}x{mapHeight} fallback world*0.5");
                    return world * 0.5f;
                }
            }
            Vector2 anchored = (world - offset) * scale + center;
            return anchored;
        }
        catch (Exception ex)
        {
            Plugin.L.LogWarning($"[TS][Map] WorldToMapPos 失败 fallback world*0.5: {ex.Message.Split('\n')[0]}");
            return world * 0.5f;
        }
    }

    // 兼容 spec 命名：WorldToMapPos(Vector2) 的包装
    private Vector2 WorldToMap(Vector2 world, object mapPanel) => WorldToMapPos(world);

    // ===== OnMarkerClick =====
    [HideFromIl2Cpp]
    public void OnMarkerClick(TerrainObject pad)
    {
        try
        {
            if (PendingConsole == null) { ShowBubble("请先在控制台选择传送"); return; }
            long ck = GetInstanceKey(PendingConsole);
            long pendingPadKey = TeleportBindingManager.GetBoundPad(ck);
            TerrainObject pendingPadObj = pendingPadKey != 0 ? FindByKey(pendingPadKey) as TerrainObject : null;
            if (pendingPadKey == 0 || pendingPadObj == null) { ShowBubble("本站未绑定圆盘"); return; }
            // 发送方需 IsSenderReady(供电+电池≥10000)，接收方仅 IsOnline
            if (!TeleportConsoleSelection.IsSenderReady(pendingPadObj))
            {
                if (!TeleportConsoleSelection.IsOnline(pendingPadObj)) ShowBubble("本站离线（未通电或未绑定）");
                else ShowBubble("本站电量不足（需≥10000）");
                return;
            }
            if (!TeleportConsoleSelection.IsOnline(pad)) { ShowBubble("目的地离线"); return; }
            if (pad == pendingPadObj) { ShowBubble("不能选择本站"); return; }
            long targetKey = GetInstanceKey(pad);
            if (targetKey == pendingPadKey) { ShowBubble("不能选择本站"); return; }

            TeleportConsoleSelection.SetSelected(PendingConsole, pad);
            string name = GetNameForPad(pad);
            ShowBubble($"已选择目的地: {name}");
            Plugin.L.LogInfo($"[TS][Map] 选点 console={ck} -> pad={targetKey} {name}");

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
            } catch (Exception ex2) { Plugin.L.LogWarning($"[TS][Map] 关闭地图异常: {ex2.Message.Split('\n')[0]}"); }
            PendingConsole = null;
            try { TeleportConsoleComputerFix.PendingConsoleForMap = null; TeleportConsoleComputerFix.CurrentConsole = null; } catch {}
            try { if (Instance != null) Instance._pendingClearAt = -1f; } catch {}
        }
        catch (Exception ex) { Plugin.L.LogWarning($"[TS][Map] OnMarkerClick 异常: {ex.Message.Split('\n')[0]}"); }
    }

    // ===== LoadMarkerSprite =====
    private void LoadMarkerSprite()
    {
        if (_markerSprite != null) return;
        string p1 = Path.Combine(Plugin.PluginDir, "textures/marker.png");
        string p2 = Path.Combine(Plugin.PluginDir, "textures/mapmarker.png");
        string p3 = Path.Combine(Plugin.PluginDir, "marker.png");
        string chosen = null;
        foreach (var p in new[] { p1, p2, p3 }) { try { if (File.Exists(p)) { chosen = p; break; } } catch {} }
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
                    Plugin.L.LogInfo($"[TS][Map] 贴图载入 {chosen} {tex.width}x{tex.height} -> {sp.name} (730→36 需 scale≈0.05, 实际按 sizeDelta 36)");
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
        if (pad == null) return "未知";
        try { var r = TeleportStationNameManager.GetName(pad); if (!string.IsNullOrWhiteSpace(r)) return r; } catch {}
        try { if (!string.IsNullOrWhiteSpace(pad.name)) return pad.name; } catch {}
        try { if (pad.attr != null) { var n = RGet(pad.attr, "itemName") as string; if (!string.IsNullOrWhiteSpace(n)) return n; } } catch {}
        return $"传送台 {GetInstanceKey(pad) % 1000}";
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
