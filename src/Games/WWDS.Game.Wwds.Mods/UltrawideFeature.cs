using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using WWDS.ModCore.Config;
using WWDS.ModCore.Features;

namespace WWDS.Game.Wwds.Mods;

/// <summary>How the game is shown on screens wider than 16:9.</summary>
public enum UltrawideMode
{
    /// <summary>The game as it is (its menus scale by width and get cut off at the top and bottom).</summary>
    Off,

    /// <summary>The menus and prompts scale by height so they fit; the studio uses the whole width.</summary>
    Auto,

    /// <summary>Like Auto, plus the picture is kept at 16:9 with black bars at the sides.</summary>
    Pillarbox,
}

/// <summary>
/// Ultrawide screens (21:9, 32:9): the game's interface scales with the screen width, so on wide
/// screens it grows too tall and the team prompts at the top and bottom are cut off. Above 16:9 every
/// game canvas scales by height instead; optionally the 3D picture is pillarboxed to 16:9.
/// </summary>
public sealed class UltrawideFeature : FeatureBase
{
    public const string FeatureId = "Ultrawide";
    private const float Wide = 16f / 9f;

    private readonly Dictionary<CanvasScaler, float> _original = new();
    private readonly Dictionary<Camera, Rect> _rects = new();
    private IConfigValue<UltrawideMode> _mode = null!;
    private Camera? _bars;
    private float _nextScan;

    public override string Id => FeatureId;

    public override string DisplayName => "Ultrawide";

    public override string Description => "On screens wider than 16:9 the menus fit the height; optional 16:9 picture with side bars.";

    protected override string LogCategoryName => "Mods";

    public IConfigValue<UltrawideMode> Mode => _mode;

    /// <summary>Game canvases changed to height matching (self-test).</summary>
    public int FittedCanvases { get; private set; }

    protected override void OnStart()
    {
        _mode = Context.Config.Bind("Display", "Ultrawide", UltrawideMode.Auto, "Screens wider than 16:9: Off | Auto (menus fit the height) | Pillarbox (also a 16:9 picture with black side bars).");
        Context.Provide(this);
    }

    protected override void OnStop() => Restore();

    public override void Tick()
    {
        if (Time.unscaledTime < _nextScan) return;
        _nextScan = Time.unscaledTime + 0.5f;
        var wide = Screen.height > 0 && (float)Screen.width / Screen.height > Wide + 0.01f;
        if (!wide || _mode.Value == UltrawideMode.Off)
        {
            Restore();
            return;
        }

        FitCanvases();
        if (_mode.Value == UltrawideMode.Pillarbox) Pillarbox();
        else RestoreCameras();
    }

    private void FitCanvases()
    {
        var count = 0;
        foreach (var scaler in Object.FindObjectsOfType<CanvasScaler>())
        {
            if (scaler.uiScaleMode != CanvasScaler.ScaleMode.ScaleWithScreenSize || scaler.gameObject.name == "WWDS_ModUi") continue;
            if (!_original.ContainsKey(scaler)) _original[scaler] = scaler.matchWidthOrHeight;
            if (scaler.screenMatchMode == CanvasScaler.ScreenMatchMode.MatchWidthOrHeight && scaler.matchWidthOrHeight < 1f) scaler.matchWidthOrHeight = 1f;
            count++;
        }

        FittedCanvases = count;
    }

    private void Pillarbox()
    {
        var aspect = (float)Screen.width / Screen.height;
        var width = Wide / aspect;
        var rect = new Rect((1f - width) / 2f, 0f, width, 1f);
        if (_bars == null)
        {
            var go = new GameObject("WWDS_PillarboxBars");
            Object.DontDestroyOnLoad(go);
            _bars = go.AddComponent<Camera>();
            _bars.depth = -100;
            _bars.cullingMask = 0;
            _bars.clearFlags = CameraClearFlags.SolidColor;
            _bars.backgroundColor = Color.black;
        }

        foreach (var cam in Camera.allCameras)
        {
            if (cam == _bars || cam.targetTexture != null) continue;
            if (!_rects.ContainsKey(cam)) _rects[cam] = cam.rect;
            if (cam.rect != rect) cam.rect = rect;
        }
    }

    private void RestoreCameras()
    {
        foreach (var pair in _rects)
        {
            if (pair.Key != null) pair.Key.rect = pair.Value;
        }

        _rects.Clear();
        if (_bars != null) Object.Destroy(_bars.gameObject);
        _bars = null;
    }

    private void Restore()
    {
        foreach (var pair in _original)
        {
            if (pair.Key != null) pair.Key.matchWidthOrHeight = pair.Value;
        }

        _original.Clear();
        FittedCanvases = 0;
        RestoreCameras();
    }
}
