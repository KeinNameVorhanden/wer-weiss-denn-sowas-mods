using WWDS.ModCore;
using WWDS.ModCore.Capabilities;
using WWDS.ModCore.Config;
using WWDS.ModCore.Features;
using Xunit;

namespace WWDS.Tests.Core;

public class FeatureRegistryTests
{
    private sealed class TestFeature : FeatureBase
    {
        private readonly string _id;
        private readonly string[] _deps;
        private readonly GameCapability _caps;
        private readonly List<string> _trace;

        public TestFeature(string id, List<string> trace, GameCapability caps = GameCapability.None, params string[] deps)
        {
            _id = id;
            _trace = trace;
            _caps = caps;
            _deps = deps;
        }

        public bool ThrowOnStart { get; set; }

        public bool ThrowOnTick { get; set; }

        public override string Id => _id;

        public override string DisplayName => _id;

        public override IEnumerable<string> Dependencies => _deps;

        public override GameCapability RequiredCapabilities => _caps;

        protected override void OnStart()
        {
            if (ThrowOnStart) throw new InvalidOperationException("start failed");
            _trace.Add("start " + _id);
        }

        public override void Tick()
        {
            if (ThrowOnTick) throw new InvalidOperationException("tick failed");
        }

        protected override void OnStop() => _trace.Add("stop " + _id);
    }

    private static (FeatureRegistry, ModContext) Create()
    {
        var ctx = TestContext.Create();
        return (ctx.Features, ctx);
    }

    [Fact]
    public void StartAll_RespectsDependencies_RegardlessOfRegistrationOrder()
    {
        var (reg, ctx) = Create();
        var trace = new List<string>();
        reg.Register(new TestFeature("voting", trace, GameCapability.None, "audience"));
        reg.Register(new TestFeature("audience", trace, GameCapability.None, "twitch"));
        reg.Register(new TestFeature("twitch", trace));

        reg.StartAll(ctx, new CapabilitySet());

        Assert.Equal(new[] { "start twitch", "start audience", "start voting" }, trace);
    }

    [Fact]
    public void MissingCapability_BlocksFeatureAndItsDependents_OthersStillRun()
    {
        var (reg, ctx) = Create();
        var trace = new List<string>();
        var opponent = reg.Register(new TestFeature("opponent", trace, GameCapability.ControlBotAnswer));
        var naming = reg.Register(new TestFeature("naming", trace, GameCapability.None, "opponent"));
        var overlay = reg.Register(new TestFeature("overlay", trace));
        var caps = new CapabilitySet();
        caps.Deny(GameCapability.ControlBotAnswer, "patch failed");

        reg.StartAll(ctx, caps);

        Assert.Equal(FeatureState.MissingCapabilities, opponent.State);
        Assert.Contains("ControlBotAnswer", opponent.Detail);
        Assert.Equal(FeatureState.MissingDependency, naming.State);
        Assert.Equal(FeatureState.Running, overlay.State);
    }

    [Fact]
    public void StartException_FaultsOnlyThatFeature()
    {
        var (reg, ctx) = Create();
        var trace = new List<string>();
        var broken = reg.Register(new TestFeature("broken", trace) { ThrowOnStart = true });
        var fine = reg.Register(new TestFeature("fine", trace));

        reg.StartAll(ctx, new CapabilitySet());

        Assert.Equal(FeatureState.Faulted, broken.State);
        Assert.Equal(FeatureState.Running, fine.State);
    }

    [Fact]
    public void RepeatedTickFailures_FaultFeature_AndStopDependentsFirst()
    {
        var (reg, ctx) = Create();
        var trace = new List<string>();
        var flaky = new TestFeature("twitch", trace);
        var flakyInfo = reg.Register(flaky);
        var dependent = reg.Register(new TestFeature("voting", trace, GameCapability.None, "twitch"));
        reg.StartAll(ctx, new CapabilitySet());
        flaky.ThrowOnTick = true;

        for (var i = 0; i < FeatureRegistry.MaxConsecutiveTickFailures - 1; i++) reg.TickAll();
        Assert.Equal(FeatureState.Running, flakyInfo.State);
        reg.TickAll();

        Assert.Equal(FeatureState.Faulted, flakyInfo.State);
        Assert.Equal(FeatureState.Stopped, dependent.State);
        Assert.Equal(new[] { "start twitch", "start voting", "stop voting", "stop twitch" }, trace);
    }

    [Fact]
    public void DisabledInConfig_IsNotStarted()
    {
        var (reg, ctx) = Create();
        var trace = new List<string>();
        var info = reg.Register(new TestFeature("stats", trace));
        info.Enabled.Value = false;

        reg.StartAll(ctx, new CapabilitySet());

        Assert.Equal(FeatureState.Disabled, info.State);
        Assert.Empty(trace);
    }
}
