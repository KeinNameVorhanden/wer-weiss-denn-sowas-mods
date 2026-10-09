using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.SceneManagement;
using WWDS.ModCore.Config;
using WWDS.ModCore.Features;
using WWDS.Runtime.Patching;

namespace WWDS.Game.Wwds.Mods;

public enum WindowMode
{
    Fullscreen,
    Borderless,
    Windowed,
}

/// <summary>
/// General game settings beyond the game's own Tech room: volumes
/// (also applied at start, which the game forgets, and a real mute at 0), resolution with window mode,
/// frame rate cap, vsync and quality level. Display settings are only managed once changed here, so a
/// player who never touches them keeps the game's behaviour.
/// </summary>
public sealed class GameSettingsFeature : FeatureBase
{
    public const string FeatureId = "GameSettings";
    public static readonly int[] FrameRates = { 30, 60, 75, 120, 144, 165, 240, -1 };

    private static GameSettingsFeature? _current;
    private IConfigValue<bool> _manageDisplay = null!;
    private IConfigValue<string> _resolution = null!;
    private IConfigValue<WindowMode> _windowMode = null!;
    private IConfigValue<int> _frameRate = null!;
    private IConfigValue<bool> _vsync = null!;
    private IConfigValue<int> _quality = null!;
    private IConfigValue<bool> _applyVolumes = null!;
    private IConfigValue<bool> _muteMusic = null!;
    private IConfigValue<bool> _muteSfx = null!;
    private IConfigValue<bool> _muteVoice = null!;
    private IConfigValue<float> _master = null!;
    private IConfigValue<bool> _muteInBackground = null!;
    private GameObject? _focus;
    private bool _focused = true;
    private bool _volumesApplied;
    private ModUi _ui = null!;

    public override string Id => FeatureId;

    public override string DisplayName => "Game settings";

    public override string Description => "Volumes (applied at start, real mute), resolution, window mode, frame rate, vsync, quality.";

    protected override string LogCategoryName => "Mods";

    public bool ManageDisplay => _manageDisplay.Value;

    protected override void OnStart()
    {
        var c = Context.Config;
        const string s = "Game";
        _manageDisplay = c.Bind(s, "ManageDisplay", false, "Apply the display settings below at start (set automatically when changed in the mod menu).");
        _resolution = c.Bind(s, "Resolution", "", "Width x height, e.g. 1920x1080 (empty = keep).");
        _windowMode = c.Bind(s, "WindowMode", WindowMode.Fullscreen, "Fullscreen | Borderless | Windowed (the game itself only knows fullscreen).");
        _frameRate = c.Bind(s, "FrameRateLimit", 60, "Frame rate cap (the game uses 60); -1 = unlimited.");
        _vsync = c.Bind(s, "VSync", false, "Vertical sync (overrides the frame rate cap).");
        _quality = c.Bind(s, "QualityLevel", -1, "Unity quality level index (-1 = keep the game's).");
        _applyVolumes = c.Bind(s, "ApplySavedVolumesAtStart", true, "Push the saved music/SFX/voice volumes to the mixer at start (the game only does so when its menus open).");
        _muteMusic = c.Bind(s, "MuteMusic", false, "Silence music completely (the game's lowest volume step is still audible).");
        _muteSfx = c.Bind(s, "MuteSfx", false, "Silence sound effects completely.");
        _muteVoice = c.Bind(s, "MuteVoice", false, "Silence voices completely.");
        _master = c.Bind(s, "MasterVolume", 1f, "Overall volume (0 to 1) on top of the music, effects and voice settings.");
        _muteInBackground = c.Bind(s, "MuteInBackground", false, "Silence the game while its window is not in front (with RunInBackground it keeps running quietly).");
        _focus = new GameObject("WWDS_Focus");
        UnityEngine.Object.DontDestroyOnLoad(_focus);
        _focus.AddComponent<FocusWatcher>().Changed += _ => ApplyMaster();
        ApplyMaster();
        _ui = ModUi.For(Context);
        _current = this;
        Context.Provide(this);
        _ui.Patch(new PatchSpec("mods.display-guard", "Keep the mod's window mode when the Tech room re-applies its resolution (always fullscreen).",
            () => AccessTools.Method(typeof(SDataStore), "SetScreenResolution", new[] { typeof(int) }), ModCore.Capabilities.GameCapability.None)
        {
            Prefix = AccessTools.Method(typeof(GameSettingsFeature), nameof(SetScreenResolutionPrefix)),
        });
        foreach (var setter in new[] { "SetMusicVolume", "SetSFXVolume", "SetVoiceVolume" })
        {
            var name = setter;
            _ui.Patch(new PatchSpec("mods.mute." + name, "Re-apply the mod's mute after the game sets a volume.",
                () => AccessTools.Method(typeof(SSoundManager), name, new[] { typeof(float) }), ModCore.Capabilities.GameCapability.None)
            {
                Postfix = AccessTools.Method(typeof(GameSettingsFeature), nameof(SoundSetterPostfix)),
            });
        }

        SceneManager.sceneLoaded += OnSceneLoaded;
        if (_manageDisplay.Value) ApplyDisplay();
    }

