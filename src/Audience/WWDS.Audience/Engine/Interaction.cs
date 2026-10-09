using System;
using System.Collections.Generic;
using WWDS.ModCore.Commands;
using WWDS.ModCore.Events;

namespace WWDS.Audience.Engine;

/// <summary>
/// Lifecycle: Collecting (accepting input) -> Locked (closed) -> Evaluating (may wait for a moderator
/// decision or an external result) -> Applying -> Complete. Cancelled/Failed are terminal.
/// </summary>
public enum InteractionState
{
    Collecting,
    Locked,
    Evaluating,
    Applying,
    Complete,
    Cancelled,
    Failed,
}

public enum SubmissionStatus
{
    Accepted,
    Changed,
    Duplicate,
    Rejected,
    NotAllowed,
    Closed,
    RateLimited,
}

public sealed class SubmissionResult
{
    public SubmissionResult(SubmissionStatus status, string detail = "")
    {
        Status = status;
        Detail = detail;
    }

    public SubmissionStatus Status { get; }

    public string Detail { get; }

    public bool Counted => Status == SubmissionStatus.Accepted || Status == SubmissionStatus.Changed || Status == SubmissionStatus.Duplicate;
}

public enum ResultReason
{
    Decided,
    NoQuorum,
    Tie,
    Override,
    Fallback,
    Timeout,
}

public sealed class InteractionResult
{
    public InteractionResult(bool success, ResultReason reason, string summary)
    {
        Success = success;
        Reason = reason;
        Summary = summary;
    }

    public bool Success { get; }

    public ResultReason Reason { get; }

    public string Summary { get; }

    /// <summary>Index of the winning option (votes, picked name entry), -1 if none.</summary>
    public int WinningIndex { get; set; } = -1;

    public string? WinningText { get; set; }

    public double? Number { get; set; }

    /// <summary>Not final: reopen collecting (run-off vote). The engine handles it.</summary>
    public bool Reopen { get; set; }

    /// <summary>Who decided when <see cref="Reason"/> is Override.</summary>
    public string DecidedBy { get; set; } = string.Empty;

    /// <summary>Final tally lines for chat/overlay.</summary>
    public IList<string> Lines { get; set; } = new List<string>();

    /// <summary>Viewers credited by the result (guess winners, name author).</summary>
    public IList<Viewer> Winners { get; set; } = new List<Viewer>();

    public override string ToString() => (Success ? "OK " : "NO RESULT ") + Reason + ": " + Summary;
}

public sealed class InteractionOptions
{
    public string Title { get; set; } = string.Empty;

    /// <summary>Seconds the interaction collects input.</summary>
    public double Duration { get; set; } = 30;

    /// <summary>Minimum participants for a valid result (quorum).</summary>
    public int MinParticipants { get; set; } = 1;

    /// <summary>Allow a viewer to change their submission while collecting.</summary>
    public bool AllowChanges { get; set; } = true;

    /// <summary>Lock as soon as this many participants submitted (0 = off).</summary>
    public int EarlyCompleteAt { get; set; }

    /// <summary>Reopen this many times when the quorum is missed.</summary>
    public int MaxRetries { get; set; }

    /// <summary>Seconds to wait for a moderator decision / external result after locking.</summary>
    public double DecisionTimeout { get; set; } = 20;

    public Authority MinAuthority { get; set; } = Authority.Viewer;

    /// <summary>When set, only these user ids may participate (selected viewer mode).</summary>
    public ICollection<string>? AllowedUserIds { get; set; }
}

/// <summary>Base of every audience interaction. Not thread-safe: main thread only.</summary>
public abstract class Interaction
{
    private static long _nextId;

    protected Interaction(string kind, InteractionOptions options)
    {
        Id = ++_nextId;
        Kind = kind;
        Options = options;
    }

    public long Id { get; }

    /// <summary>"vote", "name", "guess", ...</summary>
    public string Kind { get; }

    public InteractionOptions Options { get; }

    public string Title => Options.Title;

    /// <summary>What the interaction is about (question id, "winner", "opponent") for statistics and features.</summary>
    public string Subject { get; set; } = string.Empty;

    public InteractionState State { get; internal set; } = InteractionState.Collecting;

    public double OpenedAt { get; internal set; }

    public double LockAt { get; internal set; }

    public double LockedAt { get; internal set; }

    public int Retries { get; internal set; }

    public InteractionResult? Result { get; internal set; }

    /// <summary>Called once with the final result when the interaction applies (game action, chat).</summary>
    public Action<InteractionResult>? OnApply { get; set; }

    public abstract int Participants { get; }

    public bool IsActive => State == InteractionState.Collecting || State == InteractionState.Locked || State == InteractionState.Evaluating;

    /// <summary>True while a moderator decision or external result is pending after locking.</summary>
    public virtual bool AwaitingDecision => false;

    internal abstract SubmissionResult Accept(Viewer viewer, string text, double now);

    /// <summary>Computes the result after locking. Return null to keep waiting (see <see cref="AwaitingDecision"/>).</summary>
    internal abstract InteractionResult? Evaluate(Random random);

    /// <summary>Result when the decision timeout expires while awaiting a decision.</summary>
    internal virtual InteractionResult EvaluateOnTimeout(Random random) => new(false, ResultReason.Timeout, "no decision in time");

    /// <summary>Moderator/broadcaster override ("force"): parse the choice text into a result.</summary>
    internal abstract InteractionResult? Force(string choice, Viewer by);

    /// <summary>Removes all submissions (moderator "clear").</summary>
    internal abstract void Clear();

    public abstract IList<string> TallyLines();
}
