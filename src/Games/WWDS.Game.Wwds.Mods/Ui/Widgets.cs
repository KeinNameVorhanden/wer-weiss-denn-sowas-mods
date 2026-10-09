using System;
using System.Collections.Generic;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace WWDS.Game.Wwds.Mods.Ui;

/// <summary>
/// One row of a mod menu page. Rows are plain uGUI objects; focus, values and input are driven by
/// <see cref="MenuWindow"/> so keyboard, controller and mouse behave the same.
/// </summary>
public abstract class Widget
{
    public const float RowHeight = 62;
    public const float LabelSize = 32;

    protected Widget(string label) => Label = label;

    public string Label { get; protected set; }

    /// <summary>Help text shown in the footer while the row has focus.</summary>
    public string? Hint { get; set; }

    /// <summary>Row hidden while this returns false (re-evaluated every frame).</summary>
    public Func<bool>? VisibleWhen { get; set; }

    public virtual bool Focusable => true;

    public virtual float Height => RowHeight;

    public RectTransform? Root { get; private set; }

    protected UiRoot Ui { get; private set; } = null!;

    protected Image? Background { get; private set; }

    protected TextMeshProUGUI? LabelText { get; private set; }

    public bool IsVisible => VisibleWhen == null || VisibleWhen();

    internal void Build(UiRoot ui, RectTransform parent, float width)
    {
        Ui = ui;
        Root = UiRoot.Child(parent, GetType().Name + ":" + Label);
        Root.sizeDelta = new Vector2(width, Height);
        if (Focusable)
        {
            Background = ui.Image(Root, "Bg", "TransparentBoxNormal", GameStyle.RowFallback, true);
            UiRoot.Stretch(Background.rectTransform);
        }

        BuildContent(width);
        Refresh();
    }

    protected virtual void BuildContent(float width)
    {
        LabelText = Ui.Text(Root!, "Label", Label, LabelSize, GameStyle.White);
        UiRoot.Place(LabelText.rectTransform, 24, 0, width * 0.55f - 24, Height);
    }

    internal void SetFocus(bool focused)
    {
        if (Background != null) Ui.SetFocusLook(Background, focused);
        if (LabelText != null) LabelText.color = focused ? GameStyle.Accent : GameStyle.White;
    }

    /// <summary>Re-reads the model; called every frame for visible rows.</summary>
    public virtual void Refresh()
    {
    }

    /// <summary>Enter / A / click.</summary>
    public virtual void Submit()
    {
    }

    /// <summary>Left (-1) / right (+1).</summary>
    public virtual void Adjust(int direction)
    {
    }

    /// <summary>Mouse click at a normalized x (0..1) inside the row. Default: submit.</summary>
    public virtual void Click(float x) => Submit();

    /// <summary>Mouse drag while the button is held (sliders).</summary>
    public virtual void Drag(float x)
    {
    }

    /// <summary>True while the row captures the keyboard (text editing).</summary>
    public virtual bool Capturing => false;

    /// <summary>Called every frame while <see cref="Capturing"/>.</summary>
    public virtual void CaptureInput()
    {
    }

    protected TextMeshProUGUI ValueText(float width, TextAlignmentOptions align = TextAlignmentOptions.MidlineRight)
    {
        var t = Ui.Text(Root!, "Value", string.Empty, LabelSize, GameStyle.White, false, align);
        UiRoot.Place(t.rectTransform, width * 0.55f, 0, width * 0.45f - 28, Height);
        return t;
    }

    protected static void Set(TextMeshProUGUI? t, string text)
    {
        if (t != null) UiRoot.SetText(t, text);
    }
}

/// <summary>Section title inside a page (not focusable).</summary>
public sealed class HeaderWidget : Widget
{
    public HeaderWidget(string label)
        : base(label)
    {
    }

    public override bool Focusable => false;

    public override float Height => 70;

    protected override void BuildContent(float width)
    {
        var t = Ui.Text(Root!, "Header", Label, 38, GameStyle.Accent, true);
        UiRoot.Place(t.rectTransform, 8, 14, width - 16, 56);
    }
}

/// <summary>Read-only text, optionally live and multi-line.</summary>
public sealed class InfoWidget : Widget
{
    private readonly Func<string> _text;
    private readonly int _lines;
    private TextMeshProUGUI? _t;

    public InfoWidget(Func<string> text, int lines = 1)
        : base(string.Empty)
    {
        _text = text;
        _lines = Math.Max(1, lines);
    }

    public InfoWidget(string text, int lines = 1)
        : this(() => text, lines)
    {
    }

    public override bool Focusable => false;

