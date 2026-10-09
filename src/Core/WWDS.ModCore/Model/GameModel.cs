using System;
using System.Collections.Generic;

namespace WWDS.ModCore.Model;

public enum GameId
{
    Unknown,
    Wwds1,
    Wwds2,
}

/// <summary>The two fixed teams of the show. A = "Team Bernhard" (Hoecker), B = "Team Elton".</summary>
public enum TeamSlot
{
    A,
    B,
}

public enum ContestantKind
{
    Human,
    Bot,
}

public enum QuestionKind
{
    /// <summary>Category question with three options, +500 when correct.</summary>
    Normal,

    /// <summary>Final bet question, both teams answer the same three options within a clock.</summary>
    Master,

    /// <summary>Tie-break estimate, numeric answer, closest team wins.</summary>
    Bonus,
}

/// <summary>Coarse, game-independent progress of the game.</summary>
public enum GamePhase
{
    Unknown,
    Boot,
    MainMenu,
    PlayerSelection,
    Loading,
    ShowIntro,
    Tutorial,
    CategorySelection,
    QuestionPresented,
    WaitingForAnswer,
    Reveal,
    MasterBet,
    BonusEstimate,
    Outro,
}

public sealed class AnswerOption
{
    public AnswerOption(int index, string text, bool isCorrect)
    {
        Index = index;
        Text = text ?? string.Empty;
        IsCorrect = isCorrect;
    }

    public int Index { get; }

    /// <summary>"A", "B", "C".</summary>
    public string Key => KeyOf(Index);

    public string Text { get; }

    public bool IsCorrect { get; }

    public static string KeyOf(int index) => ((char)('A' + index)).ToString();

    /// <summary>Parses "a", "B", "1".."3" into a 0-based index.</summary>
    public static bool TryParseKey(string? text, int optionCount, out int index)
    {
        index = -1;
        if (text == null) return false;
        var t = text.Trim();
        if (t.Length != 1) return false;
        var c = char.ToUpperInvariant(t[0]);
        if (c >= 'A' && c < 'A' + optionCount) index = c - 'A';
        else if (c >= '1' && c < '1' + optionCount) index = c - '1';
        return index >= 0;
    }
}

/// <summary>Normalized question as exposed by a game adapter.</summary>
public sealed class QuestionInfo
{
    public QuestionInfo(GameId game, int showId, int index, QuestionKind kind, string category, string text, IList<AnswerOption> answers, double? numericAnswer)
    {
        Game = game;
        ShowId = showId;
        Index = index;
        Kind = kind;
        Category = category ?? string.Empty;
        Text = text ?? string.Empty;
        Answers = answers ?? new List<AnswerOption>();
        NumericAnswer = numericAnswer;
        Metadata = new Dictionary<string, string>();
    }

    /// <summary>Stable id "wwds1:show7:q3".</summary>
    public string Id => (Game == GameId.Wwds2 ? "wwds2" : "wwds1") + ":show" + ShowId + ":q" + Index;

    public GameId Game { get; }

    public int ShowId { get; }

    public int Index { get; }

    public QuestionKind Kind { get; }

    public string Category { get; }

    public string Text { get; }

    public IList<AnswerOption> Answers { get; }

    /// <summary>Target value of a bonus estimate question.</summary>
    public double? NumericAnswer { get; }

    public IDictionary<string, string> Metadata { get; }

    public int CorrectIndex
    {
        get
        {
            for (var i = 0; i < Answers.Count; i++)
            {
                if (Answers[i].IsCorrect) return i;
            }

            return -1;
        }
    }
}

public sealed class ContestantInfo
{
    public ContestantInfo(string id, string name, TeamSlot team, ContestantKind kind, bool externallyControlled)
    {
        Id = id;
        Name = name ?? string.Empty;
        Team = team;
        Kind = kind;
        ExternallyControlled = externallyControlled;
    }

    public string Id { get; }

    public string Name { get; }

    public TeamSlot Team { get; }

    public ContestantKind Kind { get; }

    /// <summary>True when the harness (audience, opponent behavior) makes this contestant's decisions.</summary>
    public bool ExternallyControlled { get; }
}

public sealed class TeamInfo
{
    public TeamInfo(TeamSlot slot, string displayName, int score, IList<ContestantInfo> members)
    {
        Slot = slot;
        DisplayName = displayName;
        Score = score;
        Members = members ?? new List<ContestantInfo>();
    }

    public TeamSlot Slot { get; }

    public string DisplayName { get; }

    public int Score { get; }

    public IList<ContestantInfo> Members { get; }
}

/// <summary>Point-in-time view of the game for UI, bridge and features. Cheap to build.</summary>
public sealed class GameSnapshot
{
    public static readonly GameSnapshot Empty = new(GamePhase.Unknown, null, null, null, null, null, null, false);

    public GameSnapshot(GamePhase phase, string? rawState, QuestionInfo? question, TeamInfo? teamA, TeamInfo? teamB, TeamSlot? activeTeam, ContestantInfo? activeContestant, bool inShow)
    {
        Phase = phase;
        RawState = rawState;
        Question = question;
        TeamA = teamA;
        TeamB = teamB;
        ActiveTeam = activeTeam;
        ActiveContestant = activeContestant;
        InShow = inShow;
    }

    public GamePhase Phase { get; }

    /// <summary>Game-specific state name (for diagnostics only).</summary>
    public string? RawState { get; }

    public QuestionInfo? Question { get; }

    public TeamInfo? TeamA { get; }

    public TeamInfo? TeamB { get; }

    public TeamSlot? ActiveTeam { get; }

    public ContestantInfo? ActiveContestant { get; }

    public bool InShow { get; }

    public TeamInfo? Team(TeamSlot slot) => slot == TeamSlot.A ? TeamA : TeamB;
}

/// <summary>Result of an adapter action. Never throws for expected refusals.</summary>
public sealed class ActionResult
{
    private ActionResult(bool ok, string message)
    {
        Ok = ok;
        Message = message;
    }

    public bool Ok { get; }

    public string Message { get; }

    public static ActionResult Success(string message = "ok") => new(true, message);

    public static ActionResult Fail(string message) => new(false, message);

    public override string ToString() => (Ok ? "OK: " : "FAILED: ") + Message;
}

public static class TeamSlotExtensions
{
    public static TeamSlot Other(this TeamSlot slot) => slot == TeamSlot.A ? TeamSlot.B : TeamSlot.A;

    public static bool TryParse(string? text, out TeamSlot slot)
    {
        slot = TeamSlot.A;
        if (text == null) return false;
        switch (text.Trim().ToUpperInvariant())
        {
            case "A":
            case "TEAMA":
            case "BERNHARD":
            case "HOECKER":
                slot = TeamSlot.A;
                return true;
            case "B":
            case "TEAMB":
            case "ELTON":
                slot = TeamSlot.B;
                return true;
            default:
                return false;
        }
    }
}

/// <summary>Fixed reference data of the show format (CONFIRMED by static analysis).</summary>
public static class ShowFormat
{
    public const int OptionCount = 3;
    public const int NormalQuestionsPerShow = 12;
    public const int MasterQuestionIndex = 12;
    public const int BonusQuestionIndex = 13;
    public const int PointsPerCorrectAnswer = 500;

    /// <summary>The master question clock is 20 s (+1 s lead-in); audience decisions must finish before.</summary>
    public const double MasterClockSeconds = 20.0;

    public static string DefaultTeamName(TeamSlot slot) => slot == TeamSlot.A ? "Team Bernhard" : "Team Elton";
}
