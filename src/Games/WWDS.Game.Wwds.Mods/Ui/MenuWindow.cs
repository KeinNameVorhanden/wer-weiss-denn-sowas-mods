using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace WWDS.Game.Wwds.Mods.Ui;

/// <summary>A tab of a <see cref="MenuWindow"/>; rows are built each time the tab is shown.</summary>
public sealed class MenuPage
{
    public MenuPage(string title, Func<IEnumerable<Widget>> build)
    {
        Title = title;
        Build = build;
    }

    public string Title { get; }

    public Func<IEnumerable<Widget>> Build { get; }
}

/// <summary>
/// Game-styled menu window: dimmed backdrop, panel with title, tab column and a scrolling row list,
/// footer with hints, plus a modal confirm dialog. Keyboard (arrows/WASD, Enter, Esc, Q/E, Tab),
/// XInput pads (stick/D-pad, A, B, LB/RB, Start) and mouse (hover, click, drag, wheel, right click = back)
/// all drive the same focus model.
/// </summary>
public sealed class MenuWindow
{
    private readonly float _panelWidth;
    private float _panelHeight;
    private readonly float _tabWidth;
    private readonly float _listWidth;
    private readonly float _listTop = 130;
    private float _listHeight;

    private const string DefaultHint = "Pfeile/Stick: Auswahl   Enter/A: OK   Links/Rechts: Wert   Q/E, LB/RB: Reiter   Esc/B: Schließen";
    private const string CompactHint = "Pfeile/Stick: Auswahl   Enter/A: OK   Esc/B: Schließen";
    private const float CompactMaxHeight = 760;

    private readonly UiRoot _ui;
    private readonly MenuInput _input = new();
    private readonly string _title;
    private readonly List<MenuPage> _pages = new();
    private readonly List<Widget> _rows = new();
    private readonly List<TabView> _tabs = new();
    private RectTransform? _root;
    private RectTransform? _panel;
    private RectTransform? _tabColumn;
    private RectTransform? _viewport;
    private RectTransform? _content;
    private const float CloseWidth = 230;
    private TextMeshProUGUI? _footer;
    private RectTransform? _close;
    private Image? _closeBg;
    private TextMeshProUGUI? _closeText;
    private bool _closeHover;
    private TextMeshProUGUI? _pageTitle;
    private RectTransform? _dialog;
    private readonly List<Widget> _dialogRows = new();
    private int _dialogFocus;
    private int _page;
    private int _focus = -1;
    private float _scroll;
    private Vector3 _lastMouse;
    private Widget? _dragging;
    private bool _dirtyLayout;
    private int _openedFrame;

    /// <param name="compact">Small single-page window without the tab column (ESC menu).</param>
    public MenuWindow(UiRoot ui, string title, bool compact = false)
    {
        _ui = ui;
        _title = title;
        Compact = compact;
        _panelWidth = compact ? 860 : 1520;
        _panelHeight = compact ? 760 : 900;
        _tabWidth = compact ? 0 : 330;
        _listWidth = compact ? _panelWidth - 100 : _panelWidth - _tabWidth - 90;
        _listHeight = _panelHeight - _listTop - 90;
    }

    public bool Compact { get; }

    /// <summary>Text of the corner button: "Schließen", or "Zurück" when closing returns to another menu.</summary>
    public string CloseLabel { get; set; } = "Schließen";

    /// <summary>Requested size factor (UI scale); capped so the panel always fits the screen.</summary>
    public Func<float>? Scale { get; set; }

    public bool IsOpen { get; private set; }

    /// <summary>Raised when the window closes (Escape on the top level, Start, or <see cref="Close"/>).</summary>
    public event Action? Closed;

    public IList<MenuPage> Pages => _pages;

    /// <summary>Rows of the current page (self-test and diagnostics).</summary>
    public int RowCount => _rows.Count;

    /// <summary>Last page build error, if any.</summary>
    public string? LastError { get; private set; }

    public string? CurrentPageTitle => _page >= 0 && _page < _pages.Count ? _pages[_page].Title : null;

    public void Open(int page = 0)
    {
        if (_root == null) BuildFrame();
        _root!.gameObject.SetActive(true);
        IsOpen = true;
        BuildTabs();
        ShowPage(Mathf.Clamp(page, 0, Math.Max(0, _pages.Count - 1)));
        _lastMouse = Input.mousePosition;
        _openedFrame = Time.frameCount;
        ApplyScale();
        if (_closeText != null) UiRoot.SetText(_closeText, CloseLabel);
    }

