using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Audio;
using WWDS.ModCore.Config;
using WWDS.ModCore.Features;
using WWDS.Runtime.Patching;

namespace WWDS.Game.Wwds.Mods;

/// <summary>
/// Fewer cut-off voice lines. The game plays each speaker on one source and a new line replaces the
/// running one instantly, often only seconds after a line was started without waiting; its waits also
/// start before a streamed clip is audible. Now a new line of the same speaker waits for the running one
/// (up to a limit), a waited-for line really ends before the show goes on, a player moving the selection
/// only silences the reactions (contestants, captains) instead of the host too, and the studio audience
/// gets quieter while someone speaks. Each speaker also gets its own source object, so per-speaker audio
/// processing (normalization) can attach to it.
/// </summary>
public sealed class VoiceFeature : FeatureBase
{
    public const string FeatureId = "Voice";

    private static readonly EVoiceType[] Reactions = { EVoiceType.Male, EVoiceType.Female, EVoiceType.Elton, EVoiceType.Hoecker };
    private static VoiceFeature? _current;
    private IConfigValue<bool> _queue = null!;
    private IConfigValue<float> _maxWait = null!;
    private IConfigValue<bool> _waitForEnd = null!;
    private IConfigValue<bool> _keepHost = null!;
    private IConfigValue<float> _duck = null!;
    private float _duckLevel = 1f;

    public override string Id => FeatureId;

    public override string DisplayName => "Voice lines";

    public override string Description => "Lines of one speaker no longer cut each other off, waited-for lines end completely, quieter audience while someone speaks.";

    protected override string LogCategoryName => "Mods";

    /// <summary>Raised when a speaker's own source object is created (normalization attaches here).</summary>
    public static event System.Action<EVoiceType, AudioSource>? SourceCreated;

    protected override void OnStart()
    {
        var c = Context.Config;
        const string s = "Voice";
        _queue = c.Bind(s, "WaitForSameSpeaker", true, "A new line waits until the same speaker's running line is over (instead of cutting it off).");
        _maxWait = c.Bind(s, "MaxWaitSeconds", 4f, "Longest wait for a running line before the new one starts anyway.");
        _waitForEnd = c.Bind(s, "WaitForRealEnd", true, "When the game waits for a line, wait until it is really over (streamed lines start late).");
        _keepHost = c.Bind(s, "KeepHostOnInput", true, "Moving the selection silences contestants and captains but lets the host finish.");
        _duck = c.Bind(s, "AudienceDuck", 0.5f, "Audience level while someone speaks (1 = no change, 0.5 = half).");
        _current = this;
        Context.Provide(this);
        var ui = ModUi.For(Context);
        var none = ModCore.Capabilities.GameCapability.None;
        var t = typeof(SSoundManager);
        ui.Patch(new PatchSpec("mods.voice.sources", "Each speaker gets its own AudioSource object.",
            () => AccessTools.Method(t, nameof(SSoundManager.GetVoiceAudioSource), new[] { typeof(EVoiceType) }), none)
        {
            Prefix = AccessTools.Method(typeof(VoiceFeature), nameof(GetVoiceSourcePrefix)),
        });
        ui.Patch(new PatchSpec("mods.voice.queue-bundle", "Same-speaker queue and real end for show voice lines.",
            () => AccessTools.Method(t, nameof(SSoundManager.PlayVoiceFileFromAssetBundleAndWait), new[] { typeof(EVoiceType), typeof(string) }), none)
        {
            Postfix = AccessTools.Method(typeof(VoiceFeature), nameof(PlayPostfix)),
        });
        ui.Patch(new PatchSpec("mods.voice.queue-resources", "Same-speaker queue and real end for generic voice lines.",
            () => AccessTools.Method(t, nameof(SSoundManager.PlayVoiceFileAndWait), new[] { typeof(EVoiceType), typeof(string), typeof(string[]) }), none)
        {
            Postfix = AccessTools.Method(typeof(VoiceFeature), nameof(PlayPostfix)),
        });
        ui.Patch(new PatchSpec("mods.voice.stop", "Player input silences the reactions, not the host.",
            () => AccessTools.Method(t, nameof(SSoundManager.StopAllVoices)), none)
        {
            Prefix = AccessTools.Method(typeof(VoiceFeature), nameof(StopAllVoicesPrefix)),
        });
    }

