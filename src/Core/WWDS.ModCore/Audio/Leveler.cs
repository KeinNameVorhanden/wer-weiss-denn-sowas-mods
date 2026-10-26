using System;

namespace WWDS.ModCore.Audio;

/// <summary>
/// Loudness leveler for one voice: brings each line to a common level and never lets a sample pass the
/// ceiling. A line heard before plays with its learned gain (constant, no pumping); a new line starts at
/// the speaker's usual gain and follows its loudness slowly (faster down than up). Blocks quieter than
/// the gate (pauses, breaths) neither count for the measurement nor raise the gain. Unity scales a
/// source by the listener volume before its filters, so levels are measured relative to that volume
/// (<see cref="SetInputScale"/>) and nothing is learned while it is muted. Process runs on the audio
/// thread, the rest on the main thread; both are locked.
/// </summary>
public sealed class Leveler
{
    private const float DownDbPerSecond = 20f;
    private const float UpDbPerSecond = 6f;
    private const float WindowSeconds = 0.4f;
    private const float ReleaseSeconds = 0.05f;
    private readonly object _lock = new();
    private readonly int _sampleRate;
    private float _gainDb;
    private bool _learned;
    private float _shortMs;
    private double _sumSq;
    private long _measured;
    private float _envelope;
    private float _inputScale = 1f;

    public Leveler(int sampleRate) => _sampleRate = Math.Max(8000, sampleRate);

    /// <summary>Level every line is brought to (RMS of its speech, dBFS).</summary>
    public float TargetDb { get; set; } = -20f;

    public float MinGainDb { get; set; } = -6f;

    public float MaxGainDb { get; set; } = 12f;

    /// <summary>Blocks below this input level (dBFS) are treated as silence.</summary>
    public float GateDb { get; set; } = -50f;

    /// <summary>No output sample exceeds this level (dBFS).</summary>
    public float CeilingDb { get; set; } = -1f;

    /// <summary>Current gain in dB (for logs and tests).</summary>
    public float GainDb
    {
        get
        {
            lock (_lock) return _gainDb;
        }
    }

    /// <summary>Volume the input was already scaled by (the listener volume, 0..1).</summary>
    public void SetInputScale(float scale)
    {
        lock (_lock) _inputScale = Math.Max(0f, scale);
    }

    /// <summary>Starts a line: with its measured level when known, else from <paramref name="seedGainDb"/>.</summary>
    public void Begin(float? knownLevelDb, float seedGainDb)
    {
        lock (_lock)
        {
            _learned = knownLevelDb.HasValue;
            _gainDb = Clamp(knownLevelDb.HasValue ? TargetDb - knownLevelDb.Value : seedGainDb);
            _shortMs = 0f;
            _sumSq = 0;
            _measured = 0;
        }
    }

    /// <summary>Speech level of the line so far (dBFS), or null when less than half a second was above the gate.</summary>
    public float? Finish()
    {
        lock (_lock)
        {
            if (_measured < _sampleRate / 2) return null;
            return ToDb(_sumSq / _measured);
        }
    }

    /// <summary>Gain the leveler would use for a line of this level.</summary>
    public float GainFor(float levelDb) => Clamp(TargetDb - levelDb);

    /// <summary>Levels interleaved samples in place.</summary>
    public void Process(float[] data, int channels)
    {
        if (data.Length == 0 || channels <= 0) return;
        lock (_lock)
        {
            double sum = 0;
            for (var i = 0; i < data.Length; i++) sum += data[i] * data[i];
            var scale2 = _inputScale * _inputScale;
            var muted = scale2 < 1e-6f;
            var blockMs = muted ? 0f : (float)(sum / data.Length / scale2);
            var frames = data.Length / channels;
            var seconds = frames / (float)_sampleRate;
            var startDb = _gainDb;
            if (!muted && ToDb(blockMs) > GateDb)
            {
                _sumSq += sum / channels / scale2;
                _measured += frames;
                if (!_learned)
                {
                    var k = Math.Min(1f, seconds / WindowSeconds);
                    _shortMs = _shortMs <= 0f ? blockMs : _shortMs + ((blockMs - _shortMs) * k);
                    var wanted = Clamp(TargetDb - ToDb(_shortMs));
                    var step = (wanted < _gainDb ? DownDbPerSecond : UpDbPerSecond) * seconds;
                    _gainDb = wanted < _gainDb ? Math.Max(wanted, _gainDb - step) : Math.Min(wanted, _gainDb + step);
                }
            }

            var ceiling = (float)Math.Pow(10, CeilingDb / 20f);
            var release = (float)Math.Exp(-1.0 / (ReleaseSeconds * _sampleRate * channels));
            var from = FromDb(startDb);
            var to = FromDb(_gainDb);
            for (var i = 0; i < data.Length; i++)
            {
                var x = data[i] * (from + ((to - from) * i / data.Length));
                var a = Math.Abs(x);
                _envelope = a > _envelope ? a : (_envelope * release) + (a * (1f - release));
                if (_envelope > ceiling) x *= ceiling / _envelope;
                data[i] = x;
            }
        }
    }

    private float Clamp(float db) => Math.Max(MinGainDb, Math.Min(MaxGainDb, db));

    private static float ToDb(double meanSquare) => meanSquare <= 1e-12 ? -120f : (float)(10 * Math.Log10(meanSquare));

    private static float FromDb(float db) => (float)Math.Pow(10, db / 20f);
}
