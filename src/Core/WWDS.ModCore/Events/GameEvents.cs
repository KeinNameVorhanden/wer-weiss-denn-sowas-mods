using System.Collections.Generic;
using WWDS.ModCore.Adapters;
using WWDS.ModCore.Model;

namespace WWDS.ModCore.Events;

/// <summary>Base of every event published by a game adapter.</summary>
public abstract class GameEvent
{
    public override string ToString() => GetType().Name;
}

/// <summary>Raw game state machine transition (diagnostics; features should prefer the typed events).</summary>
public sealed class GameStateChanged : GameEvent
{
    public GameStateChanged(string state, GamePhase phase)
    {
        State = state;
        Phase = phase;
    }

    public string State { get; }

    public GamePhase Phase { get; }

    public override string ToString() => "GameStateChanged " + State + " (" + Phase + ")";
}

/// <summary>Main menu reached for the first time after boot.</summary>
public sealed class GameLoaded : GameEvent
{
}

/// <summary>Line-up of a show is final (new, resumed or programmatic session).</summary>
public sealed class SessionCreated : GameEvent
{
    public SessionCreated(int showId, IList<ContestantInfo> contestants)
    {
        ShowId = showId;
        Contestants = contestants;
    }

    public int ShowId { get; }

    public IList<ContestantInfo> Contestants { get; }
}

public sealed class GameStarted : GameEvent
{
    public GameStarted(int showId) => ShowId = showId;

    public int ShowId { get; }
}

/// <summary>A team's turn starts (one per normal question) or a final phase starts.</summary>
public sealed class RoundStarted : GameEvent
{
    public RoundStarted(QuestionKind kind, TeamSlot? team, ContestantInfo? contestant)
    {
        Kind = kind;
        Team = team;
        Contestant = contestant;
    }

    public QuestionKind Kind { get; }

    public TeamSlot? Team { get; }

    public ContestantInfo? Contestant { get; }
}

public sealed class QuestionStarted : GameEvent
{
    public QuestionStarted(QuestionInfo question, TeamSlot? team)
    {
        Question = question;
        Team = team;
    }

    public QuestionInfo Question { get; }

    public TeamSlot? Team { get; }

    public override string ToString() => "QuestionStarted " + Question.Id + " (" + Question.Kind + ")";
}

/// <summary>All options were read out and input is open.</summary>
public sealed class AnswersPresented : GameEvent
{
    public AnswersPresented(QuestionInfo question, ContestantInfo? answering)
    {
        Question = question;
        Answering = answering;
    }

    public QuestionInfo Question { get; }

    /// <summary>Contestant whose turn it is (null for the master question: both teams answer).</summary>
    public ContestantInfo? Answering { get; }
}

public sealed class AnswerLocked : GameEvent
{
    public AnswerLocked(TeamSlot team, int optionIndex, bool correct, bool byAudienceJoker, QuestionKind kind)
    {
        Team = team;
        OptionIndex = optionIndex;
        Correct = correct;
        ByAudienceJoker = byAudienceJoker;
        Kind = kind;
    }

    public TeamSlot Team { get; }

    public int OptionIndex { get; }

    public bool Correct { get; }

    public bool ByAudienceJoker { get; }

    public QuestionKind Kind { get; }

    public override string ToString() => "AnswerLocked team " + Team + " -> " + AnswerOption.KeyOf(OptionIndex) + (Correct ? " (correct)" : " (wrong)");
}

public sealed class BetLocked : GameEvent
{
    public BetLocked(TeamSlot team, int bet)
    {
        Team = team;
        Bet = bet;
    }

    public TeamSlot Team { get; }

    public int Bet { get; }
}

public sealed class AnswerRevealed : GameEvent
{
    public AnswerRevealed(QuestionInfo? question, int correctIndex, double? numericAnswer)
    {
        Question = question;
        CorrectIndex = correctIndex;
        NumericAnswer = numericAnswer;
    }

    public QuestionInfo? Question { get; }

    public int CorrectIndex { get; }

    public double? NumericAnswer { get; }
}

public sealed class QuestionFinished : GameEvent
{
    public QuestionFinished(QuestionInfo? question, TeamSlot? team, bool correct)
    {
        Question = question;
        Team = team;
        Correct = correct;
    }

    public QuestionInfo? Question { get; }

    public TeamSlot? Team { get; }

    public bool Correct { get; }
}

public sealed class ScoreChanged : GameEvent
{
    public ScoreChanged(TeamSlot team, int oldScore, int newScore, string reason)
    {
        Team = team;
        OldScore = oldScore;
        NewScore = newScore;
        Reason = reason;
    }

    public TeamSlot Team { get; }

    public int OldScore { get; }

    public int NewScore { get; }

    public string Reason { get; }

    public override string ToString() => "ScoreChanged team " + Team + " " + OldScore + " -> " + NewScore + " (" + Reason + ")";
}

public sealed class PlayerNameChanged : GameEvent
{
    public PlayerNameChanged(TeamSlot team, string oldName, string newName)
    {
        Team = team;
        OldName = oldName;
        NewName = newName;
    }

    public TeamSlot Team { get; }

    public string OldName { get; }

    public string NewName { get; }
}

/// <summary>End of a team turn (normal or master question), after scoring.</summary>
public sealed class RoundFinished : GameEvent
{
    public RoundFinished(int questionsPlayed) => QuestionsPlayed = questionsPlayed;

    public int QuestionsPlayed { get; }
}

public sealed class GameFinished : GameEvent
{
    public GameFinished(TeamSlot? winner, int scoreA, int scoreB, bool aborted)
    {
        Winner = winner;
        ScoreA = scoreA;
        ScoreB = scoreB;
        Aborted = aborted;
    }

    public TeamSlot? Winner { get; }

    public int ScoreA { get; }

    public int ScoreB { get; }

    public bool Aborted { get; }
}

/// <summary>An externally controlled contestant must decide. Resolve <see cref="Request"/> to answer.</summary>
public sealed class DecisionRequested : GameEvent
{
    public DecisionRequested(DecisionRequest request) => Request = request;

    public DecisionRequest Request { get; }

    public override string ToString() => "DecisionRequested " + Request;
}

public sealed class DecisionCompleted : GameEvent
{
    public DecisionCompleted(DecisionRequest request) => Request = request;

    public DecisionRequest Request { get; }

    public override string ToString() => "DecisionCompleted " + Request;
}
