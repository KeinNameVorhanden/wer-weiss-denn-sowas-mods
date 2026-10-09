using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace WWDS.Game.Wwds.Mods.Ui;

/// <summary>
/// The mod's own overlay canvas: survives scene loads (the game's 3_UI roots do not), sits above every
/// game view (pause menu 400, reassign 500) and below the loading screen (1000). Never touches DoozyUI's
/// UIManager canvases. Also owns the menu sound source (routed into the game's UI mixer group).
/// </summary>
public sealed class UiRoot
{
    public const int SortingOrder = 950;

    private readonly AudioSource _audio;

    public UiRoot(GameStyle style)
    {
        Style = style;
        Object = new GameObject("WWDS_ModUi");
        UnityEngine.Object.DontDestroyOnLoad(Object);
        Object.layer = 5;
        Canvas = Object.AddComponent<Canvas>();
        Canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        Canvas.sortingOrder = SortingOrder;
        var scaler = Object.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
        Object.AddComponent<GraphicRaycaster>();
        _audio = Object.AddComponent<AudioSource>();
        _audio.playOnAwake = false;
        _audio.ignoreListenerPause = true;
        Style.Refresh();
        SceneManager.sceneLoaded += (_, _) => Style.Refresh();
    }

    public GameObject Object { get; }

    public Canvas Canvas { get; }

    public GameStyle Style { get; }

    public RectTransform Rect => (RectTransform)Object.transform;

    public void PlayHighlight() => Play("sfx_mouseoverlay", 0.15f);

    public void PlayClick() => Play("sfx_Button_click", 0.4f);

    private void Play(string clip, float volume)
    {
        var c = Style.Clip(clip);
        if (c == null) return;
        if (_audio.outputAudioMixerGroup == null && Style.UiMixer != null) _audio.outputAudioMixerGroup = Style.UiMixer;
        _audio.PlayOneShot(c, volume);
    }

    // ---- element factory ----------------------------------------------------------------------------

    public static RectTransform Child(Transform parent, string name)
    {
        var go = new GameObject(name);
        go.layer = 5;
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        return rt;
    }

    /// <summary>Anchors a rect at the top-left of its parent with a fixed size.</summary>
    public static RectTransform Place(RectTransform rt, float x, float y, float width, float height)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
        rt.pivot = new Vector2(0, 1);
        rt.anchoredPosition = new Vector2(x, -y);
        rt.sizeDelta = new Vector2(width, height);
        return rt;
    }

    public static RectTransform Stretch(RectTransform rt, float inset = 0)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(inset, inset);
        rt.offsetMax = new Vector2(-inset, -inset);
        return rt;
    }

    public Image Image(Transform parent, string name, string? sprite, Color fallback, bool sliced)
    {
        var rt = Child(parent, name);
        var img = rt.gameObject.AddComponent<Image>();
        var s = sprite == null ? null : Style.Sprite(sprite);
        if (s != null)
        {
            img.sprite = s;
            img.type = sliced ? UnityEngine.UI.Image.Type.Sliced : UnityEngine.UI.Image.Type.Simple;
            img.color = Color.white;
        }
        else
        {
            img.color = fallback;
        }

        img.raycastTarget = false;
        return img;
    }

    public TextMeshProUGUI Text(Transform parent, string name, string text, float size, Color color, bool title = false, TextAlignmentOptions align = TextAlignmentOptions.MidlineLeft)
    {
        var rt = Child(parent, name);
        rt.gameObject.SetActive(false);
        var tmp = rt.gameObject.AddComponent<TextMeshProUGUI>();
        var font = title ? Style.TitleFont : Style.BodyFont;
        if (font != null) tmp.font = font;
        tmp.text = text;
        // Style after Awake: TMP's Awake converts an already-set alignment from the legacy enum values.
        rt.gameObject.SetActive(true);
        tmp.fontSize = size;
        tmp.color = color;
        tmp.alignment = align;
        tmp.enableWordWrapping = false;
        tmp.overflowMode = TextOverflowModes.Ellipsis;
        tmp.raycastTarget = false;
        CenterCaps(tmp);
        return tmp;
    }

    /// <summary>
    /// Sets text and re-centres it. TMP's midline alignment uses the font's line metrics; with the game's
    /// fonts (tall ascender for umlauts) that puts text visibly above the middle of a box.
    /// </summary>
    public static void SetText(TextMeshProUGUI t, string text)
    {
        if (t.text == text) return;
        t.text = text;
        CenterCaps(t);
    }

    /// <summary>Re-centres every text below <paramref name="root"/> (call after placing rects).</summary>
    public static void CenterAll(Transform root)
    {
        foreach (var t in root.GetComponentsInChildren<TextMeshProUGUI>(false)) CenterCaps(t);
    }

    /// <summary>Shifts a midline-aligned single-line text so the middle of its capital letters sits on the rect's middle.</summary>
    public static void CenterCaps(TextMeshProUGUI t)
    {
        var a = t.alignment;
        if (a != TextAlignmentOptions.MidlineLeft && a != TextAlignmentOptions.Midline && a != TextAlignmentOptions.MidlineRight) return;
        t.margin = Vector4.zero;
        if (string.IsNullOrEmpty(t.text) || !t.gameObject.activeInHierarchy) return;
        t.ForceMeshUpdate();
        var info = t.textInfo;
        if (info == null || info.characterCount == 0) return;
        var first = info.characterInfo[0];
        var face = t.font != null ? t.font.fontInfo : null;
        float capHeight;
        if (face != null && face.CapHeight > 0 && face.PointSize > 0) capHeight = face.CapHeight * (first.pointSize / face.PointSize) * face.Scale;
        else capHeight = (first.ascender - first.baseLine) * 0.72f;
        var capMiddle = first.baseLine + (capHeight / 2f);
        var delta = capMiddle - t.rectTransform.rect.center.y;
        if (Mathf.Abs(delta) < 0.5f) return;
        t.margin = new Vector4(0, delta, 0, -delta);
        t.ForceMeshUpdate();
    }

    /// <summary>Swaps a row background between the game's normal and highlight box sprites.</summary>
    public void SetFocusLook(Image background, bool focused)
    {
        var s = Style.Sprite(focused ? "TransparentBoxHighlight" : "TransparentBoxNormal");
        if (s != null)
        {
            background.sprite = s;
            background.type = UnityEngine.UI.Image.Type.Sliced;
            background.color = Color.white;
        }
        else
        {
            background.sprite = null;
            background.color = focused ? GameStyle.RowFocusFallback : GameStyle.RowFallback;
        }
    }
}
