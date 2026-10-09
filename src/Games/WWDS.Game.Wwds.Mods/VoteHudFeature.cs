using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using WWDS.Game.Wwds.Mods.Ui;
using WWDS.ModCore.Config;
using WWDS.ModCore.Events;
using WWDS.ModCore.Features;

namespace WWDS.Game.Wwds.Mods;

/// <summary>
/// The audience vote window in the game's look: title, countdown bar, one row per option with share bar
/// and vote count, the chat command to use, the winner highlighted once locked. Shows itself while an
/// interaction runs (or on F9), can be dragged with the mouse anywhere on screen, remembers its position,
/// can be locked, scaled and placed with a preview from the mod menu.
/// </summary>
public sealed class VoteHudFeature : FeatureBase
{
    public const string FeatureId = "VoteHud";
    private const float Width = 470;
    private const float RowHeight = 40;
    private const float RowsTop = 70;
    private const int MaxRows = 8;

    private static readonly InteractionStatusChanged Sample = new("Vorschau: Welche Antwort?", "Collecting",
        new List<string> { "A  48% (12)  Der Mond", "B  32% (8)  Die Sonne", "C  20% (5)  Ein Komet" }, 23);

    private readonly List<Row> _rows = new();
    private IConfigValue<string> _hotkey = null!;
    private IConfigValue<bool> _auto = null!;
    private IConfigValue<float> _x = null!;
    private IConfigValue<float> _y = null!;
    private IConfigValue<bool> _locked = null!;
    private IConfigValue<float> _opacity = null!;
    private IConfigValue<float> _linger = null!;
    private IConfigValue<bool> _hint = null!;
    private ModUi _ui = null!;
    private RectTransform? _panel;
    private CanvasGroup? _group;
    private TextMeshProUGUI? _title;
    private TextMeshProUGUI? _status;
    private TextMeshProUGUI? _footer;
    private RectTransform? _timerFill;
    private InteractionStatusChanged _current = InteractionStatusChanged.Idle;
    private InteractionStatusChanged? _shown;
    private double _lastActive;
    private bool _forced;
    private double _previewUntil;
    private bool _dragging;
    private Vector2 _dragOffset;
    private float _timerTotal;

    public override string Id => FeatureId;

    public override string DisplayName => "Vote window";

    public override string Description => "Game-styled, movable vote window for audience interactions (F9).";

    protected override string LogCategoryName => "Mods";

    public IConfigValue<bool> Locked => _locked;

    /// <summary>True while the window is on screen.</summary>
    public bool Visible => _panel != null && _panel.gameObject.activeSelf;

    /// <summary>True while the mouse is over the window or dragging it (the mouse router then ignores the click).</summary>
    public bool CapturesPointer => _dragging || (_panel != null && _panel.gameObject.activeSelf && Over(_panel));

    protected override void OnStart()
    {
        var c = Context.Config;
        _hotkey = c.Bind("UI", "HudHotkey", "F9", "Key that toggles the stream HUD.");
        _auto = c.Bind("UI", "AutoShowHud", true, "Show the HUD automatically while an audience interaction runs.");
        _x = c.Bind("Hud", "X", -1f, "Vote window position: left edge as a share of the screen width (-1 = top right).");
        _y = c.Bind("Hud", "Y", -1f, "Vote window position: top edge as a share of the screen height (-1 = top right).");
        _locked = c.Bind("Hud", "Locked", false, "Prevent moving the vote window with the mouse.");
        _opacity = c.Bind("Hud", "Opacity", 0.95f, "Vote window opacity (0.3 to 1).");
        _linger = c.Bind("Hud", "ResultSeconds", 6f, "Seconds the result stays visible after an interaction ended.");
        _hint = c.Bind("Hud", "ShowCommandHint", true, "Show the chat command (e.g. !vote A, B oder C) in the window.");
        _ui = ModUi.For(Context);
        Context.Provide(this);
        Track(Context.Bus.Subscribe<InteractionStatusChanged>(e => _current = e));
    }