    public void OpenPage(string title)
    {
        var i = _pages.FindIndex(p => p.Title == title);
        Open(i < 0 ? 0 : i);
    }

    public void Close()
    {
        if (!IsOpen) return;
        CloseDialog();
        IsOpen = false;
        if (_root != null) _root.gameObject.SetActive(false);
        Closed?.Invoke();
    }

    /// <summary>Rebuilds the current page (call after the model changed shape, e.g. a list grew).</summary>
    public void Rebuild()
    {
        if (IsOpen) ShowPage(_page, keepFocus: true);
    }

    /// <summary>Modal yes/no question; <paramref name="onYes"/> runs on confirm.</summary>
    public void Confirm(string message, string yes, Action onYes, string no = "Abbrechen")
    {
        CloseDialog();
        _dialog = UiRoot.Child(_root!, "Dialog");
        UiRoot.Stretch(_dialog);
        var dim = _ui.Image(_dialog, "Dim", null, GameStyle.Dim, false);
        UiRoot.Stretch(dim.rectTransform);
        dim.raycastTarget = true;
        var box = UiRoot.Child(_dialog, "Box");
        box.anchorMin = box.anchorMax = box.pivot = new Vector2(0.5f, 0.5f);
        box.sizeDelta = new Vector2(900, 400);
        Frame(box);
        var text = _ui.Text(box, "Message", message, 36, GameStyle.White, false, TextAlignmentOptions.Center);
        text.enableWordWrapping = true;
        UiRoot.Place(text.rectTransform, 50, 40, 800, 170);
        _dialogRows.Clear();
        _dialogRows.Add(new ButtonWidget(yes, () =>
        {
            CloseDialog();
            onYes();
        }));
        _dialogRows.Add(new ButtonWidget(no, CloseDialog));
        var y = 220f;
        foreach (var row in _dialogRows)
        {
            row.Build(_ui, box, 700);
            UiRoot.Place(row.Root!, 100, y, 700, Widget.RowHeight);
            y += Widget.RowHeight + 8;
        }

        _dialogFocus = 1;
        FocusDialog(1);
        UiRoot.CenterAll(box);
    }

    private void CloseDialog()
    {
        if (_dialog != null) UnityEngine.Object.Destroy(_dialog.gameObject);
        _dialog = null;
        _dialogRows.Clear();
    }

    // ---- frame -------------------------------------------------------------------------------------

    private void BuildFrame()
    {
        _root = UiRoot.Child(_ui.Rect, "Menu:" + _title);
        UiRoot.Stretch(_root);
        var dim = _ui.Image(_root, "Dim", null, GameStyle.Dim, false);
        UiRoot.Stretch(dim.rectTransform);
        dim.raycastTarget = true;

        _panel = UiRoot.Child(_root, "Panel");
        _panel.anchorMin = _panel.anchorMax = _panel.pivot = new Vector2(0.5f, 0.5f);
        _panel.sizeDelta = new Vector2(_panelWidth, _panelHeight);
        Frame(_panel);

        var title = _ui.Text(_panel, "Title", _title, 56, GameStyle.White, true);
        UiRoot.Place(title.rectTransform, 50, 30, Compact ? _listWidth - CloseWidth - 20 : _tabWidth + 200, 70);
        _pageTitle = _ui.Text(_panel, "PageTitle", string.Empty, 44, GameStyle.Accent, true);
        UiRoot.Place(_pageTitle.rectTransform, _tabWidth + 60, 38, _listWidth - CloseWidth - 20, 64);

        // Visible back/close button for mouse users (Escape, B and right click do the same).
        _close = UiRoot.Child(_panel, "Close");
        _close.anchorMin = _close.anchorMax = _close.pivot = new Vector2(1, 1);
        _close.anchoredPosition = new Vector2(-40, -36);
        _close.sizeDelta = new Vector2(CloseWidth, 58);
        _closeBg = _ui.Image(_close, "Bg", "TransparentBoxNormal", GameStyle.RowFallback, true);
        UiRoot.Stretch(_closeBg.rectTransform);
        _closeText = _ui.Text(_close, "Text", CloseLabel, 30, GameStyle.White, false, TextAlignmentOptions.Midline);
        UiRoot.Place(_closeText.rectTransform, 10, 0, CloseWidth - 20, 58);
        UiRoot.CenterCaps(_closeText);
        _pageTitle.gameObject.SetActive(!Compact);

        _tabColumn = UiRoot.Child(_panel, "Tabs");
        UiRoot.Place(_tabColumn, 40, _listTop, Math.Max(0, _tabWidth - 20), _listHeight);
        _tabColumn.gameObject.SetActive(!Compact);

        _viewport = UiRoot.Child(_panel, "Viewport");
        UiRoot.Place(_viewport, Compact ? 50 : _tabWidth + 50, _listTop, _listWidth, _listHeight);
        _viewport.gameObject.AddComponent<RectMask2D>();
        _content = UiRoot.Child(_viewport, "Content");
        UiRoot.Place(_content, 0, 0, _listWidth, _listHeight);

        _footer = _ui.Text(_panel, "Footer", string.Empty, 26, GameStyle.Muted);
        _footer.enableWordWrapping = true;
        UiRoot.Place(_footer.rectTransform, 50, _panelHeight - 80, _panelWidth - 100, 64);
        _root.gameObject.SetActive(false);
    }

