using System;
using System.Reflection;
using UnityEngine;

namespace NoteTagPlugin;

/// <summary>
/// 测试版 UI：打开背包并悬停物品时，按小键盘 + 弹出备注输入框。
/// 输入框：可拖动（标题栏）、可调整大小（右下角手柄）、取消/确定按钮。
/// 备注按 ItemData 实例指针绑定（NoteTagStore）。
/// </summary>
public class NoteTagUI : MonoBehaviour
{
    private const int WindowId = 847261;
    private const float ResizeHandleSize = 18f;

    /// <summary>场景中的 NoteTagUI 实例（拖放交互通过静态入口打开输入框）。</summary>
    public static NoteTagUI Instance;

    private static readonly PropertyInfo IsHoverProp =
        typeof(BasicItemUI).GetProperty("isHover",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private bool _windowOpen;
    private Rect _windowRect = new Rect(400f, 180f, 440f, 260f);
    private string _editText = "";
    private ItemData _targetItem;
    private BasicItemUI _sourceUI; // 拖放来源（命名牌所在格子），快捷键流程为 null
    private bool _resizing;
    private bool _dragging;
    private bool _selfChecked;
    private float _probeTimer = 10f;
    private bool _probeDone;
    private int _registerTries;

    private void Awake()
    {
        Instance = this;
    }

    /// <summary>拖放交互入口：为目标物品打开备注编辑器。</summary>
    public static void OpenForItem(ItemData target, BasicItemUI sourceUI)
    {
        if (Instance == null)
        {
            Plugin.L.LogError("[NoteTag] NoteTagUI 实例不存在");
            return;
        }
        Instance.OpenEditorFor(target, sourceUI);
    }

    private Font _font;
    private bool _fontReady;
    private GUIStyle _textAreaStyle;
    private GUIStyle _buttonStyle;
    private GUIStyle _windowStyle;
    private GUIStyle _labelStyle;

    private void Update()
    {
        if (!_selfChecked)
        {
            _selfChecked = true;
            SelfCheck();
        }

        // 延迟探查物品系统 + 注册命名牌（等 ItemManager 初始化，最多重试 6 次）
        if (!_probeDone)
        {
            _probeTimer -= Time.deltaTime;
            if (_probeTimer <= 0f)
            {
                ItemSystemProbe.RunOnce();
                if (NameTagItem.Register())
                {
                    _probeDone = true;
                }
                else if (++_registerTries >= 6)
                {
                    _probeDone = true;
                    Plugin.L.LogError("[NoteTag] 命名牌注册多次尝试仍失败");
                }
                else
                {
                    _probeTimer = 5f;
                }
            }
        }

        if (!_windowOpen && Input.GetKeyDown(KeyCode.KeypadPlus))
        {
            var item = FindHoveredItemUI();
            if (item == null)
            {
                Plugin.L.LogWarning("[NoteTag] 未检测到鼠标悬停的物品（请先打开背包并把鼠标移到物品上）");
                return;
            }
            OpenEditor(item);
        }

        if (_windowOpen && Input.GetKeyDown(KeyCode.Escape))
        {
            CloseEditor(false);
        }
    }

    private void SelfCheck()
    {
        try
        {
            var list = BasicItemUI.ActiveObjects;
            int count = list != null ? list.Count : -1;
            Plugin.L.LogInfo($"[NoteTag] 自检: ActiveObjects={count}, 已存备注={NoteTagStore.Count}, isHover反射={(IsHoverProp != null ? "OK" : "FAIL")}");
        }
        catch (Exception e)
        {
            Plugin.L.LogError($"[NoteTag] 自检失败: {e}");
        }
    }

    private BasicItemUI FindHoveredItemUI()
    {
        var list = BasicItemUI.ActiveObjects;
        if (list == null || IsHoverProp == null) return null;
        try
        {
            for (int i = 0; i < list.Count; i++)
            {
                var ui = list[i];
                if (ui == null) continue;
                try
                {
                    if ((bool)IsHoverProp.GetValue(ui))
                        return ui;
                }
                catch { /* 单个格子异常忽略 */ }
            }
        }
        catch (Exception e)
        {
            Plugin.L.LogError($"[NoteTag] 遍历 ActiveObjects 失败: {e}");
        }
        return null;
    }

    private void OpenEditor(BasicItemUI itemUI)
    {
        if (itemUI == null) return;
        OpenEditorFor(itemUI.itemdata, null);
    }

    /// <summary>统一打开备注编辑器：target 为目标物品，sourceUI 为拖放来源（命名牌格子，可为 null）。</summary>
    private void OpenEditorFor(ItemData target, BasicItemUI sourceUI)
    {
        if (target == null)
        {
            Plugin.L.LogWarning("[NoteTag] 目标物品没有 ItemData");
            return;
        }
        _targetItem = target;
        _sourceUI = sourceUI;
        _editText = NoteTagStore.Get(_targetItem);
        _windowOpen = true;
        try { Input.imeCompositionMode = IMECompositionMode.On; } catch { }
        Plugin.L.LogInfo($"[NoteTag] 打开备注编辑: 物品={GetItemName(_targetItem)} Ptr=0x{_targetItem.Pointer.ToInt64():X} 已有备注={NoteTagStore.Has(_targetItem)} 来源={(sourceUI != null ? "拖放" : "快捷键")}");
    }

    private void CloseEditor(bool save)
    {
        if (!_windowOpen) return;
        if (save && _targetItem != null)
        {
            NoteTagStore.Set(_targetItem, _editText);
            TooltipPatcher.InvalidateCache(); // 备注变更后使 tooltip 缓存失效
            Plugin.L.LogInfo($"[NoteTag] 已保存备注 ({_editText.Length} 字符): {GetItemName(_targetItem)}");
            // 拖放流程：保存成功后消耗 1 个命名牌
            if (_sourceUI != null)
                ConsumeNameTag(_sourceUI);
        }
        _windowOpen = false;
        _targetItem = null;
        _sourceUI = null;
    }

    /// <summary>消耗 1 个命名牌；数量耗尽时用游戏原生移除逻辑清空格子。</summary>
    private static void ConsumeNameTag(BasicItemUI source)
    {
        try
        {
            if (source == null) return;
            var item = source.itemdata;
            if (item == null)
            {
                Plugin.L.LogWarning("[NoteTag] 消耗失败: 源格子物品为空");
                return;
            }

            if (item.itemNumberFloat > 1f)
            {
                // 数量 >1：减 1 并刷新数量显示
                item.itemNumberFloat -= 1f;
                source.RefreshItemNumber();
            }
            else
            {
                // 只剩 1 个：整体移除（数据 + UI 刷新）
                var inv = item.inventoryData;
                bool removed = false;
                if (inv != null)
                {
                    try { removed = inv.RemoveItem(item, true); } catch (Exception e) { Plugin.L.LogError($"[NoteTag] RemoveItem(true) 异常: {e.Message}"); }
                    if (!removed)
                    {
                        try { removed = inv.RemoveItem(item, false); } catch (Exception e) { Plugin.L.LogError($"[NoteTag] RemoveItem(false) 异常: {e.Message}"); }
                    }
                }
                if (!removed)
                {
                    source.itemdata = null;
                    source.itemdataTemp = null;
                }

                // 主动刷新所属背包面板，清除可能的残留图标
                try
                {
                    var panel = Reflect.Get(source, "inventoryPanel");
                    if (panel != null)
                    {
                        var m = panel.GetType().GetMethod("Refresh",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        m?.Invoke(panel, null);
                    }
                }
                catch (Exception e)
                {
                    Plugin.L.LogError($"[NoteTag] 背包面板刷新失败: {e.Message}");
                }
            }
            Plugin.L.LogInfo("[NoteTag] 已消耗 1 个命名牌");
        }
        catch (Exception e)
        {
            Plugin.L.LogError($"[NoteTag] 消耗命名牌失败: {e}");
        }
    }

    private static string GetItemName(ItemData d)
    {
        try { return d.GetItemName(); }
        catch { return "?"; }
    }

    private void EnsureStyles()
    {
        // 中文字体：IL2CPP 下 CreateDynamicFontFromOSFont 不可用（运行时无该 API），
        // 改用游戏自带的 UI 字体（游戏为中文 UI，字体必然支持中文）。
        if (_font == null && !_fontReady)
        {
            _fontReady = true;
            _font = FindGameFont();
            if (_font != null) Plugin.L.LogInfo($"[NoteTag] 使用游戏字体: {_font.name}");
            else Plugin.L.LogWarning("[NoteTag] 未找到可用中文字体，输入框中文可能显示为方块（tooltip 备注不受影响，走游戏字体渲染）");
        }

        _textAreaStyle = GUI.skin.textArea;
        _buttonStyle = GUI.skin.button;
        _windowStyle = GUI.skin.window;
        _labelStyle = GUI.skin.label;

        if (_font != null)
        {
            _textAreaStyle.font = _font;
            _buttonStyle.font = _font;
            _windowStyle.font = _font;
            _labelStyle.font = _font;
        }
    }

    /// <summary>获取游戏 UI 字体：优先 DescriptionTipPanel 的 Text 字体，其次遍历已加载 Font 资源。</summary>
    private static Font FindGameFont()
    {
        try
        {
            var panel = DescriptionTipPanel.instance;
            if (panel != null && panel.informationText != null && panel.informationText.font != null)
                return panel.informationText.font;
        }
        catch (Exception e)
        {
            Plugin.L.LogInfo($"[NoteTag] 取 DescriptionTipPanel 字体失败: {e.Message}");
        }

        try
        {
            var arr = Resources.FindObjectsOfTypeAll<Font>();
            if (arr != null)
            {
                for (int i = 0; i < arr.Length; i++)
                {
                    var f = arr[i];
                    try { if (f != null && f.dynamic) return f; } catch { }
                }
                for (int i = 0; i < arr.Length; i++)
                {
                    var f = arr[i];
                    if (f != null) return f;
                }
            }
        }
        catch (Exception e)
        {
            Plugin.L.LogInfo($"[NoteTag] FindObjectsOfTypeAll 失败: {e.Message}");
        }
        return null;
    }

    private void OnGUI()
    {
        if (!_windowOpen) return;
        EnsureStyles();

        var ev = Event.current;

        // ---- 标题栏拖动（全局坐标） ----
        var titleBar = new Rect(_windowRect.x, _windowRect.y, _windowRect.width, 24f);
        if (ev.type == EventType.MouseDown && titleBar.Contains(ev.mousePosition))
        {
            _dragging = true;
        }
        else if (ev.type == EventType.MouseUp)
        {
            _dragging = false;
            _resizing = false;
        }
        if (_dragging && ev.type == EventType.MouseDrag)
        {
            _windowRect.position += ev.delta;
            ev.Use();
        }

        // ---- 调整大小（右下角手柄，全局坐标） ----
        var handleGlobal = new Rect(
            _windowRect.x + _windowRect.width - ResizeHandleSize,
            _windowRect.y + _windowRect.height - ResizeHandleSize,
            ResizeHandleSize, ResizeHandleSize);
        if (ev.type == EventType.MouseDown && handleGlobal.Contains(ev.mousePosition))
        {
            _resizing = true;
            ev.Use();
        }
        if (_resizing && ev.type == EventType.MouseDrag)
        {
            _windowRect.width = Mathf.Max(300f, ev.mousePosition.x - _windowRect.x);
            _windowRect.height = Mathf.Max(180f, ev.mousePosition.y - _windowRect.y);
            ev.Use();
        }

        // ---- 窗口内容（组内局部坐标） ----
        GUI.BeginGroup(_windowRect);
        GUI.Box(new Rect(0f, 0f, _windowRect.width, _windowRect.height), GUIContent.none, _windowStyle);
        GUI.Label(new Rect(8f, 4f, _windowRect.width - 16f, 20f), "为物品添加备注", _labelStyle);
        GUI.Label(new Rect(8f, 26f, _windowRect.width - 16f, 20f), "物品：" + GetItemName(_targetItem), _labelStyle);

        float areaHeight = Mathf.Max(40f, _windowRect.height - 122f);
        _editText = GUI.TextArea(new Rect(8f, 50f, _windowRect.width - 16f, areaHeight), _editText, _textAreaStyle);

        if (GUI.Button(new Rect(_windowRect.width - 108f, _windowRect.height - 34f, 48f, 26f), "取消", _buttonStyle))
        {
            CloseEditor(false);
            GUI.EndGroup();
            return;
        }
        if (GUI.Button(new Rect(_windowRect.width - 54f, _windowRect.height - 34f, 46f, 26f), "确定", _buttonStyle))
        {
            CloseEditor(true);
            GUI.EndGroup();
            return;
        }

        GUI.Box(new Rect(_windowRect.width - ResizeHandleSize, _windowRect.height - ResizeHandleSize,
            ResizeHandleSize, ResizeHandleSize), new GUIContent("◢"));
        GUI.EndGroup();
    }
}
