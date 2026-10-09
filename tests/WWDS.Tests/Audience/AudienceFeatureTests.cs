using WWDS.Audience;
using WWDS.Audience.Bindings;
using WWDS.Audience.Naming;
using WWDS.Audience.Opponents;
using WWDS.Audience.Voting;
using WWDS.ModCore;
using WWDS.ModCore.Adapters;
using WWDS.ModCore.Commands;
using WWDS.ModCore.Events;
using WWDS.ModCore.Model;
using WWDS.ModCore.Time;
using Xunit;

namespace WWDS.Tests.Audience;

public class AudienceFeatureTests
{
    private readonly ManualClock _clock = new();
    private readonly ModContext _ctx;
    private readonly FakeAdapter _adapter = new();
    private readonly List<string> _chat = new();

    public AudienceFeatureTests()
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

    [Fact]
    public void AssistVote_SubmitsChatMajorityForTheHuman_UnlessTestModeGateIsClosed()
    {
        _ctx.Config.Bind("Voting", "Mode", VoteMode.ShowOnly, "").Value = VoteMode.Assist;
        Start(new VotingFeature());
        var q = FakeAdapter.Question(correct: 1);
        var human = new ContestantInfo("A0", "James", TeamSlot.A, ContestantKind.Human, false);

        _ctx.Bus.Publish(new AnswersPresented(q, human));
        Chat("u1", "!vote c");
        Chat("u2", "C");
        Chat("u3", "!answer a");
        Chat("u2", "hello C");
        Run(26);

        Assert.Contains("submit A C", _adapter.Calls);
        Assert.Contains(_chat, l => l.StartsWith("Chat sagt C: A 33% (1) | B 0% (0) | C 67% (2)"));

        _adapter.Calls.Clear();
        _ctx.Get<GameActionGate>()!.DryRun = true;
        _ctx.Bus.Publish(new AnswersPresented(q, human));
        Chat("u1", "!vote b");
        Run(26);
        Assert.DoesNotContain(_adapter.Calls, c => c.StartsWith("submit"));
    }

    [Fact]
    public void Opponent_TurnsBotDecisionsIntoChatVotes_WithinTheGameDeadline()
    {
        _ctx.Config.Bind("Opponent", "Active", false, "").Value = true;
        var opponent = new OpponentFeature();
        Start(opponent);
        _adapter.Snapshot = FakeAdapter.HumanVsBot();
        _ctx.Bus.Publish(new SessionCreated(1, new List<ContestantInfo>()));
        Run(1);
        Assert.Contains("control B True", _adapter.Calls);
        Assert.Contains("rename B0 Twitch Chat", _adapter.Calls);

        // Master question: the game clock leaves 16 s, so the vote must close earlier than the configured 25 s.
        var request = new DecisionRequest(DecisionKind.MasterAnswer, TeamSlot.B, null, FakeAdapter.Question(correct: 0, QuestionKind.Master),
            new List<DecisionOption> { new(0, "A", "x"), new(1, "B", "y"), new(2, "C", "z") }, _clock.Now, _clock.Now + 16) { FallbackIndex = 1 };
        _ctx.Bus.Publish(new DecisionRequested(request));
        Chat("u1", "!vote c");
        Chat("u2", "!vote c");
        Chat("u3", "!vote a");
        Run(15.5);

        Assert.Equal((DecisionStatus.Resolved, 2), (request.Status, request.ChosenIndex));
        Assert.StartsWith("audience: AudienceMajority (3 votes)", request.ResolvedBy);
        Assert.Equal(1, opponent.Opponent.Statistics.Decisions);
    }

    [Fact]
    public void Opponent_WithoutVotes_AppliesFallbackRightAfterTheVote_NotAtTheGameDeadline()
    {
        _ctx.Config.Bind("Opponent", "Active", false, "").Value = true;
        _ctx.Config.Bind("Opponent", "AnswerVoteSeconds", 25f, "").Value = 10f;
        Start(new OpponentFeature());
        _adapter.Snapshot = FakeAdapter.HumanVsBot();
        _ctx.Bus.Publish(new SessionCreated(1, new List<ContestantInfo>()));
        Run(1);

        var request = new DecisionRequest(DecisionKind.Answer, TeamSlot.B, null, FakeAdapter.Question(),
            new List<DecisionOption> { new(0, "A", "x"), new(1, "B", "y"), new(2, "C", "z") }, _clock.Now, _clock.Now + 90) { FallbackIndex = 2 };
        _ctx.Bus.Publish(new DecisionRequested(request));
        Run(12);

        Assert.Equal((DecisionStatus.Resolved, 2), (request.Status, request.ChosenIndex));
        Assert.Contains("no votes: fallback", request.ResolvedBy);
    }

