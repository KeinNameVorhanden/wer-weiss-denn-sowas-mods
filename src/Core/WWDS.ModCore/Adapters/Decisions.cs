using System;
using System.Collections.Generic;
using WWDS.ModCore.Model;

namespace WWDS.ModCore.Adapters;

public enum DecisionKind
{
    /// <summary>Pick one of the three options of a normal question.</summary>
    Answer,

    /// <summary>Pick one of the three options of the master question (clock running).</summary>
    MasterAnswer,

    /// <summary>Pick one of the bet multipliers before the master question.</summary>
    Bet,

    /// <summary>Enter a number for the bonus estimate.</summary>
    Estimate,

    /// <summary>Pick one of the remaining category tiles.</summary>
    Category,
}

public enum DecisionStatus
{
    Pending,
    Resolved,
    TimedOut,
    Cancelled,
}

public sealed class DecisionOption
{
    public DecisionOption(int index, string key, string label)
    {
        Index = index;
        Key = key;
        Label = label;
    }

    public int Index { get; }

    public string Key { get; }

    public string Label { get; }
}

/// <summary>
/// A decision the game needs from an externally controlled contestant. The adapter publishes it,
/// a controller (audience vote, opponent behavior) resolves it, and the adapter's waiting
/// coroutine applies the outcome. If nobody resolves it before <see cref="Deadline"/>, the adapter
/// applies <see cref="FallbackIndex"/> (or the game's own bot logic) and marks it timed out.
/// Resolve/Cancel are main-thread only.
/// </summary>
public sealed class DecisionRequest
{
    private static long _nextId;

    public DecisionRequest(DecisionKind kind, TeamSlot team, ContestantInfo? contestant, QuestionInfo? question, IList<DecisionOption> options, double createdAt, double? deadline)
    {
        Id = ++_nextId;
        Kind = kind;
        Team = team;
        Contestant = contestant;
        Question = question;
        Options = options ?? new List<DecisionOption>();
        CreatedAt = createdAt;
        Deadline = deadline;
    }

    public long Id { get; }

    public DecisionKind Kind { get; }

    public TeamSlot Team { get; }

    public ContestantInfo? Contestant { get; }

    public QuestionInfo? Question { get; }

    /// <summary>Discrete options. Empty for <see cref="DecisionKind.Estimate"/>.</summary>
    public IList<DecisionOption> Options { get; }

    /// <summary>Monotonic seconds (see <see cref="Time.IClock"/>).</summary>
    public double CreatedAt { get; }

    /// <summary>Absolute monotonic deadline, or null if the game imposes none.</summary>
    public double? Deadline { get; }

    /// <summary>Applied by the adapter if the request times out (-1 = let the game's bot decide).</summary>
    public int FallbackIndex { get; set; } = -1;

    public double? EstimateMin { get; set; }

    public double? EstimateMax { get; set; }

    public DecisionStatus Status { get; private set; } = DecisionStatus.Pending;

    public int ChosenIndex { get; private set; } = -1;

    public double? ChosenNumber { get; private set; }

    public string ResolvedBy { get; private set; } = string.Empty;

    public bool IsPending => Status == DecisionStatus.Pending;

    public event Action<DecisionRequest>? Completed;

    public bool TryResolve(int index, string resolvedBy)
    {
        if (!IsPending || index < 0 || index >= Options.Count) return false;
        ChosenIndex = index;
        return Finish(DecisionStatus.Resolved, resolvedBy);
    }

    public bool TryResolveNumber(double value, string resolvedBy)
    {
        if (!IsPending || Kind != DecisionKind.Estimate || double.IsNaN(value) || double.IsInfinity(value)) return false;
        if (EstimateMin.HasValue && value < EstimateMin.Value) value = EstimateMin.Value;
        if (EstimateMax.HasValue && value > EstimateMax.Value) value = EstimateMax.Value;
        ChosenNumber = value;
        return Finish(DecisionStatus.Resolved, resolvedBy);
    }

    /// <summary>Called by the adapter when the deadline passed.</summary>
    public bool MarkTimedOut()
    {
        if (!IsPending) return false;
        ChosenIndex = FallbackIndex;
        return Finish(DecisionStatus.TimedOut, "timeout");
    }

    public bool Cancel(string reason)
    {
        if (!IsPending) return false;
        return Finish(DecisionStatus.Cancelled, reason);
    }

    private bool Finish(DecisionStatus status, string by)
    {
        Status = status;
        ResolvedBy = by ?? string.Empty;
        Completed?.Invoke(this);
        return true;
    }

    public override string ToString() => "Decision#" + Id + " " + Kind + " team " + Team + " " + Status + (ChosenIndex >= 0 ? " -> " + ChosenIndex : string.Empty);
}