    /// <summary>Compact windows (ESC menu, questions) grow and shrink with their rows.</summary>
    private void FitCompact(float contentHeight)
    {
        var list = Mathf.Min(contentHeight, CompactMaxHeight - _listTop - 110);
        var panel = _listTop + list + 110;
        if (Mathf.Abs(panel - _panelHeight) < 0.5f) return;
        _panelHeight = panel;
        _listHeight = list;
        _panel!.sizeDelta = new Vector2(_panelWidth, _panelHeight);
        _viewport!.sizeDelta = new Vector2(_listWidth, _listHeight);
        _content!.sizeDelta = new Vector2(_listWidth, _listHeight);
        UiRoot.Place(_footer!.rectTransform, 50, _panelHeight - 92, _panelWidth - 100, 76);
    }

    private void ApplyScale()
    {
        if (_panel == null) return;
        var canvas = _ui.Rect.rect;
        var fit = Mathf.Min(canvas.width / (_panelWidth + 40), canvas.height / (_panelHeight + 40));
        var s = Mathf.Min(Scale?.Invoke() ?? 1f, fit);
        var v = new Vector3(s, s, 1);
        if (_panel.localScale != v) _panel.localScale = v;
        if (_dialog != null)
        {
            var box = _dialog.Find("Box");
            if (box != null && box.localScale != v) box.localScale = v;
        }
    }

    /// <summary>Game dialog look: CategorySelectionBG body with the glowing border on top.</summary>
    private void Frame(RectTransform box)
    {
        var solid = _ui.Image(box, "Solid", null, GameStyle.PanelFallback, false);
        UiRoot.Stretch(solid.rectTransform);
        solid.raycastTarget = true;
        var bg = _ui.Image(box, "Background", "CategorySelectionBG", GameStyle.PanelFallback, false);
        UiRoot.Stretch(bg.rectTransform);
        bg.raycastTarget = true;
        var shade = _ui.Image(box, "Shade", null, new Color(0, 0, 0, 0.35f), false);
        UiRoot.Stretch(shade.rectTransform);
        if (_ui.Style.Sprite("GlowingBorder") != null)
        {
            var border = _ui.Image(box, "Border", "GlowingBorder", Color.clear, true);
            UiRoot.Stretch(border.rectTransform, -12);
        }
    }

    private void BuildTabs()
    {
        if (Compact) return;
        foreach (Transform child in _tabColumn!) UnityEngine.Object.Destroy(child.gameObject);
        _tabs.Clear();
        var y = 0f;
        foreach (var page in _pages)
        {
            var rt = UiRoot.Child(_tabColumn, "Tab:" + page.Title);
            UiRoot.Place(rt, 0, y, _tabWidth - 20, 58);
            var bg = _ui.Image(rt, "Bg", "TransparentBoxNormal", GameStyle.RowFallback, true);
            UiRoot.Stretch(bg.rectTransform);
            var text = _ui.Text(rt, "Text", page.Title, 32, GameStyle.White);
            UiRoot.Place(text.rectTransform, 20, 0, _tabWidth - 60, 58);
            _tabs.Add(new TabView(rt, bg, text));
            y += 66;
        }

        UiRoot.CenterAll(_tabColumn);
    }

