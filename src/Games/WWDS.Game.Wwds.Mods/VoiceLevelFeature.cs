using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;
using WWDS.ModCore.Audio;
using WWDS.ModCore.Config;
using WWDS.ModCore.Features;

namespace WWDS.Game.Wwds.Mods;

/// <summary>
/// Voice normalization: every speaker's source gets a <see cref="Leveler"/>, so quiet host lines are
/// raised, loud ones lowered and peaks limited. The game's voice clips are streamed and cannot be read
/// in advance, so each line is measured the first time it plays and remembered (voice-levels.txt in the
/// mod data folder); from then on it plays with a constant gain.
/// </summary>
public sealed class VoiceLevelFeature : FeatureBase
{
    public const string FeatureId = "VoiceLevel";

    private readonly Dictionary<string, float> _levels = new();
    private readonly List<VoiceLevelFilter> _filters = new();
    private IConfigValue<bool> _enabled = null!;
    private IConfigValue<float> _target = null!;
    private IConfigValue<float> _maxBoost = null!;
    private string? _file;
    private bool _dirty;
    private float _saveAt;

    public override string Id => FeatureId;

    public override string DisplayName => "Voice normalization";

    public override string Description => "Brings every voice line to the same loudness and limits peaks (learns each line on its first play).";

    protected override string LogCategoryName => "Mods";

    /// <summary>Number of lines with a learned level.</summary>
    public int Known => _levels.Count;

    public bool Enabled
    {
        get => _enabled.Value;
        set => _enabled.Value = value;
    }

    protected override void OnStart()
    {
        var c = Context.Config;
        _enabled = c.Bind("Voice", "Normalize", true, "Same loudness for every voice line, peaks limited.");
        _target = c.Bind("Voice", "TargetLevelDb", -20f, "Speech level every line is brought to (dBFS RMS, -26 quieter to -16 louder).");
        _maxBoost = c.Bind("Voice", "MaxBoostDb", 12f, "Largest boost for a quiet line in dB (0 = only make loud lines quieter).");
        var dir = Context.Host?.DataDirectory;
        if (dir != null)
        {
            _file = Path.Combine(dir, "voice-levels.txt");
            Load();
        }

        VoiceFeature.SourceCreated += Attach;
        Context.Provide(this);
    }

    protected override void OnStop()
    {
        VoiceFeature.SourceCreated -= Attach;
        foreach (var f in _filters.Where(f => f != null)) UnityEngine.Object.Destroy(f);
        _filters.Clear();
        Save();
    }

    public override void Tick()
    {
        foreach (var f in _filters)
        {
            if (f == null) continue;
            f.enabled = _enabled.Value;
            f.Configure(_target.Value, _maxBoost.Value);
        }

        if (_dirty && Time.unscaledTime >= _saveAt) Save();
    }

    private void Attach(EVoiceType speaker, AudioSource source)
    {
        var filter = source.gameObject.AddComponent<VoiceLevelFilter>();
        filter.Init(this, source, speaker);
        filter.enabled = _enabled.Value;
        filter.Configure(_target.Value, _maxBoost.Value);
        _filters.Add(filter);
        Log.Info("Voice leveler on " + speaker);
    }

    /// <summary>Lines of the same name exist in every show, so the clip length is part of the key.</summary>
    internal static string Key(AudioClip clip) => clip.name + "|" + Mathf.RoundToInt(clip.length * 1000f);

    internal void Report(string message) => Log.Info("Voice leveler: " + message);

    internal float? Level(AudioClip clip) => _levels.TryGetValue(Key(clip), out var db) ? db : (float?)null;

    internal void Learn(AudioClip clip, float db)
    {
        _levels[Key(clip)] = db;
        if (_levels.Count <= 3 || _levels.Count % 50 == 0) Log.Info("Voice level learned: " + clip.name + " " + db.ToString("0.0", CultureInfo.InvariantCulture) + " dB (" + _levels.Count + " lines known)");
        if (!_dirty) _saveAt = Time.unscaledTime + 10f;
        _dirty = true;
    }