    [Fact]
    public void SelectedViewerMode_IgnoresEveryoneElse()
    {
        _ctx.Config.Bind("Opponent", "Active", false, "").Value = true;
        _ctx.Config.Bind("Opponent", "Mode", OpponentMode.Crowd, "").Value = OpponentMode.SelectedViewer;
        _ctx.Config.Bind("Opponent", "SelectedViewer", "", "").Value = "QuizFan";
        Start(new OpponentFeature());
        _adapter.Snapshot = FakeAdapter.HumanVsBot();
        _ctx.Bus.Publish(new SessionCreated(1, new List<ContestantInfo>()));
        Run(1);

        var request = new DecisionRequest(DecisionKind.Answer, TeamSlot.B, null, FakeAdapter.Question(),
            new List<DecisionOption> { new(0, "A", "x"), new(1, "B", "y"), new(2, "C", "z") }, _clock.Now, _clock.Now + 90) { FallbackIndex = 0 };
        _ctx.Bus.Publish(new DecisionRequested(request));
        for (var i = 0; i < 10; i++) Chat("crowd" + i, "!vote a");
        Chat("quizfan", "!vote b");
        Run(3);

        Assert.Equal((DecisionStatus.Resolved, 1), (request.Status, request.ChosenIndex));
    }

    [Fact]
    public void TwitchPoll_IsRequested_AndItsWinnerDecidesTheChatVote()
    {
        _ctx.Config.Bind("Voting", "UseTwitchPolls", false, "").Value = true;
        _ctx.Config.Bind("Voting", "AutoOpen", VoteAutoOpen.HumanTurns, "").Value = VoteAutoOpen.AllQuestions;
        Start(new VotingFeature());
        var polls = new List<PollCreateRequested>();
        _ctx.Bus.Subscribe<PollCreateRequested>(polls.Add);

        _ctx.Bus.Publish(new AnswersPresented(FakeAdapter.Question(), null));
        Chat("u1", "!vote a");
        Chat("u2", "!vote a");
        var poll = Assert.Single(polls);
        Assert.Equal(new[] { "A: sechs", "B: acht", "C: zehn" }, poll.Choices);

        _ctx.Bus.Publish(new PollUpdated("p1", poll.Title, "end", new List<KeyValuePair<string, int>> { new("A: sechs", 3), new("B: acht", 40), new("C: zehn", 1) }));
        Run(1);

        Assert.Contains(_chat, l => l.StartsWith("Chat sagt B"));
    }

    [Fact]
    public void ContestantGuess_IsScoredByTheContestantsLockedAnswer()
    {
        _ctx.Config.Bind("Guessing", "GuessContestantCorrect", false, "").Value = true;
        var guessing = new WWDS.Audience.Guessing.GuessingFeature();
        Start(guessing);
        var bot = new ContestantInfo("B0", "Christine", TeamSlot.B, ContestantKind.Bot, false);

        _ctx.Bus.Publish(new AnswersPresented(FakeAdapter.Question(), bot));
        Chat("optimist", "!guess ja");
        Chat("pessimist", "!guess nein");
        Chat("skeptic", "!guess NEIN");
        _ctx.Bus.Publish(new AnswerLocked(TeamSlot.B, 0, false, false, QuestionKind.Normal));
        Run(1);

        Assert.Equal(new[] { "pessimist", "skeptic" }, guessing.Leaderboard.Top(5).Select(kv => kv.Key).OrderBy(x => x));
    }

    [Fact]
    public void Naming_ModeratesAndRenamesTheBotContestant()
    {
        Start(new NamingFeature());
        _adapter.Snapshot = FakeAdapter.HumanVsBot();
        Assert.True(_ctx.Get<NamingFeature>()!.OpenNaming("opponent", 30));

        Chat("u1", "!name <b>Elton Fan</b>");
        Chat("u2", "!name elton fan");
        Chat("u3", "!name http://spam.example");
        Chat("u4", "!name Quizzer");
        Run(31);

        Assert.Contains("rename B0 Elton Fan", _adapter.Calls);
        Assert.Contains(_chat, l => l.StartsWith("Neuer Name: Elton Fan"));
    }

