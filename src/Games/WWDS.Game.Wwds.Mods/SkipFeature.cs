using System;
using System.Collections.Generic;
using System.Linq;
using DoozyUI;
using HarmonyLib;
using InControl;
using UnityEngine;
using WWDS.ModCore.Config;
using WWDS.ModCore.Features;
using WWDS.ModCore.Model;

namespace WWDS.Game.Wwds.Mods;

/// <summary>
/// Faster shows. A tap on the skip key (Tab, or the right stick click on a pad) ends the running answer
/// video, or the line being spoken (the show moves on as if it was over). Holding it fast-forwards
/// everything that does not wait for a player (talk, animations, reveals) at the configured speed,
/// silently. Never while a player answers, bets or estimates (the final's clock would run faster) or
/// while the game is paused. Answer videos can also be skipped automatically.
/// </summary>
public sealed class SkipFeature : FeatureBase
{
    public const string FeatureId = "Skip";
    private const float HoldSeconds = 0.35f;

    private static readonly GamePhase[] PlayerPhases = { GamePhase.WaitingForAnswer, GamePhase.MasterBet, GamePhase.BonusEstimate };
    private IConfigValue<bool> _enabled = null!;
    private IConfigValue<string> _key = null!;
    private IConfigValue<float> _speed = null!;
    private IConfigValue<bool> _autoVideos = null!;
    private ModUi _ui = null!;
    private float _downAt = -1f;
    private bool _fast;
    private float _skipUntil;
    private float _scaleBefore = 1f;
    private bool _videoWasPlaying;

    public override string Id => FeatureId;

    public override string DisplayName => "Skip and fast-forward";

    public override string Description => "Tap Tab (or the right stick) to skip a video or line, hold to fast-forward; optional automatic video skip.";

    protected override string LogCategoryName => "Mods";

    public IConfigValue<string> Key => _key;

    public IConfigValue<float> Speed => _speed;

    public IConfigValue<bool> AutoSkipVideos => _autoVideos;

    protected override void OnStart()
    {
        var c = Context.Config;
        _enabled = c.Bind("Skip", "Enabled", true, "Skip key in a show: tap = skip the answer video or the current line, hold = fast-forward.");
        _key = c.Bind("Skip", "Key", "Tab", "Keyboard key (UnityEngine.KeyCode name); on a pad the right stick click.");
        _speed = c.Bind("Skip", "Speed", 4f, "Fast-forward speed while the key is held (2 to 10).");
        _autoVideos = c.Bind("Skip", "AutoSkipVideos", false, "Skip every answer video automatically.");
        _ui = ModUi.For(Context);
        Context.Provide(this);
        Track(_ui.OnState(_ => EndFast()));
    }

    protected override void OnStop() => EndFast();

    public override void Tick()
    {
        if (!_enabled.Value || !_ui.InShow || _ui.AnyOpen || _ui.Blocker.Active)
        {
            EndFast();
            _downAt = -1f;
            return;
        }

        var video = VideoPlaying();
        if (_autoVideos.Value && video && !_videoWasPlaying) SkipVideo();
        _videoWasPlaying = video;

        var held = KeyHeld();
        var now = Time.unscaledTime;
        if (held && _downAt < 0f) _downAt = now;
        if (held && now - _downAt >= HoldSeconds && Allowed()) StartFast();
        if (!held && _downAt >= 0f)
        {
            var tap = now - _downAt < HoldSeconds;
            _downAt = -1f;
            if (_fast) EndFast();
            else if (tap) SkipOne();
        }

        if (_fast)
        {
            if (!Allowed()) EndFast();
            else StopVoices();
        }
        else if (_skipUntil > 0f)
        {
            if (now >= _skipUntil || !Allowed()) EndFast();
        }
    }

    /// <summary>A tap: the video if one runs, else the line being spoken (time runs fast until it would have ended).</summary>
    private void SkipOne()
    {
        if (!Allowed()) return;
        if (VideoPlaying())
        {
            SkipVideo();
            return;
        }

        var remaining = 0f;
        foreach (var source in VoiceSources())
        {
            if (source == null || !source.isPlaying || source.clip == null) continue;
            remaining = Mathf.Max(remaining, source.clip.length - source.time);
            source.Stop();
        }

        if (remaining <= 0.1f) return;
        // The game waits the line's length in scaled time: run that wait at 10x.
        SetScale(10f);
        _skipUntil = Time.unscaledTime + (remaining / 10f);
    }

    private void StartFast()
    {
        if (_fast) return;
        _fast = true;
        SetScale(Mathf.Clamp(_speed.Value, 2f, 10f));
        StopVoices();
        if (VideoPlaying()) SkipVideo();
        _ui.Toast(">> Schneller Vorlauf", 1.5f, "skip");
    }

    private void SetScale(float scale)
    {
        if (!_fast && _skipUntil <= 0f) _scaleBefore = Time.timeScale;
        Time.timeScale = scale;
    }

    private void EndFast()
    {
        if (!_fast && _skipUntil <= 0f) return;
        _fast = false;
        _skipUntil = 0f;
        // Only undo our own speed: a pause set in between keeps its 0.
        if (Time.timeScale > 1.01f) Time.timeScale = _scaleBefore > 0f ? _scaleBefore : 1f;
    }

    private bool Allowed()
    {
        if (UIManager.Instance != null && UIManager.Instance.gamePaused) return false;
        if (Time.timeScale <= 0f) return false;
        var phase = Context.Adapter?.GetSnapshot().Phase ?? GamePhase.Unknown;
        return Array.IndexOf(PlayerPhases, phase) < 0;
    }

    private bool KeyHeld()
    {
        var keyHeld = false;
        try
        {
            keyHeld = Input.GetKey((KeyCode)Enum.Parse(typeof(KeyCode), _key.Value, true));
        }
        catch (ArgumentException)
        {
        }

        return keyHeld || (InputManager.Devices != null && InputManager.Devices.Any(d => d.GetControl(InputControlType.RightStickButton).IsPressed));
    }

    private static IEnumerable<AudioSource> VoiceSources()
    {
        var sound = GameSettingsFeature.ExistingSound;
        var sources = sound == null ? null : AccessTools.Field(typeof(SSoundManager), "voiceAudioSources").GetValue(sound) as Dictionary<EVoiceType, AudioSource>;
        return sources?.Values ?? Enumerable.Empty<AudioSource>();
    }

    private static void StopVoices()
    {
        foreach (var source in VoiceSources())
        {
            if (source != null && source.isPlaying) source.Stop();
        }
    }

    private static bool VideoPlaying() => SVideoController.Instance != null && (bool)AccessTools.Field(typeof(SVideoController), "isPlaying").GetValue(SVideoController.Instance);

    internal bool VideoRunning => VideoPlaying();

    /// <summary>Self-test: a tap or holding the key.</summary>
    internal void TestTap() => SkipOne();

    internal void TestHold(bool on)
    {
        if (on) StartFast();
        else EndFast();
    }

    private void SkipVideo()
    {
        SVideoController.Instance.Stop();
        Log.Info("Answer video skipped");
    }
}
