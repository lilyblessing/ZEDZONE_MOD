using System;
using UnityEngine;

namespace NoteTagPlugin;

/// <summary>
/// 备注输入框 UI：拖放命名牌到物品上时弹出，可拖动（标题栏）、可调整大小（右下角手柄）、
/// 取消/确定按钮；确定后保存备注到 NoteTagStore 并消耗 1 个命名牌（NameTagOps）。
/// 命名牌注册调度与语言轮询见 NameTagRegistrar。
/// </summary>
public class NoteTagUI : MonoBehaviour
{
    private const float ResizeHandleSize = 18f;
    private const float MinWindowWidth = 300f;
    private const float MinWindowHeight = 180f;
    private static readonly Rect DefaultWindowRect = new Rect(400f, 180f, 440f, 260f);

    /// <summary>场景中的 NoteTagUI 实例（拖放交互通过静态入口打开输入框）。</summary>
    public static NoteTagUI Instance;

    private bool _windowOpen;
    private Rect _windowRect = DefaultWindowRect;
    private string _editText = "";
    private ItemData _targetItem;
    private ItemData _sourceItem; // 拖放来源命名牌（ItemData 引用，比格子 BasicItemUI 稳定）
    private object _sourceInv;   // 拖放前源物品所属 InventoryData（RestoreDrag 后可能丢失）
    private bool _resizing;
    private bool _dragging;

    private Font _font;
    private bool _fontReady;
    private bool _stylesReady;
    private GUIStyle _textAreaStyle;
    private GUIStyle _buttonStyle;
    private GUIStyle _windowStyle;
    private GUIStyle _labelStyle;

    private void Awake()
    {
        Instance = this;
    }

    /// <summary>拖放交互入口：为目标物品打开备注编辑器。</summary>
    public static void OpenForItem(ItemData target, BasicItemUI sourceUI)
    {
        if (Instance == null)
        {
            Plugin.L.LogError("NoteTagUI 实例不存在");
            return;
        }
        Instance.OpenEditorFor(target, sourceUI);
    }

    private void Update()
    {
        // 输入框打开时支持 Escape 关闭（不保存）
        if (_windowOpen && Input.GetKeyDown(KeyCode.Escape))
        {
            CloseEditor(false);
        }
    }

    /// <summary>统一打开备注编辑器：target 为目标物品，sourceUI 为拖放来源（命名牌格子，可为 null）。</summary>
    private void OpenEditorFor(ItemData target, BasicItemUI sourceUI)
    {
        if (target == null)
        {
            Plugin.L.LogWarning("目标物品没有 ItemData");
            return;
        }
        _targetItem = target;
        _sourceItem = sourceUI?.itemdata;   // 记录 ItemData 引用（格子 RestoreDrag 后可能重建失效）
        _sourceInv = sourceUI?.itemdata?.inventoryData; // RestoreDrag 前物品还在背包，inventoryData 有效
        _editText = NoteTagStore.Get(_targetItem);
        _windowOpen = true;
        try { Input.imeCompositionMode = IMECompositionMode.On; } catch { }
        Plugin.L.LogInfo($"打开备注编辑: 物品={NameTagOps.GetItemName(_targetItem)} Ptr=0x{_targetItem.Pointer.ToInt64():X} 已有备注={NoteTagStore.Has(_targetItem)} 来源={(sourceUI != null ? "拖放" : "快捷键")}");
    }

    private void CloseEditor(bool save)
    {
        if (!_windowOpen) return;
        if (save && _targetItem != null)
        {
            NoteTagStore.Set(_targetItem, _editText);
            TooltipPatcher.InvalidateCache(); // 备注变更后使 tooltip 缓存失效
            Plugin.L.LogInfo($"已保存备注 ({_editText.Length} 字符): {NameTagOps.GetItemName(_targetItem)}");
            // 拖放流程：保存成功后消耗 1 个命名牌（按 ItemData 引用，格子可能已重建）
            if (_sourceItem != null)
                NameTagOps.ConsumeNameTag(_sourceItem, _sourceInv);
        }
        _windowOpen = false;
        _targetItem = null;
        _sourceItem = null;
        _sourceInv = null;
    }

    private void EnsureStyles()
    {
        // P1-3: 样式与字体只初始化一次（OnGUI 每帧触发 Layout/Repaint 多个事件，
        // 避免每帧重复 GUI.skin 访问与 font 赋值）
        if (_stylesReady) return;

        // 中文字体：IL2CPP 下 CreateDynamicFontFromOSFont 不可用（运行时无该 API），
        // 改用游戏自带的 UI 字体（游戏为中文 UI，字体必然支持中文）。
        if (_font == null && !_fontReady)
        {
            _fontReady = true;
            _font = FindGameFont();
            if (_font != null) Plugin.L.LogInfo($"使用游戏字体: {_font.name}");
            else Plugin.L.LogWarning("未找到可用中文字体，输入框中文可能显示为方块（tooltip 备注不受影响，走游戏字体渲染）");
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
        _stylesReady = true;
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
            Plugin.L.LogInfo($"取 DescriptionTipPanel 字体失败: {e.Message}");
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
            Plugin.L.LogInfo($"FindObjectsOfTypeAll 失败: {e.Message}");
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
            _windowRect.width = Mathf.Max(MinWindowWidth, ev.mousePosition.x - _windowRect.x);
            _windowRect.height = Mathf.Max(MinWindowHeight, ev.mousePosition.y - _windowRect.y);
            ev.Use();
        }

        // ---- 窗口内容（组内局部坐标） ----
        GUI.BeginGroup(_windowRect);
        GUI.Box(new Rect(0f, 0f, _windowRect.width, _windowRect.height), GUIContent.none, _windowStyle);
        GUI.Label(new Rect(8f, 4f, _windowRect.width - 16f, 20f), GameLocale.T("为物品添加备注", "Add Note to Item"), _labelStyle);
        GUI.Label(new Rect(8f, 26f, _windowRect.width - 16f, 20f), GameLocale.T("物品：", "Item: ") + NameTagOps.GetItemName(_targetItem), _labelStyle);

        float areaHeight = Mathf.Max(40f, _windowRect.height - 122f);
        _editText = GUI.TextArea(new Rect(8f, 50f, _windowRect.width - 16f, areaHeight), _editText, _textAreaStyle);

        if (GUI.Button(new Rect(_windowRect.width - 108f, _windowRect.height - 34f, 48f, 26f), GameLocale.T("取消", "Cancel"), _buttonStyle))
        {
            CloseEditor(false);
            GUI.EndGroup();
            return;
        }
        if (GUI.Button(new Rect(_windowRect.width - 54f, _windowRect.height - 34f, 46f, 26f), GameLocale.T("确定", "OK"), _buttonStyle))
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