    [Fact]
    public void InGameSimulator_DrivesTheSameChatPathAsTwitch()
    {
        _ctx.Config.Bind("Voting", "AutoOpen", VoteAutoOpen.HumanTurns, "").Value = VoteAutoOpen.AllQuestions;
        Start(new VotingFeature());
        var sim = new AudienceSimulator(_ctx);
        var commands = new List<ChatCommandReceived>();
        _ctx.Bus.Subscribe<ChatCommandReceived>(commands.Add);

        _ctx.Bus.Publish(new AnswersPresented(FakeAdapter.Question(), null));
        sim.Votes("vote", new KeyValuePair<string, int>("A", 2), new KeyValuePair<string, int>("C", 5));
        sim.Say("lurker", "C");
        Run(26);

        Assert.Equal(7, commands.Count);
        Assert.Contains(_chat, l => l.StartsWith("Chat sagt C: A 25% (2) | B 0% (0) | C 75% (6)"));
    }

    [Fact]
    public void ModeratorControls_RequireAuthority_AndCanForceAResult()
    {
        _ctx.Config.Bind("Voting", "Mode", VoteMode.ShowOnly, "").Value = VoteMode.Assist;
        Start(new VotingFeature());
        _ctx.Bus.Publish(new AnswersPresented(FakeAdapter.Question(), new ContestantInfo("A0", "L", TeamSlot.A, ContestantKind.Human, false)));
        Chat("viewer", "!wwds force A");
        Run(1);
        Assert.DoesNotContain(_adapter.Calls, c => c.StartsWith("submit"));

        Chat("mod", "!wwds force B", Authority.Moderator);
        Run(1);
        Assert.Contains("submit A B", _adapter.Calls);
    }
}

public class BindingEngineTests
{
    [Fact]
    public void RulesMatchEventTypeAndConditions_FillTemplates_AndRespectCooldowns()
    {
        var actions = new BindingActions();
        var ran = new List<string>();
        actions.Register("chat.say", (args, _) =>
        {
            ran.Add(args["text"]);
            return ActionResult.Success();
        });
        var engine = new BindingEngine(actions);
        engine.Load(BindingEngine.Parse("""
            { "rules": [
              { "id": "cheer", "on": "CheerReceived", "when": { "Bits": ">=500", "Viewer.Login": "!=bot" }, "do": "chat.say", "args": { "text": "{Viewer.DisplayName} cheered {Bits}" }, "cooldown": 30 },
              { "id": "reward", "on": "ChannelPointRedeemed", "when": { "RewardTitle": "~opponent" }, "do": "chat.say", "args": { "text": "reward" } },
              { "id": "off", "on": "CheerReceived", "do": "chat.say", "args": { "text": "never" }, "enabled": false }
            ] }
            """).Rules);
        var fan = new Viewer("1", "fan", "Fan", Authority.Viewer, "twitch");

        engine.Handle(new CheerReceived(fan, 499, "", false), 0);
        engine.Handle(new CheerReceived(fan, 500, "", false), 1);
        engine.Handle(new CheerReceived(fan, 900, "", false), 10);
        engine.Handle(new CheerReceived(new Viewer("2", "bot", "Bot", Authority.Viewer, "twitch"), 900, "", false), 40);
        engine.Handle(new CheerReceived(fan, 900, "", false), 41);
        engine.Handle(new ChannelPointRedeemed(fan, "r1", "Name the OPPONENT", 100, ""), 50);
        var unknown = engine.Handle(new RaidReceived(fan, 3), 60);

        Assert.Equal(new[] { "Fan cheered 500", "Fan cheered 900", "reward" }, ran);
        Assert.Empty(unknown);
    }

    [Fact]
    public void ShippedExampleFile_IsValid_AndUsesOnlyKnownActions()
    {
        var file = BindingEngine.Parse(BindingEngine.ExampleJson());
        var known = new[] { "chat.say", "naming.open", "vote.open", "guess.open", "opponent.enable", "opponent.disable", "opponent.mode", "audience.pause", "audience.resume" };
        Assert.NotEmpty(file.Rules);
        Assert.All(file.Rules, r => Assert.Contains(r.Do, known));
        Assert.All(file.Rules, r => Assert.Contains(r.On, new[] { "ChannelPointRedeemed", "SubscriptionReceived", "CheerReceived", "RaidReceived", "AnswersPresented", "PollUpdated" }));
    }
}