    public override float Height => 12 + (_lines * 38);

    protected override void BuildContent(float width)
    {
        _t = Ui.Text(Root!, "Info", string.Empty, 28, GameStyle.Muted, false, TextAlignmentOptions.TopLeft);
        _t.enableWordWrapping = true;
        _t.overflowMode = TextOverflowModes.Truncate;
        UiRoot.Place(_t.rectTransform, 24, 6, width - 48, Height - 6);
    }

    public override void Refresh() => Set(_t, _text());
}

public sealed class ButtonWidget : Widget
{
    private readonly Action _action;
    private readonly Func<string>? _value;
    private TextMeshProUGUI? _v;

    public ButtonWidget(string label, Action action, Func<string>? value = null)
        : base(label)
    {
        _action = action;
        _value = value;
    }

    public Func<bool>? EnabledWhen { get; set; }

    protected override void BuildContent(float width)
    {
        base.BuildContent(width);
        _v = ValueText(width);
    }

    public override void Refresh()
    {
        var enabled = EnabledWhen == null || EnabledWhen();
        Set(_v, _value != null ? _value() : enabled ? ">" : string.Empty);
        if (_v != null) _v.color = enabled ? GameStyle.White : GameStyle.Muted;
    }

    public override void Submit()
    {
        if (EnabledWhen != null && !EnabledWhen()) return;
        _action();
    }
}

public sealed class ToggleWidget : Widget
{
    private readonly Func<bool> _get;
    private readonly Action<bool> _set;
    private TextMeshProUGUI? _v;

    public ToggleWidget(string label, Func<bool> get, Action<bool> set)
        : base(label)
    {
        _get = get;
        _set = set;
    }

    protected override void BuildContent(float width)
    {
        base.BuildContent(width);
        _v = ValueText(width);
    }

    public override void Refresh()
    {
        var on = _get();
        Set(_v, on ? "An" : "Aus");
        if (_v != null) _v.color = on ? GameStyle.Accent : GameStyle.Muted;
    }

    public override void Submit() => _set(!_get());

    public override void Adjust(int direction) => _set(direction > 0);
}

/// <summary>Pick one of several options with left/right ("&lt; Option &gt;").</summary>
public sealed class CycleWidget : Widget
{
    private readonly Func<IList<string>> _options;
    private readonly Func<int> _get;
    private readonly Action<int> _set;
    private TextMeshProUGUI? _v;

    public CycleWidget(string label, Func<IList<string>> options, Func<int> get, Action<int> set)
        : base(label)
    {
        _options = options;
        _get = get;
        _set = set;
    }

    public CycleWidget(string label, IList<string> options, Func<int> get, Action<int> set)
        : this(label, () => options, get, set)
    {
    }

    /// <summary>Cycle over an enum's names.</summary>
    public static CycleWidget ForEnum<T>(string label, Func<T> get, Action<T> set)
    {
        var names = Enum.GetNames(typeof(T));
        return new CycleWidget(label, names, () => Array.IndexOf(names, get()!.ToString()), i => set((T)Enum.Parse(typeof(T), names[i])));
    }

    protected override void BuildContent(float width)
    {
        base.BuildContent(width);
        _v = ValueText(width);
    }

    public override void Refresh()
    {
        var options = _options();
        var i = _get();
        Set(_v, options.Count == 0 ? "-" : "<  " + (i >= 0 && i < options.Count ? options[i] : "?") + "  >");
    }

    public override void Submit() => Adjust(1);

    public override void Adjust(int direction)
    {
        var count = _options().Count;
        if (count == 0) return;
        var i = _get();
        _set(((i < 0 ? 0 : i + direction) % count + count) % count);
    }

    public override void Click(float x) => Adjust(x < 0.78f ? -1 : 1);
}

/// <summary>Numeric value with a bar; left/right steps, mouse drags.</summary>
public sealed class SliderWidget : Widget
{
    private readonly float _min;
    private readonly float _max;
    private readonly float _step;
    private readonly Func<float> _get;
    private readonly Action<float> _set;
    private readonly Func<float, string> _format;
    private RectTransform? _fill;
    private TextMeshProUGUI? _v;
    private float _trackStart;
    private float _trackWidth;

    public SliderWidget(string label, float min, float max, float step, Func<float> get, Action<float> set, Func<float, string>? format = null)
        : base(label)
    {
        _min = min;
        _max = max;
        _step = step;
        _get = get;
        _set = set;
        _format = format ?? (v => v.ToString("0.##", CultureInfo.InvariantCulture));
    }