    protected override void OnStop()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        if (_focus != null) UnityEngine.Object.Destroy(_focus);
        AudioListener.volume = 1f;
        _current = null;
    }

    public override void Tick()
    {
        ApplyMaster();
        if (_volumesApplied || !_applyVolumes.Value || SDataStore.Instance == null || SSoundManager.Instance == null) return;
        if (_ui.State.Length == 0 || _ui.State == "StateInit") return;
        _volumesApplied = true;
        var d = SDataStore.Instance.SettingsData;
        ApplyVolume(Channel.Music, d.MusicVolume, save: false);
        ApplyVolume(Channel.Sfx, d.SfxVolume, save: false);
        ApplyVolume(Channel.Voice, d.VoiceVolume, save: false);
        Log.Info("Applied saved volumes: music " + d.MusicVolume + ", sfx " + d.SfxVolume + ", voice " + d.VoiceVolume);
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        // GraphicsController.Awake resets the frame cap and vsync whenever its scene loads.
        if (_manageDisplay.Value) ApplyFrameSettings();
    }

    // ---- volumes ------------------------------------------------------------------------------------

    /// <summary>Overall volume 0..1 (AudioListener), independent of the game's three channels.</summary>
    public float MasterVolume
    {
        get => Mathf.Clamp01(_master.Value);
        set
        {
            _master.Value = Mathf.Clamp01(value);
            ApplyMaster();
        }
    }

    public bool MuteInBackground
    {
        get => _muteInBackground.Value;
        set
        {
            _muteInBackground.Value = value;
            ApplyMaster();
        }
    }

    /// <summary>Listener volume for the settings: master volume, 0 while unfocused with the background mute, 0 when forced.</summary>
    internal static float TargetVolume(float master, bool muteInBackground, bool focused, bool forceMute) =>
        forceMute || (muteInBackground && !focused) ? 0f : Mathf.Clamp01(master);

    /// <summary>Silence regardless of settings (unattended self-test runs); not saved.</summary>
    public static bool ForceMute { get; set; }

    /// <summary>Master volume, or silence while unfocused when MuteInBackground is on.</summary>
    private void ApplyMaster()
    {
        if (_focus != null) _focused = _focus.GetComponent<FocusWatcher>().Focused;
        var target = TargetVolume(_master.Value, _muteInBackground.Value, _focused, ForceMute);
        if (!Mathf.Approximately(AudioListener.volume, target)) AudioListener.volume = target;
    }

    /// <summary>
    /// Unity calls OnApplicationFocus on every MonoBehaviour, also when the game stops updating in the
    /// background, so the mute is applied before the player loop pauses.
    /// </summary>
    private sealed class FocusWatcher : MonoBehaviour
    {
        public bool Focused { get; private set; } = true;

        public event Action<bool>? Changed;

        private void OnApplicationFocus(bool focus)
        {
            Focused = focus;
            Changed?.Invoke(focus);
        }
    }

    public enum Channel
    {
        Music,
        Sfx,
        Voice,
    }

    public float Volume(Channel channel)
    {
        var d = SDataStore.Instance?.SettingsData;
        if (d == null) return 1;
        return channel switch
        {
            Channel.Music => d.MusicVolume,
            Channel.Sfx => d.SfxVolume,
            _ => d.VoiceVolume,
        };
    }

    /// <summary>
    /// Sets and saves a volume in the game's own steps (0.1..1.0): the Tech room and pause menu bars index
    /// by value*10-1, so other values would crash or be snapped there. Silence is the separate mute.
    /// </summary>
    public void SetVolume(Channel channel, float value) => ApplyVolume(channel, Mathf.Clamp(Mathf.Round(value * 10f) / 10f, 0.1f, 1f), save: true);

    public bool Muted(Channel channel) => MuteValue(channel).Value;

    public void SetMuted(Channel channel, bool muted)
    {
        MuteValue(channel).Value = muted;
        ApplyVolume(channel, Volume(channel), save: false);
    }

    private IConfigValue<bool> MuteValue(Channel channel) => channel switch
    {
        Channel.Music => _muteMusic,
        Channel.Sfx => _muteSfx,
        _ => _muteVoice,
    };

    private void ApplyVolume(Channel channel, float value, bool save)
    {
        var store = SDataStore.Instance;
        var sound = SSoundManager.Instance;
        if (store == null || sound == null) return;
        value = Mathf.Max(value, 0.1f);
        switch (channel)
        {
            case Channel.Music:
                if (save) store.SetMusicVolume(value);
                else sound.SetMusicVolume(value);
                break;
            case Channel.Sfx:
                if (save) store.SetSFXVolume(value);
                else sound.SetSFXVolume(value);
                break;
            default:
                if (save) store.SetVoiceVolume(value);
                else sound.SetVoiceVolume(value);
                break;
        }

        // The SSoundManager postfix re-applies the mute after every setter call.
    }

    private static void SoundSetterPostfix(MethodBase __originalMethod)
    {
        PatchGuard.Run("mods.mute", () =>
        {
            var f = _current;
            if (f == null) return;
            var channel = __originalMethod.Name switch
            {
                "SetMusicVolume" => Channel.Music,
                "SetSFXVolume" => Channel.Sfx,
                _ => Channel.Voice,
            };
            if (f.Muted(channel)) Mute(channel);
        });
    }

    private static void Mute(Channel channel)
    {
        var field = typeof(SSoundManager).GetField("VoiceAudioMixer", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(SSoundManager.Instance) is not AudioMixerGroup group || group.audioMixer == null) return;
        var names = channel switch
        {
            Channel.Music => new[] { "volMusic" },
            Channel.Sfx => new[] { "volUI", "volAmbient", "volSFX" },
            _ => new[] { "volVoice" },
        };
        foreach (var n in names) group.audioMixer.SetFloat(n, -80f);
    }

    // ---- display ------------------------------------------------------------------------------------

    /// <summary>Distinct resolutions offered by the monitor, smallest first ("1920x1080").</summary>
    public IList<string> Resolutions()
    {
        var list = Screen.resolutions.Select(r => r.width + "x" + r.height).Distinct().ToList();
        var current = Screen.width + "x" + Screen.height;
        if (!list.Contains(current)) list.Add(current);
        return list;
    }

    public string CurrentResolution => _manageDisplay.Value && _resolution.Value.Length > 0 ? _resolution.Value : Screen.width + "x" + Screen.height;

    public WindowMode CurrentWindowMode => _manageDisplay.Value ? _windowMode.Value : Screen.fullScreen ? WindowMode.Fullscreen : WindowMode.Windowed;

    public int FrameRate => _manageDisplay.Value ? _frameRate.Value : Application.targetFrameRate;

    public bool VSync => _manageDisplay.Value ? _vsync.Value : QualitySettings.vSyncCount > 0;

    public int Quality => QualitySettings.GetQualityLevel();

    public void SetResolution(string resolution)
    {
        _resolution.Value = resolution;
        Manage();
    }

    public void SetWindowMode(WindowMode mode)
    {
        _windowMode.Value = mode;
        Manage();
    }

    public void SetFrameRate(int fps)
    {
        _frameRate.Value = fps;
        Manage();
    }

    public void SetVSync(bool on)
    {
        _vsync.Value = on;
        Manage();
    }

    public void SetQuality(int level)
    {
        _quality.Value = level;
        Manage();
    }

    private void Manage()
    {
        if (!_manageDisplay.Value)
        {
            // Freeze the current state first so untouched values keep what the player sees now.
            if (_resolution.Value.Length == 0) _resolution.Value = Screen.width + "x" + Screen.height;
            _manageDisplay.Value = true;
        }

        ApplyDisplay();
    }

    private void ApplyDisplay()
    {
        try
        {
            if (TryParseResolution(_resolution.Value, out var w, out var h)) Screen.SetResolution(w, h, ToUnity(_windowMode.Value));
            else Screen.fullScreenMode = ToUnity(_windowMode.Value);
            if (_quality.Value >= 0 && _quality.Value < QualitySettings.names.Length) QualitySettings.SetQualityLevel(_quality.Value, true);
            ApplyFrameSettings();
        }
        catch (Exception ex)
        {
            Log.Warn("Display settings failed: " + ex.Message);
        }
    }

    private void ApplyFrameSettings()
    {
        QualitySettings.vSyncCount = _vsync.Value ? 1 : 0;
        Application.targetFrameRate = _frameRate.Value;
    }

    private static FullScreenMode ToUnity(WindowMode mode) => mode switch
    {
        WindowMode.Borderless => FullScreenMode.FullScreenWindow,
        WindowMode.Windowed => FullScreenMode.Windowed,
        _ => FullScreenMode.ExclusiveFullScreen,
    };

    public static bool TryParseResolution(string text, out int width, out int height)
    {
        width = height = 0;
        var parts = (text ?? string.Empty).ToLowerInvariant().Split('x');
        return parts.Length == 2
            && int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out width)
            && int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out height)
            && width >= 640 && height >= 480;
    }

    private static bool SetScreenResolutionPrefix()
    {
        try
        {
            if (_current == null || !_current.ManageDisplay) return true;
            _current.ApplyDisplay();
            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }
}
