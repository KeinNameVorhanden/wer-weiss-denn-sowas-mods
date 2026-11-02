using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace WWDS.ModCore.Model;

/// <summary>Frame pacing summary of measured frame times (seconds): average rate, spread and slow frames.</summary>
public sealed class FrameStats
{
    private FrameStats(double fps, double meanMs, double stdevMs, double p1Ms, double p99Ms, int frames)
    {
        Fps = fps;
        MeanMs = meanMs;
        StdevMs = stdevMs;
        P1Ms = p1Ms;
        P99Ms = p99Ms;
        Frames = frames;
    }

    public double Fps { get; }

    public double MeanMs { get; }

    /// <summary>Standard deviation of the frame time: how uneven the pacing is.</summary>
    public double StdevMs { get; }

    /// <summary>Fastest 1 % of frames.</summary>
    public double P1Ms { get; }

    /// <summary>Slowest 1 % of frames.</summary>
    public double P99Ms { get; }

    public int Frames { get; }

    public static FrameStats From(IList<double> seconds)
    {
        if (seconds.Count == 0) return new FrameStats(0, 0, 0, 0, 0, 0);
        var ms = seconds.Select(s => s * 1000.0).OrderBy(x => x).ToList();
        var mean = ms.Average();
        var stdev = Math.Sqrt(ms.Sum(x => (x - mean) * (x - mean)) / ms.Count);
        double Pick(double q) => ms[Math.Min(ms.Count - 1, (int)(q * (ms.Count - 1)))];
        return new FrameStats(1000.0 / mean, mean, stdev, Pick(0.01), Pick(0.99), ms.Count);
    }

    public override string ToString() => string.Format(CultureInfo.InvariantCulture,
        "{0:0.0} fps, {1:0.00} ms +/- {2:0.00}, 1% {3:0.00} ms, 99% {4:0.00} ms ({5} frames)", Fps, MeanMs, StdevMs, P1Ms, P99Ms, Frames);
}
