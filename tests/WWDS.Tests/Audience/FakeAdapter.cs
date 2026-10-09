using WWDS.ModCore;
using WWDS.ModCore.Adapters;
using WWDS.ModCore.Capabilities;
using WWDS.ModCore.Model;

namespace WWDS.Tests.Audience;

/// <summary>Scriptable game adapter: records every action so feature tests can assert on them.</summary>
internal sealed class FakeAdapter : IGameAdapter
{
    public FakeAdapter()
    {
        foreach (var cap in CapabilitySet.Split((GameCapability)long.MaxValue)) Capabilities.Grant(cap);
    }

    public List<string> Calls { get; } = new();

    public GameSnapshot Snapshot { get; set; } = GameSnapshot.Empty;

    public bool[] External { get; } = new bool[2];

    public GameId Game => GameId.Wwds1;

    public string DisplayName => "fake";

    public string AdapterVersion => "test";

    public CapabilitySet Capabilities { get; } = new();

    public void Initialize(ModContext context)
    {
    }

    public void Tick()
    {
    }

    public void Shutdown()
    {
    }

    public GameSnapshot GetSnapshot() => Snapshot;

    public ActionResult SetExternalControl(TeamSlot team, bool enabled)
    {
        External[(int)team] = enabled;
        Calls.Add("control " + team + " " + enabled);
        return ActionResult.Success();
    }

    public bool IsExternallyControlled(TeamSlot team) => External[(int)team];

    public ActionResult RenameContestant(TeamSlot team, int memberIndex, string name)
    {
        Calls.Add("rename " + team + memberIndex + " " + name);
        return ActionResult.Success();
    }

    public ActionResult SubmitAnswer(TeamSlot team, int optionIndex)
    {
        Calls.Add("submit " + team + " " + AnswerOption.KeyOf(optionIndex));
        return ActionResult.Success();
    }

    public IList<DecisionOption> GetOpenCategories() => new List<DecisionOption>();

    public ActionResult SelectCategory(TeamSlot team, int categoryIndex)
    {
        Calls.Add("category " + team + " " + categoryIndex);
        return ActionResult.Success();
    }

    public ActionResult RenameTeam(TeamSlot team, string? name)
    {
        Calls.Add("label " + team + " " + name);
        return ActionResult.Success();
    }

    public ActionResult StartShow(ShowSetup setup) => ActionResult.Fail("not supported");

    public static QuestionInfo Question(int correct = 1, QuestionKind kind = QuestionKind.Normal) => new(GameId.Wwds1, 1, 3, kind, "Natur", "Wie viele Beine hat eine Spinne?",
        new List<AnswerOption> { new(0, "sechs", correct == 0), new(1, "acht", correct == 1), new(2, "zehn", correct == 2) }, null);

    /// <summary>Human on team A vs bot on team B, in a show.</summary>
    public static GameSnapshot HumanVsBot(QuestionInfo? q = null) => new(GamePhase.WaitingForAnswer, "StateShowLoop", q,
        new TeamInfo(TeamSlot.A, "Team Bernhard", 0, new List<ContestantInfo> { new("A0", "James", TeamSlot.A, ContestantKind.Human, false) }),
        new TeamInfo(TeamSlot.B, "Team Elton", 0, new List<ContestantInfo> { new("B0", "Christine", TeamSlot.B, ContestantKind.Bot, false) }),
        TeamSlot.A, null, true);
}
