using WWDS.Audience.Engine;
using WWDS.Audience.Guessing;
using WWDS.Audience.Moderation;
using WWDS.Audience.Naming;
using WWDS.ModCore.Commands;
using WWDS.ModCore.Events;
using WWDS.ModCore.Logging;
using WWDS.ModCore.Time;
using Xunit;

namespace WWDS.Tests.Audience;

public class ModerationTests
{
    private static readonly ContentModerator Mod = new(new ModerationRules { MaxLength = 14, Denylist = new List<string> { "nazi", "idiot" } });

    [Theory]
    [InlineData("  Quiz   Master ", "Quiz Master")]
    [InlineData("<color=red>Bob</color>", "Bob")]
    [InlineData("Jürgen Größe", "Jürgen Größe")]
    [InlineData("R2-D2", "R2-D2")]
    public void Accepts_AndNormalizes(string input, string expected)
    {
        var r = Mod.Check(input);
        Assert.True(r.Accepted, r.Reason);
        Assert.Equal(expected, r.Text);
    }

    [Theory]
    [InlineData("N4z1 Fan", "blocked word")]
    [InlineData("1D10T", "blocked word")]
    [InlineData("bit.ly", "links not allowed")]
    [InlineData("A very long name indeed", "too long (max 14)")]
    [InlineData("Heyyyyy", "too many repeated characters")]
    [InlineData("12345", "needs a letter")]
    [InlineData("x", "too short")]
    [InlineData("Bob\U0001F600", "character")]
    public void Rejects(string input, string reasonStart)
    {
        var r = Mod.Check(input);
        Assert.False(r.Accepted);
        Assert.StartsWith(reasonStart, r.Reason);
    }
}

public class NamingInteractionTests
{
    private readonly ManualClock _clock = new();
    private readonly InteractionEngine _engine;

    public NamingInteractionTests() => _engine = new InteractionEngine(_ => { }, _clock, new ModLog(new ListSink(), "t"), seed: 3);

    private static Viewer V(string id) => new(id, id, id, Authority.Viewer, "t");

    private NamingInteraction Open(NameSelection selection, bool approval = false)
    {
        var n = new NamingInteraction(new InteractionOptions { Duration = 30 }, new ContentModerator(new ModerationRules()), selection, approval);
        Assert.True(_engine.Open(n));
        return n;
    }

    [Fact]
    public void SameNameFromSeveralViewers_MergesIntoOneEntry_MostSubmittedWins()
    {
        var n = Open(NameSelection.MostSubmitted);
        _engine.Submit("name", V("u1"), "Quiz Master");
        _engine.Submit("name", V("u2"), "quiz  master");
        _engine.Submit("name", V("u3"), "Qu1z Master");
        _engine.Submit("name", V("u4"), "Elton Fan");
        Assert.Equal(SubmissionStatus.Duplicate, _engine.Submit("name", V("u1"), "QUIZ MASTER").Status);

        _clock.Advance(30.1);
        _engine.Tick();
        _engine.Tick();

        Assert.Equal(2, n.Entries.Count);
        Assert.Equal("Quiz Master", n.Result!.WinningText);
        Assert.Equal("u1", n.Result.Winners[0].Id);
    }

    [Fact]
    public void ApprovalMode_OnlyApprovedEntriesCanWin_BannedNamesCannotReturn()
    {
        var n = Open(NameSelection.Random, approval: true);
        _engine.Submit("name", V("u1"), "Troll Name");
        _engine.Submit("name", V("u2"), "Nice Name");
        Assert.True(n.Ban("1"));
        Assert.True(n.Approve("Nice Name"));
        Assert.Equal(SubmissionStatus.Rejected, _engine.Submit("name", V("u3"), "troll name").Status);

        _clock.Advance(30.1);
        _engine.Tick();
        _engine.Tick();
        Assert.Equal("Nice Name", n.Result!.WinningText);
    }

    [Fact]
    public void ModeratorSelection_WaitsForPick()
    {
        var n = Open(NameSelection.Moderator);
        _engine.Submit("name", V("u1"), "Alpha");
        _engine.Submit("name", V("u2"), "Beta");
        _clock.Advance(30.1);
        _engine.Tick();
        _engine.Tick();
        Assert.True(n.AwaitingDecision);
        Assert.True(_engine.Force(n, "#2", new Viewer("m", "m", "Mod", Authority.Moderator, "t")));
        Assert.Equal(("Beta", ResultReason.Override), (n.Result!.WinningText, n.Result.Reason));
    }
}

public class GuessTests
{
    [Theory]
    [InlineData("25000", 25000)]
    [InlineData("25.000", 25000)]
    [InlineData("25,000", 25000)]
    [InlineData("25k", 25000)]
    [InlineData("1.5k", 1500)]
    [InlineData("2 Mio", 2000000)]
    [InlineData("12,5", 12.5)]
    [InlineData("3.75", 3.75)]
    public void ParsesCommonNumberFormats(string text, double expected)
    {
        Assert.True(GuessInteraction.TryParseNumber(text, out var v));
        Assert.Equal(expected, v, 6);
    }

    [Theory]
    [InlineData("lots")]
    [InlineData("")]
    [InlineData("1e400")]
    public void RejectsNonNumbers(string text) => Assert.False(GuessInteraction.TryParseNumber(text, out _));

    [Fact]
    public void NumberGuess_ClosestWins_TiesShare_AndWaitsForOutcome()
    {
        var clock = new ManualClock();
        var engine = new InteractionEngine(_ => { }, clock, new ModLog(new ListSink(), "t"));
        var g = new GuessInteraction(new InteractionOptions { Duration = 10, DecisionTimeout = 600 }, GuessKind.Number);
        engine.Open(g);
        engine.Submit("guess", new Viewer("1", "a", "A", Authority.Viewer, "t"), "5000");
        engine.Submit("guess", new Viewer("2", "b", "B", Authority.Viewer, "t"), "7000");
        engine.Submit("guess", new Viewer("3", "c", "C", Authority.Viewer, "t"), "9000");
        engine.Submit("guess", new Viewer("4", "d", "D", Authority.Viewer, "t"), "100k");

        clock.Advance(10.1);
        engine.Tick();
        engine.Tick();
        Assert.Equal(InteractionState.Evaluating, g.State);

        Assert.True(g.ProvideOutcome("8000"));
        engine.Tick();
        Assert.Equal(new[] { "B", "C" }, g.Result!.Winners.Select(w => w.DisplayName).OrderBy(x => x));
    }
}