    /// <summary>Usual gain of a speaker (average over its learned lines), the start for a new line.</summary>
    internal float SeedGain(Leveler leveler, EVoiceType speaker)
    {
        var prefix = SpeakerPrefix(speaker);
        var gains = _levels.Where(p => prefix.Length > 0 && p.Key.StartsWith(prefix, StringComparison.Ordinal)).Select(p => leveler.GainFor(p.Value)).ToList();
        return gains.Count == 0 ? 0f : gains.Average();
    }

    private static string SpeakerPrefix(EVoiceType speaker) => speaker switch
    {
        EVoiceType.Pflaume => "speech_kp_",
        EVoiceType.Elton => "speech_elton_",
        EVoiceType.Hoecker => "speech_hoecker_",
        EVoiceType.Male => "speech_male_",
        EVoiceType.Female => "speech_female_",
        EVoiceType.Director => "speech_director_",
        _ => "speech_guest",
    };

    private void Load()
    {
        if (_file == null || !File.Exists(_file)) return;
        try
        {
            foreach (var line in File.ReadAllLines(_file))
            {
                var tab = line.LastIndexOf('\t');
                if (tab > 0 && float.TryParse(line.Substring(tab + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var db)) _levels[line.Substring(0, tab)] = db;
            }

            Log.Info("Voice levels: " + _levels.Count + " lines known");
        }
        catch (Exception ex)
        {
            Log.Warn("Voice levels could not be read: " + ex.Message);
        }
    }

    private void Save()
    {
        if (_file == null || !_dirty) return;
        _dirty = false;
        try
        {
            File.WriteAllLines(_file, _levels.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "\t" + p.Value.ToString("0.0", CultureInfo.InvariantCulture)).ToArray());
        }
        catch (Exception ex)
        {
            Log.Warn("Voice levels could not be saved: " + ex.Message);
        }
    }
}

/// <summary>Runs a speaker's <see cref="Leveler"/> in the audio chain of its source.</summary>
public sealed class VoiceLevelFilter : MonoBehaviour
{
    private VoiceLevelFeature? _owner;
    private AudioSource? _source;
    private EVoiceType _speaker;
    private Leveler _leveler = new(48000);
    private AudioClip? _clip;
    private bool _playing;
    private volatile int _blocks;
    private bool _reported;
    private int _lines;

    internal void Init(VoiceLevelFeature owner, AudioSource source, EVoiceType speaker)
    {
        _owner = owner;
        _source = source;
        _speaker = speaker;
        _leveler = new Leveler(AudioSettings.outputSampleRate);
    }

    internal void Configure(float target, float maxBoost)
    {
        _leveler.TargetDb = target;
        _leveler.MaxGainDb = Mathf.Max(0f, maxBoost);
    }

    private void Update()
    {
        if (_owner == null || _source == null) return;
        _leveler.SetInputScale(AudioListener.volume);
        var playing = _source.isPlaying;
        var clip = _source.clip;
        var started = playing && (!_playing || clip != _clip);
        var ended = !playing && _playing;
        _playing = playing;
        if (!started && !ended) return;

        if (_blocks > 0 && !_reported)
        {
            _reported = true;
            _owner.Report(_speaker + ": audio flowing at " + AudioSettings.outputSampleRate + " Hz");
        }

        // A line ended or another one started: keep the measurement, set up the next line.
        if (_clip != null && _owner.Level(_clip) == null)
        {
            var measured = _leveler.Finish();
            if (measured is { } db) _owner.Learn(_clip, db);
            else if (AudioListener.volume > 0f && _lines++ < 3) _owner.Report(_speaker + ": " + _clip.name + " too short or silent to measure (" + _blocks + " blocks so far)");
        }

        _clip = clip;
        if (started && clip != null) _leveler.Begin(_owner.Level(clip), _owner.SeedGain(_leveler, _speaker));
    }

    private void OnAudioFilterRead(float[] data, int channels)
    {
        _blocks++;
        _leveler.Process(data, channels);
    }
}
