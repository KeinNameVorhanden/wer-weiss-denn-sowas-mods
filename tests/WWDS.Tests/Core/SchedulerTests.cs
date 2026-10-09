using WWDS.ModCore.Threading;
using WWDS.ModCore.Time;
using Xunit;

namespace WWDS.Tests.Core;

public class SchedulerTests
{
    [Fact]
    public void RunsDueActionsInDueOrder_SkipsCancelled_AndAllowsReschedulingFromInsideAnAction()
    {
        var clock = new ManualClock();
        var errors = new List<Exception>();
        var s = new Scheduler(clock, errors.Add);
        var trace = new List<string>();

        s.After(2, () => trace.Add("b"));
        s.After(1, () => trace.Add("a"));
        var cancelled = s.After(1.5, () => trace.Add("cancelled"));
        s.After(1, () =>
        {
            trace.Add("a2");
            s.After(0, () => trace.Add("chained"));
        });
        s.After(1.2, () => throw new InvalidOperationException());
        cancelled.Dispose();

        clock.Advance(0.5);
        s.Tick();
        Assert.Empty(trace);

        clock.Advance(2);
        s.Tick();
        Assert.Equal(new[] { "a", "a2", "b" }, trace);
        Assert.Single(errors);

        s.Tick();
        Assert.Equal(new[] { "a", "a2", "b", "chained" }, trace);
        Assert.Equal(0, s.Count);
    }
}
