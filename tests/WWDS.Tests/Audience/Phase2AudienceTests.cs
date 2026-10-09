using WWDS.Audience;
using WWDS.Audience.Guessing;
using WWDS.Audience.Naming;
using WWDS.Audience.Opponents;
using WWDS.Audience.Predictions;
using WWDS.Audience.Voting;
using WWDS.ModCore;
using WWDS.ModCore.Adapters;
using WWDS.ModCore.Commands;
using WWDS.ModCore.Events;
using WWDS.ModCore.Model;
using WWDS.ModCore.Time;
using Xunit;

namespace WWDS.Tests.Audience;

public class Phase2AudienceTests
{
    private readonly ManualClock _clock = new();
    private readonly ModContext _ctx;
    private readonly FakeAdapter _adapter = new();
    private readonly List<string> _chat = new();

    public Phase2AudienceTests()
    {
        _ctx = TestContext.Create(_clock);
        _ctx.AttachAdapter(_adapter);
        _ctx.Bus.Subscribe<ChatSendRequested>(e => _chat.Add(e.Text));
        _ctx.Provide(new GameActionGate(_ctx.Log));
    }

    private void Start(params WWDS.ModCore.Features.IFeature[] features)
    {
        _ctx.Features.Register(new AudienceFeature());
        foreach (var f in features) _ctx.Features.Register(f);
        _ctx.Features.StartAll(_ctx, _adapter.Capabilities);
        Assert.All(_ctx.Features.Features, f => Assert.Equal(WWDS.ModCore.Features.FeatureState.Running, f.State));
    }

    private void Chat(string user, string text, Authority a = Authority.Viewer)
    {
        var viewer = new Viewer("id-" + user, user, user, a, "mock");
        _ctx.Bus.Publish(new ChatMessageReceived(viewer, text, Guid.NewGuid().ToString()));
        var cmd = CommandParser.Parse(text, "!");
        if (cmd != null) _ctx.Commands.Execute(cmd, viewer.AsInvoker());
    }

    private void Run(double seconds)
    {
        for (var t = 0.0; t < seconds; t += 0.5)
        {
            _clock.Advance(0.5);
            _ctx.Scheduler.Tick();
            _ctx.Features.TickAll();
        }
    }

    private static DecisionRequest Answer(double now) => new(DecisionKind.Answer, TeamSlot.B, null, FakeAdapter.Question(),
        new List<DecisionOption> { new(0, "A", "x"), new(1, "B", "y"), new(2, "C", "z") }, now, now + 90) { FallbackIndex = 0 };

    [Fact]
    public void SelectedViewerGoingAfk_IsReplacedByAVolunteer_AndForcedRetargetWorks()
    {
        _ctx.Config.Bind("Opponent", "Active", false, "").Value = true;
        _ctx.Config.Bind("Opponent", "Mode", OpponentMode.Crowd, "").Value = OpponentMode.SelectedViewer;
        _ctx.Config.Bind("Opponent", "SelectedViewer", "", "").Value = "sleepy";
        _ctx.Config.Bind("Opponent", "AnswerVoteSeconds", 25f, "").Value = 8f;
        var opponent = new OpponentFeature();
        Start(opponent);
        _adapter.Snapshot = FakeAdapter.HumanVsBot();
        _ctx.Bus.Publish(new SessionCreated(1, new List<ContestantInfo>()));
        Run(1);
        Chat("eager", "!join");
        Chat("lurker", "hello");

        var first = Answer(_clock.Now);
        _ctx.Bus.Publish(new DecisionRequested(first));
        Chat("eager", "!vote c");
        Run(9);
        Assert.Equal((DecisionStatus.Resolved, 0), (first.Status, first.ChosenIndex));
        Assert.Equal("eager", opponent.CurrentDecider?.Login);
        Assert.Contains(_chat, l => l.Contains("@eager spielt jetzt") && l.Contains("sleepy ist AFK"));

        var second = Answer(_clock.Now);
        _ctx.Bus.Publish(new DecisionRequested(second));
        Chat("eager", "!vote b");
        Run(2);
        Assert.Equal(1, second.ChosenIndex);

        Chat("mod", "!opponent next", Authority.Moderator);
        Assert.Equal("lurker", opponent.CurrentDecider?.Login);
    }

    [Fact]
    public void UniversalVote_AnswersGuessAndNamingRounds()
    {
        var guessing = new GuessingFeature();
        Start(guessing, new NamingFeature());
        _adapter.Snapshot = FakeAdapter.HumanVsBot();

        Assert.True(guessing.OpenWinner(20, "Streamer (James)", "Twitch"));
        Chat("u1", "!vote b");
        Chat("u2", "!vote A");
        _ctx.Bus.Publish(new GameFinished(TeamSlot.B, 0, 500, false));
        Run(21);
        Assert.Equal(new[] { "u1" }, guessing.Leaderboard.Top(5).Select(kv => kv.Key));

        Assert.True(_ctx.Get<NamingFeature>()!.OpenNaming("opponent", 20));
        Chat("u1", "!name Quiz Fan");
        Chat("u2", "!name Elton Fan");
        Chat("u3", "!vote 2");
        Chat("u4", "!vote #2");
        Run(21);
        Assert.Contains("rename B0 Elton Fan", _adapter.Calls);
    }

