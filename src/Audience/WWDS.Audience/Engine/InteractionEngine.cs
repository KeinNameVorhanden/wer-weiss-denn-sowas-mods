using System;
using System.Collections.Generic;
using System.Linq;
using WWDS.Audience.Moderation;
using WWDS.ModCore.Commands;
using WWDS.ModCore.Events;
using WWDS.ModCore.Logging;
using WWDS.ModCore.Time;

namespace WWDS.Audience.Engine;

public abstract class InteractionEvent : AudienceEvent
{
    protected InteractionEvent(Interaction interaction) => Interaction = interaction;

    public Interaction Interaction { get; }

    public override string ToString() => GetType().Name + " #" + Interaction.Id + " " + Interaction.Kind + " '" + Interaction.Title + "' " + Interaction.State;
}

public sealed class InteractionOpened : InteractionEvent
{
    public InteractionOpened(Interaction i)
        : base(i)
    {
    }
}

public sealed class InteractionLocked : InteractionEvent
{
    public InteractionLocked(Interaction i)
        : base(i)
    {
    }
}

public sealed class InteractionCompleted : InteractionEvent
{
    public InteractionCompleted(Interaction i, InteractionResult result)
        : base(i) => Result = result;

    public InteractionResult Result { get; }

    public override string ToString() => base.ToString() + " -> " + Result;
}

public sealed class InteractionCancelled : InteractionEvent
{
    public InteractionCancelled(Interaction i, string reason)
        : base(i) => Reason = reason;

    public string Reason { get; }
}

/// <summary>
/// Runs audience interactions: routes submissions, enforces flood limits, authority and allow-lists,
/// times interactions out, evaluates, applies and publishes lifecycle events. Main thread only;
/// driven by <see cref="Tick"/>.
/// </summary>
public sealed class InteractionEngine
{
    private readonly List<Interaction> _active = new();
    private readonly Dictionary<string, double> _lastCompletedByKind = new();
    private readonly Action<object> _publish;
    private readonly IClock _clock;
    private readonly ModLog _log;
    private readonly Random _random;
    private double _lastStatus;

    public InteractionEngine(Action<object> publish, IClock clock, ModLog log, int? seed = null)
    {
        _publish = publish;
        _clock = clock;
        _log = log;
        _random = seed.HasValue ? new Random(seed.Value) : new Random();
    }

    public SpamGuard Spam { get; } = new(8, 10);

    /// <summary>Seconds a kind of interaction must rest after completing (0 = none).</summary>
    public Dictionary<string, double> Cooldowns { get; } = new();

    public IList<Interaction> Active => _active.AsReadOnly();

    public Interaction? Current(string kind) => _active.LastOrDefault(i => i.Kind == kind && i.IsActive);

    public Interaction? Find(long id) => _active.FirstOrDefault(i => i.Id == id);

    /// <summary>Starts collecting. Refused during the kind's cooldown unless <paramref name="ignoreCooldown"/>.</summary>
    public bool Open(Interaction interaction, bool ignoreCooldown = false)
    {
        var now = _clock.Now;
        if (!ignoreCooldown && Cooldowns.TryGetValue(interaction.Kind, out var cd) && cd > 0
            && _lastCompletedByKind.TryGetValue(interaction.Kind, out var last) && now - last < cd)
        {
            _log.Info("Interaction '" + interaction.Title + "' refused: " + interaction.Kind + " cooldown " + Math.Ceiling(cd - (now - last)) + " s left");
            return false;
        }

        interaction.State = InteractionState.Collecting;
        interaction.OpenedAt = now;
        interaction.LockAt = now + Math.Max(1, interaction.Options.Duration);
        _active.Add(interaction);
        _log.Info("Opened #" + interaction.Id + " " + interaction.Kind + " '" + interaction.Title + "' for " + interaction.Options.Duration + " s");
        _publish(new InteractionOpened(interaction));
        PublishStatus(force: true);
        return true;
    }

    /// <summary>Routes a viewer submission to the newest active interaction of <paramref name="kind"/>.</summary>
    public SubmissionResult Submit(string kind, Viewer viewer, string text)
    {
        var interaction = Current(kind);
        if (interaction == null || interaction.State != InteractionState.Collecting) return new SubmissionResult(SubmissionStatus.Closed, "nothing open");
        if (viewer.Authority < interaction.Options.MinAuthority) return new SubmissionResult(SubmissionStatus.NotAllowed, "authority");
        var allowed = interaction.Options.AllowedUserIds;
        if (allowed != null && !allowed.Contains(viewer.Id) && !allowed.Contains("login:" + viewer.Login.ToLowerInvariant())) return new SubmissionResult(SubmissionStatus.NotAllowed, "not a participant");
        var now = _clock.Now;
        if (viewer.Authority < Authority.Moderator && !Spam.Allow(viewer.Id, now)) return new SubmissionResult(SubmissionStatus.RateLimited);

        var result = interaction.Accept(viewer, text, now);
        if (result.Counted && interaction.Options.EarlyCompleteAt > 0 && interaction.Participants >= interaction.Options.EarlyCompleteAt)
        {
            Lock(interaction, "enough participants");
        }

        return result;
    }