    protected override void OnStop()
    {
        _current = null;
        Context.Get<GameSettingsFeature>()?.SetAudienceDuck(1f);
    }

    public override void Tick()
    {
        var target = AnyVoicePlaying() ? Mathf.Clamp01(_duck.Value) : 1f;
        if (Mathf.Approximately(target, _duckLevel)) return;
        // About 0.2 s down, 0.6 s back up.
        var speed = target < _duckLevel ? 2.5f : 0.8f;
        _duckLevel = Mathf.MoveTowards(_duckLevel, target, speed * Time.unscaledDeltaTime);
        Context.Get<GameSettingsFeature>()?.SetAudienceDuck(_duckLevel);
    }

    private static Dictionary<EVoiceType, AudioSource>? Sources()
    {
        var sound = GameSettingsFeature.ExistingSound;
        return sound == null ? null : AccessTools.Field(typeof(SSoundManager), "voiceAudioSources").GetValue(sound) as Dictionary<EVoiceType, AudioSource>;
    }

    private static bool AnyVoicePlaying()
    {
        var sources = Sources();
        if (sources == null) return false;
        foreach (var source in sources.Values)
        {
            if (source != null && source.isPlaying) return true;
        }

        return false;
    }

    private static void GetVoiceSourcePrefix(SSoundManager __instance, EVoiceType voiceType, ref Dictionary<EVoiceType, AudioSource> ___voiceAudioSources,
        List<AudioSource> ___audioSources, AudioMixerGroup ___VoiceAudioMixer)
    {
        if (_current == null) return;
        ___voiceAudioSources ??= new Dictionary<EVoiceType, AudioSource>();
        if (___voiceAudioSources.ContainsKey(voiceType)) return;
        var sources = ___voiceAudioSources;
        PatchGuard.Run("mods.voice.sources", () =>
        {
            var go = new GameObject("Voice_" + voiceType);
            go.transform.SetParent(__instance.transform, false);
            var source = go.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.outputAudioMixerGroup = ___VoiceAudioMixer;
            sources.Add(voiceType, source);
            ___audioSources.Add(source);
            SourceCreated?.Invoke(voiceType, source);
        });
    }

    private static void PlayPostfix(EVoiceType voiceType, ref IEnumerator __result)
    {
        var self = _current;
        if (self == null || SSoundManager.Instance == null) return;
        __result = self.Queued(voiceType, __result);
    }

    private IEnumerator Queued(EVoiceType speaker, IEnumerator original)
    {
        var source = SSoundManager.Instance.GetVoiceAudioSource(speaker);
        if (_queue.Value)
        {
            var waited = 0f;
            while (source != null && source.isPlaying && waited < _maxWait.Value)
            {
                waited += Time.deltaTime;
                yield return null;
            }
        }

        while (original.MoveNext()) yield return original.Current;
        if (!_waitForEnd.Value || source == null) yield break;
        var clip = source.clip;
        var extra = 0f;
        while (source != null && source.isPlaying && source.clip == clip && extra < 2f)
        {
            extra += Time.deltaTime;
            yield return null;
        }
    }

    private static bool StopAllVoicesPrefix()
    {
        var self = _current;
        if (self == null || !self._keepHost.Value || !FromPlayerInput()) return true;
        var sources = Sources();
        if (sources == null) return true;
        foreach (var speaker in Reactions)
        {
            if (sources.TryGetValue(speaker, out var source) && source != null) source.Stop();
        }

        return false;
    }

    /// <summary>The selection handlers of the category grid and the answer buttons (also inside their coroutines).</summary>
    private static bool FromPlayerInput()
    {
        foreach (var frame in new StackTrace(2, false).GetFrames() ?? new StackFrame[0])
        {
            var type = frame.GetMethod()?.DeclaringType;
            for (var level = 0; level < 2 && type != null; level++, type = type.DeclaringType)
            {
                if (type == typeof(ViewCategoryUI) || type == typeof(ViewQuestionUI)) return true;
            }
        }

        return false;
    }
}