    protected override void OnStop()
    {
        if (_panel != null) UnityEngine.Object.Destroy(_panel.gameObject);
        _panel = null;
        _rows.Clear();
    }

    /// <summary>Shows sample content for placing the window (drag it, any key or click outside ends).</summary>
    public void Preview(double seconds = 30)
    {
        _previewUntil = Context.Clock.Now + seconds;
    }

    public bool Previewing => Context.Clock.Now < _previewUntil;

    /// <summary>Back to the default corner.</summary>
    public void ResetPosition()
    {
        _x.Value = -1;
        _y.Value = -1;
    }

    public override void Tick()
    {
        if (Input.GetKeyDown(ModUi.Key(_hotkey.Value)) && !_ui.AnyOpen) _forced = !_forced;
        var now = Context.Clock.Now;
        var active = _current.Title.Length > 0;
        var show = _auto.Value || _forced;
        if (active)
        {
            if (_shown == null || _shown.Title != _current.Title) _timerTotal = (float)Math.Max(_current.SecondsLeft, 1);
            _shown = _current;
            _lastActive = now;
        }

        InteractionStatusChanged? content = null;
        var ended = false;
        if (Previewing)
        {
            content = Sample;
            _timerTotal = 30;
        }
        else if (active && show)
        {
            content = _current;
        }
        else if (!active && show && _shown != null && now - _lastActive < _linger.Value)
        {
            content = _shown;
            ended = true;
        }
        else if (_forced)
        {
            content = InteractionStatusChanged.Idle;
        }

        if (content == null)
        {
            if (_panel != null && _panel.gameObject.activeSelf) _panel.gameObject.SetActive(false);
            _dragging = false;
            return;
        }

        if (_panel == null) Build();
        if (!_panel!.gameObject.activeSelf) _panel.gameObject.SetActive(true);
        Render(content, ended);
        Place();
        Drag();
        if (Previewing && (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.Escape) || (Input.GetMouseButtonDown(0) && !Over(_panel)))) _previewUntil = 0;
    }

    // ---- building -----------------------------------------------------------------------------------

    private void Build()
    {
        var ui = _ui.Root;
        _panel = UiRoot.Child(ui.Rect, "VoteWindow");
        _panel.SetAsFirstSibling();
        _panel.anchorMin = _panel.anchorMax = new Vector2(0, 1);
        _panel.pivot = new Vector2(0, 1);
        _group = _panel.gameObject.AddComponent<CanvasGroup>();
        _group.blocksRaycasts = false;
        UiRoot.Stretch(ui.Image(_panel, "Solid", null, GameStyle.PanelFallback, false).rectTransform);
        var bg = ui.Image(_panel, "Background", "CategorySelectionBG", GameStyle.PanelFallback, false);
        UiRoot.Stretch(bg.rectTransform);
        var shade = ui.Image(_panel, "Shade", null, new Color(0, 0, 0, 0.35f), false);
        UiRoot.Stretch(shade.rectTransform);
        if (ui.Style.Sprite("GlowingBorder") != null) UiRoot.Stretch(ui.Image(_panel, "Border", "GlowingBorder", Color.clear, true).rectTransform, -10);

        _title = ui.Text(_panel, "Title", string.Empty, 30, GameStyle.Accent, true);
        UiRoot.Place(_title.rectTransform, 20, 10, Width - 40 - 130, 42);
        _status = ui.Text(_panel, "Status", string.Empty, 24, GameStyle.Muted, false, TextAlignmentOptions.MidlineRight);
        UiRoot.Place(_status.rectTransform, Width - 20 - 130, 10, 130, 42);
        var track = ui.Image(_panel, "Timer", null, new Color(1, 1, 1, 0.15f), false);
        UiRoot.Place(track.rectTransform, 20, 54, Width - 40, 6);
        _timerFill = ui.Image(track.rectTransform, "Fill", null, GameStyle.Accent, false).rectTransform;
        _timerFill.anchorMin = Vector2.zero;
        _timerFill.anchorMax = new Vector2(1, 1);
        _timerFill.offsetMin = _timerFill.offsetMax = Vector2.zero;
        for (var i = 0; i < MaxRows; i++) _rows.Add(new Row(ui, _panel, RowsTop + (i * RowHeight)));
        _footer = ui.Text(_panel, "Hint", string.Empty, 22, GameStyle.Muted);
        UiRoot.Place(_footer.rectTransform, 20, 0, Width - 40, 28);
    }

    private void Render(InteractionStatusChanged content, bool ended)
    {
        var rows = TallyLine.ParseAll(content.Lines).Take(MaxRows).ToList();
        var idle = ReferenceEquals(content, InteractionStatusChanged.Idle);
        var locked = ended || content.State is "Locked" or "Resolved" or "Closed";
        var left = content.SecondsLeft;
        UiRoot.SetText(_title!, idle ? "Keine Abstimmung" : content.Title);
        UiRoot.SetText(_status!, idle ? "bereit" : locked ? "fertig" : left > 0 ? Math.Ceiling(left) + " s" : StateText(content.State));
        if (_timerFill != null) _timerFill.anchorMax = new Vector2(locked || idle ? 0 : Mathf.Clamp01((float)(left / Math.Max(1, _timerTotal))), 1);
        var best = locked ? rows.Where(r => r.Share != null).OrderByDescending(r => r.Share!.Value).FirstOrDefault() : null;
        for (var i = 0; i < _rows.Count; i++) _rows[i].Show(i < rows.Count ? rows[i] : null, best != null && i < rows.Count && rows[i] == best);
        var height = RowsTop + (rows.Count * RowHeight) + 8;
        var hint = !idle && !locked && _hint.Value ? TallyLine.CommandHint(rows) : string.Empty;
        _footer!.gameObject.SetActive(hint.Length > 0);
        if (hint.Length > 0)
        {
            UiRoot.Place(_footer.rectTransform, 20, height, Width - 40, 28);
            UiRoot.SetText(_footer, "Im Chat: " + hint);
            height += 36;
        }

        _panel!.sizeDelta = new Vector2(Width, height);
        _group!.alpha = Mathf.Clamp(_opacity.Value, 0.3f, 1f);
    }

    private static string StateText(string state) => state switch
    {
        "Collecting" => "läuft",
        "Locked" => "fertig",
        "Paused" => "Pausiert",
        _ => state,
    };

    // ---- placement and dragging ---------------------------------------------------------------------

    private void Place()
    {
        var scale = Mathf.Clamp(_ui.HudScale.Value, 0.5f, 2f);
        _panel!.localScale = new Vector3(scale, scale, 1);
        var canvas = _ui.Root.Rect.rect;
        var size = _panel.sizeDelta * scale;
        float x, y;
        if (_x.Value < 0 || _y.Value < 0)
        {
            x = canvas.width - size.x - 30;
            y = 30;
        }
        else
        {
            x = _x.Value * canvas.width;
            y = _y.Value * canvas.height;
        }

        x = Mathf.Clamp(x, 0, Mathf.Max(0, canvas.width - size.x));
        y = Mathf.Clamp(y, 0, Mathf.Max(0, canvas.height - size.y));
        _panel.anchoredPosition = new Vector2(x, -y);
    }

    private void Drag()
    {
        if (_locked.Value && !Previewing)
        {
            _dragging = false;
            return;
        }

        if (!Cursor.visible && !Previewing) return;
        var local = MouseInCanvas();
        if (!_dragging && Input.GetMouseButtonDown(0) && Over(_panel!))
        {
            _dragging = true;
            _dragOffset = local - new Vector2(_panel!.anchoredPosition.x, -_panel.anchoredPosition.y);
        }

        if (!_dragging) return;
        var canvas = _ui.Root.Rect.rect;
        var pos = local - _dragOffset;
        _x.Value = Mathf.Clamp01(pos.x / canvas.width);
        _y.Value = Mathf.Clamp01(pos.y / canvas.height);
        if (!Input.GetMouseButton(0)) _dragging = false;
    }

    /// <summary>Mouse in canvas units, origin top-left, y down.</summary>
    private Vector2 MouseInCanvas()
    {
        var rect = _ui.Root.Rect;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, Input.mousePosition, null, out var p);
        var r = rect.rect;
        return new Vector2(p.x - r.xMin, r.yMax - p.y);
    }

    private static bool Over(RectTransform rect) =>
        RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, Input.mousePosition, null, out var p) && rect.rect.Contains(p);

    private sealed class Row
    {
        private readonly RectTransform _root;
        private readonly Image _keyBg;
        private readonly TextMeshProUGUI _key;
        private readonly TextMeshProUGUI _label;
        private readonly TextMeshProUGUI _value;
        private readonly RectTransform _bar;
        private readonly Image _fill;
        private readonly UiRoot _ui;
        private bool _plain;

        public Row(UiRoot ui, RectTransform parent, float y)
        {
            _ui = ui;
            _root = UiRoot.Place(UiRoot.Child(parent, "Row"), 20, y, Width - 40, RowHeight - 5);
            _keyBg = ui.Image(_root, "KeyBg", "TransparentBoxNormal", GameStyle.RowFallback, true);
            UiRoot.Place(_keyBg.rectTransform, 0, 0, 50, RowHeight - 5);
            _key = ui.Text(_root, "Key", string.Empty, 24, GameStyle.White, true, TextAlignmentOptions.Midline);
            UiRoot.Place(_key.rectTransform, 0, 0, 50, RowHeight - 5);
            var track = ui.Image(_root, "Track", null, new Color(1, 1, 1, 0.08f), false);
            _bar = UiRoot.Place(track.rectTransform, 58, 0, Width - 40 - 58, RowHeight - 5);
            _fill = ui.Image(_bar, "Fill", null, new Color(1f, 0.706f, 0.114f, 0.35f), false);
            var f = _fill.rectTransform;
            f.anchorMin = Vector2.zero;
            f.anchorMax = new Vector2(0, 1);
            f.offsetMin = f.offsetMax = Vector2.zero;
            _label = ui.Text(_root, "Label", string.Empty, 24, GameStyle.White);
            _label.enableAutoSizing = true;
            _label.fontSizeMin = 18;
            _label.fontSizeMax = 24;
            UiRoot.Place(_label.rectTransform, 68, 0, Width - 40 - 68 - 110, RowHeight - 5);
            _value = ui.Text(_root, "Value", string.Empty, 22, GameStyle.White, false, TextAlignmentOptions.MidlineRight);
            UiRoot.Place(_value.rectTransform, Width - 40 - 110, 0, 102, RowHeight - 5);
        }

        public void Show(TallyLine? line, bool winner)
        {
            _root.gameObject.SetActive(line != null);
            if (line == null) return;
            var plain = line.IsPlain;
            _keyBg.gameObject.SetActive(!plain);
            _key.gameObject.SetActive(!plain);
            _bar.gameObject.SetActive(!plain);
            if (plain != _plain)
            {
                _plain = plain;
                UiRoot.Place(_label.rectTransform, plain ? 0 : 68, 0, Width - 40 - (plain ? 0 : 68) - 110, RowHeight - 5);
                UiRoot.CenterCaps(_label);
            }

            UiRoot.SetText(_key, line.Key);
            UiRoot.SetText(_label, plain ? line.Label : (line.Label.Length > 0 ? line.Label : line.Key) + (line.Pending ? "  (wartet)" : string.Empty));
            UiRoot.SetText(_value, plain ? string.Empty : Mathf.RoundToInt((float)(line.Share ?? 0) * 100) + " %  (" + line.Count + ")");
            _fill.rectTransform.anchorMax = new Vector2(Mathf.Clamp01((float)(line.Share ?? 0)), 1);
            _fill.color = winner ? new Color(1f, 0.706f, 0.114f, 0.75f) : new Color(1f, 0.706f, 0.114f, 0.35f);
            _ui.SetFocusLook(_keyBg, winner);
            _label.color = winner ? GameStyle.Accent : GameStyle.White;
        }
    }
}
