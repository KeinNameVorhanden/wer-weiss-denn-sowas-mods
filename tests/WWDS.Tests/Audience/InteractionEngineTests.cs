using WWDS.Audience.Engine;
using WWDS.Audience.Voting;
using WWDS.ModCore.Commands;
using WWDS.ModCore.Events;
using WWDS.ModCore.Logging;
using WWDS.ModCore.Time;
using Xunit;

namespace WWDS.Tests.Audience;

public class InteractionEngineTests
{
    private readonly ManualClock _clock = new();
    private readonly List<object> _events = new();
    private readonly InteractionEngine _engine;

    public InteractionEngineTests()
    {
        _engine = new InteractionEngine(_events.Add, _clock, new ModLog(new ListSink(), "test"), seed: 42);
    }

    private static Viewer V(string id, Authority a = Authority.Viewer) => new(id, id, id, a, "test");

    private static List<VoteOption> Abc() => new() { new("A", "sechs"), new("B", "acht"), new("C", "zehn") };

    private VoteInteraction Vote(InteractionOptions? o = null, TieBreak tie = TieBreak.Random, int fallback = -1, Func<Viewer, double>? weight = null)
    {
        var v = new VoteInteraction(o ?? new InteractionOptions { Duration = 30 }, Abc(), tie, fallback, weight);
        Assert.True(_engine.Open(v));
        return v;
    }

    private InteractionResult RunToEnd(Interaction i)
    {
        _clock.Advance(i.Options.Duration + 0.1);
        for (var n = 0; n < 5 && i.IsActive; n++) _engine.Tick();
        return i.Result!;
    }

    [Fact]
    public void OneVotePerViewer_ChangesOnlyWhenAllowed()
    {
        var strict = Vote(new InteractionOptions { Duration = 10, AllowChanges = false });
        Assert.Equal(SubmissionStatus.Accepted, _engine.Submit("vote", V("u1"), "a").Status);
        Assert.Equal(SubmissionStatus.Duplicate, _engine.Submit("vote", V("u1"), "A").Status);
        Assert.Equal(SubmissionStatus.Rejected, _engine.Submit("vote", V("u1"), "B").Status);
        Assert.Equal(SubmissionStatus.Rejected, _engine.Submit("vote", V("u2"), "D").Status);
        Assert.Equal(SubmissionStatus.Accepted, _engine.Submit("vote", V("u2"), "3").Status);
        Assert.Equal(1, strict.VotesFor(0));
        Assert.Equal(1, strict.VotesFor(2));
        _engine.Cancel(strict, "next");

        var loose = Vote(new InteractionOptions { Duration = 10, AllowChanges = true });
        _engine.Submit("vote", V("u1"), "a");
        Assert.Equal(SubmissionStatus.Changed, _engine.Submit("vote", V("u1"), "b").Status);
        Assert.Equal((0, 1), (loose.VotesFor(0), loose.VotesFor(1)));
    }

    [Fact]
    public void LocksWhenTimeIsUp_AppliesExactlyOnce_ThenCooldownBlocksNextRound()
    {
        var applied = new List<InteractionResult>();
        var v = Vote(new InteractionOptions { Duration = 20 });
        v.OnApply = applied.Add;
        foreach (var (u, c) in new[] { ("u1", "B"), ("u2", "B"), ("u3", "A") }) _engine.Submit("vote", V(u), c);

        _clock.Advance(19.9);
        _engine.Tick();
        Assert.Equal(InteractionState.Collecting, v.State);
        _clock.Advance(0.2);
        _engine.Tick();
        _engine.Tick();
        _engine.Tick();

        var r = Assert.Single(applied);
        Assert.Equal((true, 1, ResultReason.Decided), (r.Success, r.WinningIndex, r.Reason));
        Assert.Equal(InteractionState.Complete, v.State);
        Assert.Equal(SubmissionStatus.Closed, _engine.Submit("vote", V("late"), "C").Status);

        _engine.Cooldowns["vote"] = 60;
        Assert.False(_engine.Open(new VoteInteraction(new InteractionOptions(), Abc())));
        _clock.Advance(60.1);
        Assert.True(_engine.Open(new VoteInteraction(new InteractionOptions(), Abc())));
    }

