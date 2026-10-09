using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace WWDS.Game.Wwds.Mods.Ui;

/// <summary>
/// Short game-styled notifications at the top centre ("Gespeichert", "Twitch verbunden", login code).
/// At most three at a time, newest at the bottom, fading out on unscaled time (also while paused).
/// </summary>
public sealed class ToastLayer
{
    private const float Width = 760;
    private const float Height = 64;
    private const int Max = 3;

    private readonly UiRoot _ui;
    private readonly List<Toast> _toasts = new();
    private RectTransform? _root;

    public ToastLayer(UiRoot ui) => _ui = ui;

    public float Scale { get; set; } = 1;

    /// <summary>Shows <paramref name="text"/>; a toast with the same key replaces the previous one.</summary>
    public void Show(string text, float seconds = 4, string? key = null)
    {
        _root ??= Build();
        var existing = key == null ? null : _toasts.Find(t => t.Key == key);
        if (existing != null)
        {
            UiRoot.SetText(existing.Text, text);
            Fit(existing.Box, existing.Text);
            existing.Until = Time.unscaledTime + seconds;
            return;
        }

        if (_toasts.Count >= Max) Remove(_toasts[0]);
        var box = UiRoot.Child(_root, "Toast");
        box.anchorMin = box.anchorMax = new Vector2(0.5f, 1);
        box.pivot = new Vector2(0.5f, 1);
        box.sizeDelta = new Vector2(Width, Height);
        var group = box.gameObject.AddComponent<CanvasGroup>();
        group.blocksRaycasts = false;
        UiRoot.Stretch(_ui.Image(box, "Solid", null, GameStyle.PanelFallback, false).rectTransform);
        var bg = _ui.Image(box, "Background", "CategorySelectionBG", GameStyle.PanelFallback, false);
        UiRoot.Stretch(bg.rectTransform);
        var shade = _ui.Image(box, "Shade", null, new Color(0, 0, 0, 0.35f), false);
        UiRoot.Stretch(shade.rectTransform);
        if (_ui.Style.Sprite("GlowingBorder") != null) UiRoot.Stretch(_ui.Image(box, "Border", "GlowingBorder", Color.clear, true).rectTransform, -8);
        var label = _ui.Text(box, "Text", text, 28, GameStyle.White, false, TextAlignmentOptions.Midline);
        Fit(box, label);
        _toasts.Add(new Toast(key, box, group, label, Time.unscaledTime + seconds));
    }

    /// <summary>Removes the toast with <paramref name="key"/>, if shown.</summary>
    public void Dismiss(string key)
    {
        var t = _toasts.Find(x => x.Key == key);
        if (t != null) Remove(t);
    }

    public void Tick()
    {
        if (_root == null) return;
        _root.SetAsLastSibling();
        _root.localScale = new Vector3(Scale, Scale, 1);
        var now = Time.unscaledTime;
        var y = 24f;
        foreach (var t in _toasts.ToArray())
        {
            var left = t.Until - now;
            if (left <= 0)
            {
                Remove(t);
                continue;
            }

            t.Group.alpha = Mathf.Clamp01(left / 0.4f);
            t.Box.anchoredPosition = new Vector2(0, -y);
            y += Height + 10;
        }
    }

    /// <summary>Sizes a toast to its text (between 360 and 900 units wide).</summary>
    private static void Fit(RectTransform box, TextMeshProUGUI label)
    {
        var width = Mathf.Clamp(label.preferredWidth + 80, 360, 900);
        box.sizeDelta = new Vector2(width, Height);
        UiRoot.Place(label.rectTransform, 20, 0, width - 40, Height);
        UiRoot.CenterCaps(label);
    }

    private RectTransform Build()
    {
        var root = UiRoot.Child(_ui.Rect, "Toasts");
        root.anchorMin = root.anchorMax = new Vector2(0.5f, 1);
        root.pivot = new Vector2(0.5f, 1);
        root.sizeDelta = new Vector2(Width, 0);
        return root;
    }

    private void Remove(Toast t)
    {
        _toasts.Remove(t);
        if (t.Box != null) Object.Destroy(t.Box.gameObject);
    }

    private sealed class Toast
    {
        public Toast(string? key, RectTransform box, CanvasGroup group, TextMeshProUGUI text, float until)
        {
            Key = key;
            Box = box;
            Group = group;
            Text = text;
            Until = until;
        }

        public string? Key { get; }

        public RectTransform Box { get; }

        public CanvasGroup Group { get; }

        public TextMeshProUGUI Text { get; }

        public float Until { get; set; }
    }
}