    private void ShowPage(int index, bool keepFocus = false)
    {
        var oldFocus = _focus;
        foreach (Transform child in _content!) UnityEngine.Object.Destroy(child.gameObject);
        _rows.Clear();
        _page = index;
        _dragging = null;
        for (var i = 0; i < _tabs.Count; i++)
        {
            _ui.SetFocusLook(_tabs[i].Bg, i == _page);
            _tabs[i].Text.color = i == _page ? GameStyle.Accent : GameStyle.White;
        }

        if (_pages.Count == 0) return;
        var page = _pages[_page];
        if (_pageTitle != null) _pageTitle.text = page.Title;
        try
        {
            _rows.AddRange(page.Build());
        }
        catch (Exception ex)
        {
            LastError = page.Title + ": " + ex;
            _rows.Clear();
            _rows.Add(new InfoWidget("Seite konnte nicht aufgebaut werden: " + ex.Message, 3));
        }

        foreach (var row in _rows) row.Build(_ui, _content, _listWidth);
        _scroll = keepFocus ? _scroll : 0;
        _dirtyLayout = true;
        Layout();
        UiRoot.CenterAll(_content);
        _focus = -1;
        var target = keepFocus && oldFocus >= 0 ? Math.Min(oldFocus, _rows.Count - 1) : 0;
        MoveFocusTo(NextFocusable(target - 1, 1));
    }

    private void Layout()
    {
        var y = 0f;
        foreach (var row in _rows)
        {
            var visible = row.IsVisible;
            if (row.Root!.gameObject.activeSelf != visible)
            {
                row.Root.gameObject.SetActive(visible);
                if (visible) UiRoot.CenterAll(row.Root);
                _dirtyLayout = true;
            }

            if (!visible) continue;
            UiRoot.Place(row.Root, 0, y, _listWidth, row.Height);
            y += row.Height + 6;
        }

        if (Compact) FitCompact(y);
        var max = Mathf.Max(0, y - _listHeight);
        _scroll = Mathf.Clamp(_scroll, 0, max);
        _content!.anchoredPosition = new Vector2(0, _scroll);
        _dirtyLayout = false;
    }

    // ---- per frame ---------------------------------------------------------------------------------

    /// <summary>Runs input, mouse and refresh. Returns false once the window closed.</summary>
    public bool Tick()
    {
        if (!IsOpen) return false;
        ApplyScale();
        Cursor.visible = true;
        Cursor.lockState = CursorLockMode.None;
        if (_dialog != null)
        {
            TickDialog();
            return IsOpen;
        }

        foreach (var row in _rows)
        {
            if (row.Root != null && row.Root.gameObject.activeSelf) row.Refresh();
        }

        Layout();
        if (Time.frameCount == _openedFrame) return IsOpen;
        var editing = _focus >= 0 && _focus < _rows.Count && _rows[_focus].Capturing;
        if (editing)
        {
            _rows[_focus].CaptureInput();
            return IsOpen;
        }

        HandleMouse();
        switch (_input.Poll())
        {
            case MenuCommand.Up:
                MoveFocusTo(NextFocusable(_focus, -1));
                break;
            case MenuCommand.Down:
                MoveFocusTo(NextFocusable(_focus, 1));
                break;
            case MenuCommand.Left:
                Adjust(-1);
                break;
            case MenuCommand.Right:
                Adjust(1);
                break;
            case MenuCommand.Submit:
                SubmitFocused();
                break;
            case MenuCommand.PrevTab:
                SwitchTab(-1);
                break;
            case MenuCommand.NextTab:
                SwitchTab(1);
                break;
            case MenuCommand.Back:
            case MenuCommand.Close:
                Close();
                break;
        }

        return IsOpen;
    }

    private void TickDialog()
    {
        foreach (var row in _dialogRows) row.Refresh();
        if (Input.GetMouseButtonDown(0))
        {
            for (var i = 0; i < _dialogRows.Count; i++)
            {
                if (Hit(_dialogRows[i].Root!, out _))
                {
                    _ui.PlayClick();
                    _dialogRows[i].Submit();
                    return;
                }
            }
        }

        if (Input.GetMouseButtonDown(1))
        {
            CloseDialog();
            return;
        }

        if (Input.mousePosition != _lastMouse)
        {
            _lastMouse = Input.mousePosition;
            for (var i = 0; i < _dialogRows.Count; i++)
            {
                if (i != _dialogFocus && Hit(_dialogRows[i].Root!, out _)) FocusDialog(i);
            }
        }

        switch (_input.Poll())
        {
            case MenuCommand.Up:
            case MenuCommand.Left:
                FocusDialog(0);
                break;
            case MenuCommand.Down:
            case MenuCommand.Right:
                FocusDialog(1);
                break;
            case MenuCommand.Submit:
                _ui.PlayClick();
                _dialogRows[_dialogFocus].Submit();
                break;
            case MenuCommand.Back:
            case MenuCommand.Close:
                CloseDialog();
                break;
        }
    }