    public void Tick()
    {
        var now = _clock.Now;
        foreach (var i in _active.ToArray())
        {
            try
            {
                Step(i, now);
            }
            catch (Exception ex)
            {
                _log.Error("Interaction #" + i.Id + " failed", ex);
                i.State = InteractionState.Failed;
            }
        }

        _active.RemoveAll(i => !i.IsActive && i.State != InteractionState.Applying);
        PublishStatus(force: false);
    }

    private void Step(Interaction i, double now)
    {
        switch (i.State)
        {
            case InteractionState.Collecting when now >= i.LockAt:
                Lock(i, "time up");
                break;
            case InteractionState.Locked:
                i.State = InteractionState.Evaluating;
                var result = i.Evaluate(_random);
                if (result != null) Finish(i, result);
                break;
            case InteractionState.Evaluating:
                if (!i.AwaitingDecision)
                {
                    var r = i.Evaluate(_random);
                    if (r != null) Finish(i, r);
                }
                else if (now - i.LockedAt >= i.Options.DecisionTimeout)
                {
                    Finish(i, i.EvaluateOnTimeout(_random));
                }

                break;
        }
    }

    private void Finish(Interaction i, InteractionResult result)
    {
        if (result.Reopen)
        {
            i.State = InteractionState.Collecting;
            i.LockAt = _clock.Now + Math.Max(10, i.Options.Duration / 2);
            _log.Info("#" + i.Id + " " + result.Summary);
            _publish(new InteractionOpened(i));
            return;
        }

        if (result.Reason == ResultReason.NoQuorum && i.Retries < i.Options.MaxRetries)
        {
            i.Retries++;
            i.Clear();
            i.State = InteractionState.Collecting;
            i.LockAt = _clock.Now + Math.Max(1, i.Options.Duration);
            _log.Info("#" + i.Id + " missed quorum (" + i.Participants + "/" + i.Options.MinParticipants + "), retry " + i.Retries + "/" + i.Options.MaxRetries);
            _publish(new InteractionOpened(i));
            return;
        }

        i.Result = result;
        i.State = InteractionState.Applying;
        try
        {
            i.OnApply?.Invoke(result);
            i.State = InteractionState.Complete;
        }
        catch (Exception ex)
        {
            _log.Error("Applying #" + i.Id + " failed", ex);
            i.State = InteractionState.Failed;
        }

        _lastCompletedByKind[i.Kind] = _clock.Now;
        _log.Info("#" + i.Id + " '" + i.Title + "' " + result);
        _publish(new InteractionCompleted(i, result));
        PublishStatus(force: true);
    }

    // ---- moderator / broadcaster controls ------------------------------------------------------

    public bool Lock(Interaction i, string why)
    {
        if (i.State != InteractionState.Collecting) return false;
        i.State = InteractionState.Locked;
        i.LockedAt = _clock.Now;
        _log.Info("Locked #" + i.Id + " (" + why + "), " + i.Participants + " participants");
        _publish(new InteractionLocked(i));
        return true;
    }

    public bool Cancel(Interaction i, string reason)
    {
        if (!i.IsActive) return false;
        i.State = InteractionState.Cancelled;
        _log.Info("Cancelled #" + i.Id + ": " + reason);
        _publish(new InteractionCancelled(i, reason));
        PublishStatus(force: true);
        return true;
    }

    public bool Extend(Interaction i, double seconds)
    {
        if (i.State != InteractionState.Collecting) return false;
        i.LockAt += seconds;
        return true;
    }

    /// <summary>Re-opens a locked or evaluating interaction for another round, keeping submissions.</summary>
    public bool Reopen(Interaction i, double seconds)
    {
        if (i.State != InteractionState.Locked && i.State != InteractionState.Evaluating) return false;
        i.State = InteractionState.Collecting;
        i.LockAt = _clock.Now + Math.Max(1, seconds);
        _publish(new InteractionOpened(i));
        return true;
    }

    public bool Clear(Interaction i)
    {
        if (!i.IsActive) return false;
        i.Clear();
        return true;
    }

    /// <summary>Moderator decides the result directly ("force B", "pick 3").</summary>
    public bool Force(Interaction i, string choice, Viewer by)
    {
        if (!i.IsActive) return false;
        var result = i.Force(choice, by);
        if (result == null) return false;
        if (i.State == InteractionState.Collecting) Lock(i, "override by " + by.DisplayName);
        i.State = InteractionState.Evaluating;
        Finish(i, result);
        return true;
    }

    private void PublishStatus(bool force)
    {
        var now = _clock.Now;
        if (!force && now - _lastStatus < 0.5) return;
        _lastStatus = now;
        // Only rounds that still take entries: a locked bet waiting for the show's result is not "running".
        var current = _active.LastOrDefault(i => i.State == InteractionState.Collecting);
        if (current == null)
        {
            _publish(InteractionStatusChanged.Idle);
            return;
        }

        var left = current.State == InteractionState.Collecting ? Math.Max(0, current.LockAt - now) : 0;
        _publish(new InteractionStatusChanged(current.Title, current.State.ToString(), current.TallyLines(), left));
    }
}
