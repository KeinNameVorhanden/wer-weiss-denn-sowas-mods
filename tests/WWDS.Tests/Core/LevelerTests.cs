using System;
using System.Linq;
using WWDS.ModCore.Audio;
using Xunit;

namespace WWDS.Tests.Core;

public class LevelerTests
{
    private const int Rate = 48000;
    private const int Block = 1024;

    /// <summary>Feeds a stereo sine of the given amplitude for the given seconds; returns the output RMS in dBFS.</summary>
    private static double Feed(Leveler leveler, float amplitude, double seconds, double hz = 220)
    {
        double sum = 0;
        long n = 0;
        var t = 0;
        for (var done = 0.0; done < seconds; done += Block / (double)Rate)
        {
            var data = new float[Block * 2];
            for (var i = 0; i < Block; i++, t++)
            {
                var s = (float)(amplitude * Math.Sin(2 * Math.PI * hz * t / Rate));
                data[2 * i] = data[(2 * i) + 1] = s;
            }

            leveler.Process(data, 2);
            sum += data.Sum(x => (double)x * x);
            n += data.Length;
        }

        return 10 * Math.Log10(sum / n);
    }

    [Fact]
    public void QuietLine_IsMeasuredOnFirstPlay_AndBoostedOnTheNext()
    {
        var l = new Leveler(Rate);
        l.Begin(null, 0f);
        Feed(l, 0.03f, 2.0); // sine RMS = amplitude/sqrt2 -> about -33.5 dBFS
        var measured = l.Finish();
        Assert.NotNull(measured);
        Assert.InRange(measured!.Value, -34.5f, -32.5f);

        l.Begin(measured, 0f);
        var output = Feed(l, 0.03f, 1.0);
        Assert.InRange(output, -22.5, -19.5); // +12 dB cap from -33.5 lands a little under the -20 target
    }

    [Fact]
    public void NewLine_GainRisesSlowly_AndPausesDoNotPumpItUp()
    {
        var l = new Leveler(Rate);
        l.Begin(null, 0f);
        Feed(l, 0.03f, 1.0);
        Assert.InRange(l.GainDb, 4f, 7f); // 6 dB per second upwards, not a jump to +12

        Feed(l, 0.03f, 2.0);
        var beforePause = l.GainDb;
        Feed(l, 0.00001f, 3.0); // -100 dBFS breath/pause, below the gate
        Assert.Equal(beforePause, l.GainDb, 3);
    }

    [Fact]
    public void Peaks_NeverPassTheCeiling_EvenWithBoost()
    {
        var l = new Leveler(Rate) { CeilingDb = -1f };
        l.Begin(-30f, 0f); // learned quiet line -> +10 dB
        var data = Enumerable.Range(0, Block * 2).Select(i => i % 50 == 0 ? 0.9f : 0.05f).ToArray();
        l.Process(data, 2);
        Assert.True(data.Max(Math.Abs) <= Math.Pow(10, -1 / 20.0) + 1e-6);
    }

    [Fact]
    public void ListenerVolume_DoesNotChangeTheLearnedLevel_AndMutedPlaysLearnNothing()
    {
        var l = new Leveler(Rate);
        l.SetInputScale(0.25f); // master volume 25 %: Unity hands the filter a 12 dB quieter signal
        l.Begin(null, 0f);
        Feed(l, 0.03f * 0.25f, 2.0);
        Assert.InRange(l.Finish()!.Value, -34.5f, -32.5f);

        l.SetInputScale(0f);
        l.Begin(null, 0f);
        Feed(l, 0f, 2.0);
        Assert.Null(l.Finish());
    }

    [Fact]
    public void ShortOrSilentLine_YieldsNoMeasurement()
    {
        var l = new Leveler(Rate);
        l.Begin(null, 3f);
        Feed(l, 0.2f, 0.2);
        Feed(l, 0f, 2.0);
        Assert.Null(l.Finish());
    }
}
