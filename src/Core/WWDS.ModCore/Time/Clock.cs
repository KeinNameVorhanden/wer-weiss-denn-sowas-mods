using System;
using System.Diagnostics;

namespace WWDS.ModCore.Time;

/// <summary>Monotonic time source. In-game the host uses Unity's realtime clock; tests use <see cref="ManualClock"/>.</summary>
public interface IClock
{
    /// <summary>Seconds since an arbitrary start, never goes backwards, unaffected by Time.timeScale.</summary>
    double Now { get; }

    DateTime UtcNow { get; }
}

public sealed class StopwatchClock : IClock
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();

    public double Now => _watch.Elapsed.TotalSeconds;

    public DateTime UtcNow => DateTime.UtcNow;
}

public sealed class ManualClock : IClock
{
    private readonly DateTime _epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public double Now { get; private set; }

    public DateTime UtcNow => _epoch.AddSeconds(Now);

    public void Advance(double seconds)
    {
        if (seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        Now += seconds;
    }
}