    private void FocusDialog(int index)
    {
        if (_dialogRows.Count == 0) return;
        if (index != _dialogFocus) _ui.PlayHighlight();
        _dialogFocus = index;
        for (var i = 0; i < _dialogRows.Count; i++) _dialogRows[i].SetFocus(i == index);
    }

    private void HandleMouse()
    {
        var mouse = Input.mousePosition;
        var moved = mouse != _lastMouse;
        _lastMouse = mouse;
        var wheel = Input.mouseScrollDelta.y;
        if (Math.Abs(wheel) > 0.01f && Hit(_viewport!, out _))
        {
            _scroll -= wheel * 70;
            _dirtyLayout = true;
        }

        if (_dragging != null)
        {
            if (Input.GetMouseButton(0) && _dragging.Root != null)
            {
                Hit(_dragging.Root, out var x);
                _dragging.Drag(x);
            }
            else
            {
                _dragging = null;
            }

            return;
        }

        var overClose = _close != null && Hit(_close, out _);
        if (overClose != _closeHover && _closeBg != null)
        {
            _closeHover = overClose;
            _ui.SetFocusLook(_closeBg, overClose);
            if (overClose) _ui.PlayHighlight();
        }

        if (overClose && Input.GetMouseButtonDown(0))
        {
            _ui.PlayClick();
            Close();
            return;
        }

        if (Input.GetMouseButtonDown(1))
        {
            Close();
            return;
        }

        for (var t = 0; t < _tabs.Count; t++)
        {
            if (!Hit(_tabs[t].Rect, out _)) continue;
            if (Input.GetMouseButtonDown(0) && t != _page)
            {
                _ui.PlayClick();
                ShowPage(t);
            }

            return;
        }

        if (!Hit(_viewport!, out _)) return;
        for (var i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            if (!row.Focusable || !row.Root!.gameObject.activeSelf || !Hit(row.Root, out var x)) continue;
            if (moved && i != _focus) MoveFocusTo(i);
            if (Input.GetMouseButtonDown(0))
            {
                if (i != _focus) MoveFocusTo(i);
                _ui.PlayClick();
                row.Click(x);
                if (row is SliderWidget) _dragging = row;
            }

            return;
        }
    }

    /// <summary>True if the mouse is over <paramref name="rect"/>; x = normalized horizontal position.</summary>
    private static bool Hit(RectTransform rect, out float x)
    {
        x = 0;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, Input.mousePosition, null, out var local)) return false;
        var r = rect.rect;
        if (!r.Contains(local)) return false;
        x = (local.x - r.xMin) / r.width;
        return true;
    }

    private void SwitchTab(int direction)
    {
        if (_pages.Count < 2) return;
        _ui.PlayHighlight();
        ShowPage((_page + direction + _pages.Count) % _pages.Count);
    }

    private void Adjust(int direction)
    {
        if (_focus < 0 || _focus >= _rows.Count) return;
        _rows[_focus].Adjust(direction);
        _ui.PlayHighlight();
    }

    private void SubmitFocused()
    {
        if (_focus < 0 || _focus >= _rows.Count) return;
        _ui.PlayClick();
        _rows[_focus].Submit();
    }

    private int NextFocusable(int from, int direction)
    {
        for (var i = from + direction; i >= 0 && i < _rows.Count; i += direction)
        {
            if (_rows[i].Focusable && _rows[i].IsVisible) return i;
        }

        return from;
    }

    private void MoveFocusTo(int index)
    {
        if (index == _focus || index < 0 || index >= _rows.Count) return;
        if (_focus >= 0 && _focus < _rows.Count) _rows[_focus].SetFocus(false);
        _focus = index;
        var row = _rows[index];
        row.SetFocus(true);
        _ui.PlayHighlight();
        if (_footer != null) UiRoot.SetText(_footer, row.Hint ?? (Compact ? CompactHint : DefaultHint));
        EnsureVisible(row);
    }

    private void EnsureVisible(Widget row)
    {
        if (_dirtyLayout) Layout();
        var top = -row.Root!.anchoredPosition.y;
        var bottom = top + row.Height;
        if (top < _scroll) _scroll = top;
        else if (bottom > _scroll + _listHeight) _scroll = bottom - _listHeight;
        _content!.anchoredPosition = new Vector2(0, _scroll);
    }

    private sealed class TabView
    {
        public TabView(RectTransform rect, Image bg, TextMeshProUGUI text)
        {
            Rect = rect;
            Bg = bg;
            Text = text;
        }

        public RectTransform Rect { get; }

        public Image Bg { get; }

        public TextMeshProUGUI Text { get; }
    }
}