    [Fact]
    public void MissedQuorum_RetriesThenFallsBack()
    {
        var v = Vote(new InteractionOptions { Duration = 10, MinParticipants = 3, MaxRetries = 1 }, fallback: 2);
        _engine.Submit("vote", V("u1"), "A");

        _clock.Advance(10.1);
        _engine.Tick();
        _engine.Tick();
        Assert.Equal(InteractionState.Collecting, v.State);
        Assert.Equal(1, v.Retries);
        Assert.Equal(0, v.Participants);

        _engine.Submit("vote", V("u1"), "A");
        _engine.Submit("vote", V("u2"), "A");
        var r = RunToEnd(v);
        Assert.Equal((ResultReason.Fallback, 2), (r.Reason, r.WinningIndex));
    }

    [Fact]
    public void Revote_RunsOffBetweenTiedOptionsOnly()
    {
        var v = Vote(new InteractionOptions { Duration = 20 }, TieBreak.Revote);
        _engine.Submit("vote", V("u1"), "A");
        _engine.Submit("vote", V("u2"), "B");
        _engine.Submit("vote", V("u3"), "C");
        _engine.Submit("vote", V("u4"), "A");
        _engine.Submit("vote", V("u5"), "B");

        _clock.Advance(20.1);
        _engine.Tick();
        _engine.Tick();
        Assert.Equal(InteractionState.Collecting, v.State);
        Assert.Equal(new[] { 0, 1 }, v.Eligible);
        Assert.Equal(SubmissionStatus.Rejected, _engine.Submit("vote", V("u6"), "C").Status);
        Assert.Equal(SubmissionStatus.Changed, _engine.Submit("vote", V("u4"), "B").Status);

        var r = RunToEnd(v);
        Assert.Equal((ResultReason.Decided, 1), (r.Reason, r.WinningIndex));
    }

    [Fact]
    public void ModeratorTieBreak_WaitsForForce()
    {
        var v = Vote(new InteractionOptions { Duration = 10, DecisionTimeout = 30 }, TieBreak.Moderator);
        _engine.Submit("vote", V("u1"), "A");
        _engine.Submit("vote", V("u2"), "C");
        _clock.Advance(10.1);
        _engine.Tick();
        _engine.Tick();
        Assert.True(v.AwaitingDecision);
        Assert.Equal(InteractionState.Evaluating, v.State);

        Assert.False(_engine.Force(v, "Z", V("mod", Authority.Moderator)));
        Assert.True(_engine.Force(v, "c", V("mod", Authority.Moderator)));
        Assert.Equal((ResultReason.Override, 2, "mod"), (v.Result!.Reason, v.Result.WinningIndex, v.Result.DecidedBy));
    }

    [Fact]
    public void SubscriberWeight_CanOutweighHeadcount()
    {
        var v = Vote(weight: x => x.Authority == Authority.Subscriber ? 3 : 1);
        _engine.Submit("vote", V("s1", Authority.Subscriber), "C");
        _engine.Submit("vote", V("u1"), "A");
        _engine.Submit("vote", V("u2"), "A");

        var r = RunToEnd(v);
        Assert.Equal(2, r.WinningIndex);
        Assert.StartsWith("C  60%", v.TallyLines()[2]);
    }

    [Fact]
    public void AllowList_MatchesIdOrLogin_AndFloodIsLimited()
    {
        var v = Vote(new InteractionOptions { AllowedUserIds = new List<string> { "login:quizfan" }, EarlyCompleteAt = 1 });
        Assert.Equal(SubmissionStatus.NotAllowed, _engine.Submit("vote", V("someone"), "A").Status);
        Assert.Equal(SubmissionStatus.Accepted, _engine.Submit("vote", new Viewer("123", "QuizFan", "Quiz Fan", Authority.Viewer, "twitch"), "B").Status);
        Assert.Equal(InteractionState.Locked, v.State);

        _engine.Spam.MaxPerWindow = 3;
        _engine.Spam.WindowSeconds = 10;
        var flood = Vote();
        var statuses = Enumerable.Range(0, 5).Select(_ => _engine.Submit("vote", V("spammer"), "A").Status).ToList();
        Assert.Equal(SubmissionStatus.RateLimited, statuses[3]);
        Assert.Equal(SubmissionStatus.Duplicate, statuses[1]);
        Assert.Equal(SubmissionStatus.Accepted, _engine.Submit("vote", V("mod", Authority.Moderator), "B").Status);
        Assert.Equal(2, flood.Participants);
    }
}
