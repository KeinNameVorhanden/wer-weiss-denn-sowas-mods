using WWDS.ModCore.Events;
using WWDS.ModCore.Model;
using Xunit;

namespace WWDS.Tests.Core;

public class EventBusTests
{
    [Fact]
    public void FailingHandler_DoesNotStopOtherHandlers_AndIsReported()
    {
        var errors = new List<(object, Exception)>();
        var bus = new EventBus((e, ex) => errors.Add((e, ex)));
        var received = new List<string>();
        bus.Subscribe<ScoreChanged>(_ => throw new InvalidOperationException("broken feature"));
        bus.Subscribe<ScoreChanged>(e => received.Add("typed " + e.NewScore));
        bus.SubscribeAll(e => received.Add("all " + e.GetType().Name));

        bus.Publish(new ScoreChanged(TeamSlot.A, 0, 500, "test"));

        Assert.Equal(new[] { "typed 500", "all ScoreChanged" }, received);
        Assert.Single(errors);
        Assert.IsType<ScoreChanged>(errors[0].Item1);
    }

    [Fact]
    public void UnsubscribingInsideHandler_TakesEffectFromNextPublish_WithoutBreakingCurrentDispatch()
    {
        var bus = new EventBus((_, ex) => throw ex);
        var calls = 0;
        IDisposable? sub = null;
        sub = bus.Subscribe<GameLoaded>(_ =>
        {
            calls++;
            sub!.Dispose();
        });
        var other = 0;
        bus.Subscribe<GameLoaded>(_ => other++);

        bus.Publish(new GameLoaded());
        bus.Publish(new GameLoaded());

        Assert.Equal(1, calls);
        Assert.Equal(2, other);
    }

    [Fact]
    public void SubscribersToBaseType_ReceiveDerivedEvents()
    {
        var bus = new EventBus((_, ex) => throw ex);
        var seen = new List<string>();
        bus.Subscribe<GameEvent>(e => seen.Add(e.GetType().Name));

        bus.Publish(new GameLoaded());
        bus.Publish(new RoundFinished(3));

        Assert.Equal(new[] { "GameLoaded", "RoundFinished" }, seen);
    }
}