    [Fact]
    public void WinnerBet_PublishesOptionTexts_ForTheVoteWindow()
    {
        var guessing = new GuessingFeature();
        Start(guessing);
        var status = new List<InteractionStatusChanged>();
        _ctx.Bus.Subscribe<InteractionStatusChanged>(status.Add);

        Assert.True(guessing.OpenWinner(30, "Streamer (James)", "KI (Maic)"));
        Chat("u1", "!vote b");
        Chat("u2", "!vote B");
        Chat("u3", "!vote a");
        Run(1);

        var last = status.Last(s => s.Title.Length > 0);
        Assert.Equal("Wer gewinnt?", last.Title);
        var rows = TallyLine.ParseAll(last.Lines);
        Assert.Equal(new[] { ("A", "Streamer (James)", 1), ("B", "KI (Maic)", 2) }, rows.Select(r => (r.Key, r.Label, r.Count!.Value)));
        Assert.Equal(0.67, rows[1].Share!.Value, 2);
    }

    [Fact]
    public void WinnerBet_ClosesAtTheFirstQuestion_HidesFromTheWindow_AndStillPaysOut()
    {
        _ctx.Config.Bind("WhoWins", "TwitchPrediction", false, "").Value = true;
        var guessing = new GuessingFeature();
        Start(guessing, new WhoWinsFeature());
        _adapter.Snapshot = FakeAdapter.HumanVsBot();
        var status = new List<InteractionStatusChanged>();
        var locks = 0;
        _ctx.Bus.Subscribe<InteractionStatusChanged>(status.Add);
        _ctx.Bus.Subscribe<PredictionLockRequested>(_ => locks++);

        _ctx.Bus.Publish(new GameStarted(1));
        Run(3);
        Chat("early", "!vote B");
        _ctx.Bus.Publish(new QuestionStarted(FakeAdapter.Question(), TeamSlot.A));
        Chat("late", "!vote B");
        Run(1);

        Assert.Equal(1, locks);
        Assert.Equal(string.Empty, status.Last().Title);
        _ctx.Bus.Publish(new GameFinished(TeamSlot.B, 0, 500, false));
        Run(1);
        Assert.Equal(new[] { "early" }, guessing.Leaderboard.Top(5).Select(kv => kv.Key));
    }

    [Theory]
    [InlineData(NumberAggregation.Average, 3400)]
    [InlineData(NumberAggregation.Median, 1000)]
    [InlineData(NumberAggregation.TrimmedAverage, 1033.333333)]
    public void NumberAggregation_Modes(NumberAggregation mode, double expected)
    {
        var values = new double[] { 900, 1000, 1000, 1100, 13000 };
        Assert.Equal(expected, NumberVoteInteraction.Aggregate(values, mode)!.Value, 6);
    }

    [Fact]
    public void WhoWins_LabelsTeamsByRole_CreatesPrediction_AndResolvesWithTheWinner()
    {
        _ctx.Config.Bind("WhoWins", "TwitchPrediction", false, "").Value = true;
        var whoWins = new WhoWinsFeature();
        Start(new GuessingFeature(), whoWins);
        var requests = new List<object>();
        _ctx.Bus.Subscribe<PredictionCreateRequested>(requests.Add);
        _ctx.Bus.Subscribe<PredictionResolveRequested>(requests.Add);
        _adapter.Snapshot = new GameSnapshot(GamePhase.CategorySelection, "StateShowLoop", null,
            new TeamInfo(TeamSlot.A, "Team Bernhard", 0, new List<ContestantInfo> { new("A0", "James", TeamSlot.A, ContestantKind.Human, false) }),
            new TeamInfo(TeamSlot.B, "Team Elton", 0, new List<ContestantInfo> { new("B0", "Twitch Chat", TeamSlot.B, ContestantKind.Bot, true) }),
            TeamSlot.A, null, true);

        _ctx.Bus.Publish(new GameStarted(4));
        Run(3);
        _ctx.Bus.Publish(new GameFinished(TeamSlot.B, 1000, 2500, false));

        var created = Assert.IsType<PredictionCreateRequested>(requests[0]);
        Assert.Equal(new[] { "Streamer (James)", "Twitch" }, created.Outcomes);
        Assert.Equal(1, Assert.IsType<PredictionResolveRequested>(requests[1]).WinningIndex);
        Assert.Contains(_chat, l => l.Contains("!vote A (Streamer (James))"));
    }
}