    protected override void BuildContent(float width)
    {
        base.BuildContent(width);
        _trackStart = width * 0.55f;
        _trackWidth = width * 0.3f;
        var track = Ui.Image(Root!, "Track", null, new Color(1, 1, 1, 0.18f), false);
        UiRoot.Place(track.rectTransform, _trackStart, (Height / 2) - 6, _trackWidth, 12);
        var fill = Ui.Image(track.rectTransform, "Fill", null, GameStyle.Accent, false);
        _fill = fill.rectTransform;
        _fill.anchorMin = Vector2.zero;
        _fill.anchorMax = new Vector2(0, 1);
        _fill.pivot = new Vector2(0, 0.5f);
        _fill.offsetMin = Vector2.zero;
        _fill.offsetMax = Vector2.zero;
        _v = Ui.Text(Root!, "Value", string.Empty, LabelSize, GameStyle.White, false, TextAlignmentOptions.MidlineRight);
        UiRoot.Place(_v.rectTransform, _trackStart + _trackWidth, 0, width * 0.15f - 28, Height);
    }

    public override void Refresh()
    {
        var v = _get();
        if (_fill != null) _fill.anchorMax = new Vector2(Mathf.Clamp01((v - _min) / (_max - _min)), 1);
        Set(_v, _format(v));
    }

    public override void Adjust(int direction) => Apply(_get() + (direction * _step));

    public override void Submit() => Adjust(1);

    public override void Click(float x) => Drag(x);

    public override void Drag(float x)
    {
        var width = Root!.rect.width;
        var t = Mathf.Clamp01(((x * width) - _trackStart) / _trackWidth);
        Apply(_min + (t * (_max - _min)));
    }

    private void Apply(float value)
    {
        var snapped = _min + (Mathf.Round((value - _min) / _step) * _step);
        _set(Mathf.Clamp(snapped, _min, _max));
    }
}

/// <summary>Text entry: Enter or click starts editing, Enter commits, Escape cancels, Ctrl+V pastes.</summary>
public sealed class TextFieldWidget : Widget
{
    private readonly Func<string> _get;
    private readonly Action<string> _set;
    private readonly int _maxLength;
    private TextMeshProUGUI? _v;
    private string? _edit;

    public TextFieldWidget(string label, Func<string> get, Action<string> set, int maxLength = 64)
        : base(label)
    {
        _get = get;
        _set = set;
        _maxLength = maxLength;
    }

    /// <summary>Show only the first and last characters while not editing (secrets on stream).</summary>
    public bool Masked { get; set; }

    /// <summary>Optional left/right stepping (numbers); shown as "&lt; value &gt;".</summary>
    public Action<int>? Step { get; set; }

    public override void Adjust(int direction) => Step?.Invoke(direction);

    public override void Click(float x)
    {
        if (Step != null && x >= 0.55f && x < 0.7f) Step(-1);
        else if (Step != null && x >= 0.9f) Step(1);
        else Submit();
    }

    public override bool Capturing => _edit != null;

    protected override void BuildContent(float width)
    {
        base.BuildContent(width);
        _v = ValueText(width);
    }

    public override void Refresh()
    {
        if (_edit != null)
        {
            Set(_v, _edit + ((int)(Time.unscaledTime * 2) % 2 == 0 ? "|" : " "));
            if (_v != null) _v.color = GameStyle.Accent;
            return;
        }

        var value = _get();
        var shown = value.Length == 0 ? "(leer)" : Masked && value.Length > 6 ? value.Substring(0, 3) + "..." + value.Substring(value.Length - 3) : value;
        Set(_v, Step != null ? "<  " + shown + "  >" : shown);
        if (_v != null) _v.color = value.Length == 0 ? GameStyle.Muted : GameStyle.White;
    }

    public override void Submit() => _edit = _get();

    public override void CaptureInput()
    {
        if (_edit == null) return;
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            _edit = null;
            return;
        }

        if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
        {
            var value = _edit.Trim();
            _edit = null;
            _set(value);
            return;
        }

        var ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        if (ctrl && Input.GetKeyDown(KeyCode.V))
        {
            _edit += (GUIUtility.systemCopyBuffer ?? string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty);
        }
        else
        {
            foreach (var c in Input.inputString)
            {
                if (c == '\b')
                {
                    if (_edit.Length > 0) _edit = _edit.Substring(0, _edit.Length - 1);
                }
                else if (c != '\n' && c != '\r' && !char.IsControl(c))
                {
                    _edit += c;
                }
            }
        }

        if (_edit.Length > _maxLength) _edit = _edit.Substring(0, _maxLength);
    }
}
