using WWDS.ModCore.Threading;
using Xunit;

namespace WWDS.Tests.Core;

public class MainThreadQueueTests
{
    [Fact]
    public async Task Drain_RunsItemsPostedFromOtherThreadsInOrder_AndIsolatesFailures()
    {
        var errors = new List<Exception>();
        var queue = new MainThreadQueue(errors.Add);
        var seen = new List<int>();

        var producers = Enumerable.Range(0, 4).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                var value = t * 1000 + i;
                queue.Post(() => seen.Add(value));
            }
        })).ToArray();
        await Task.WhenAll(producers);
        queue.Post(() => throw new InvalidOperationException("boom"));
        queue.Post(() => seen.Add(-1));

        while (queue.Drain() > 0) { }

        Assert.Equal(401, seen.Count);
        Assert.Equal(-1, seen[^1]);
        Assert.Single(errors);
        // Per-producer order must be preserved.
        foreach (var t in Enumerable.Range(0, 4))
        {
            var ofProducer = seen.Where(v => v >= t * 1000 && v < t * 1000 + 1000).ToList();
            Assert.Equal(ofProducer.OrderBy(v => v), ofProducer);
        }
    }

    [Fact]
    public void Drain_LimitsWorkPerFrame_SoFloodsSpillIntoNextFrame()
    {
        var queue = new MainThreadQueue(_ => { }, maxPerDrain: 10);
        for (var i = 0; i < 25; i++) queue.Post(() => { });

        Assert.Equal(10, queue.Drain());
        Assert.Equal(10, queue.Drain());
        Assert.Equal(5, queue.Drain());
        Assert.Equal(0, queue.Drain());
    }
}
