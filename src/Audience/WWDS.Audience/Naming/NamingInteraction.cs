using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using WWDS.Audience.Engine;
using WWDS.Audience.Moderation;
using WWDS.ModCore.Events;

namespace WWDS.Audience.Naming;

public enum NameSelection
{
    /// <summary>Uniform random among eligible entries.</summary>
    Random,

    /// <summary>Entry submitted by the most viewers (ties random).</summary>
    MostSubmitted,

    /// <summary>Random, weighted by the number of supporters.</summary>
    Weighted,

    /// <summary>A moderator picks with "pick n"; random on timeout.</summary>
    Moderator,
}

public sealed class NameEntry
{
    internal NameEntry(int number, string text, string key, Viewer author)
    {
        Number = number;
        Text = text;
        Key = key;
        Author = author;
        Supporters.Add(author.Id);
    }

    /// <summary>1-based number used by moderator commands (approve/ban/pick).</summary>
    public int Number { get; }

    public string Text { get; }

    internal string Key { get; }

    public Viewer Author { get; }

    public HashSet<string> Supporters { get; } = new();

    public bool Approved { get; internal set; }

    public bool Banned { get; internal set; }
}

/// <summary>
/// Collects name suggestions from chat. Every text passes the <see cref="ContentModerator"/>; the same
/// name from several viewers becomes one entry with several supporters. Optional moderator approval.
/// </summary>
public sealed class NamingInteraction : Interaction
{
    private readonly ContentModerator _moderator;
    private readonly List<NameEntry> _entries = new();
    private readonly Dictionary<string, NameEntry> _byViewer = new();
    private bool _awaitingModerator;

    public NamingInteraction(InteractionOptions options, ContentModerator moderator, NameSelection selection, bool requireApproval, int maxEntries = 50)
        : base("name", options)
    {
        _moderator = moderator;
        Selection = selection;
        RequireApproval = requireApproval;
        MaxEntries = maxEntries;
    }

    /// <summary>What the name is for, e.g. "opponent", "teamB" (used by the applying feature).</summary>
    public string Target { get; set; } = string.Empty;

    public NameSelection Selection { get; }

    public bool RequireApproval { get; }

    public int MaxEntries { get; }

    public IList<NameEntry> Entries => _entries.AsReadOnly();

    public override int Participants => _byViewer.Count;

    public override bool AwaitingDecision => _awaitingModerator;

    private IEnumerable<NameEntry> Eligible => _entries.Where(e => !e.Banned && (!RequireApproval || e.Approved));

    internal override SubmissionResult Accept(Viewer viewer, string text, double now)
    {
        // "#2" or "2" supports an existing suggestion instead of creating a new one.
        var trimmed = (text ?? string.Empty).Trim().TrimStart('#');
        if (trimmed.Length > 0 && trimmed.Length <= 3 && int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            var target = _entries.FirstOrDefault(e => e.Number == number && !e.Banned);
            if (target == null) return new SubmissionResult(SubmissionStatus.Rejected, "no suggestion #" + number);
            if (_byViewer.TryGetValue(viewer.Id, out var current))
            {
                if (ReferenceEquals(current, target)) return new SubmissionResult(SubmissionStatus.Duplicate);
                if (!Options.AllowChanges) return new SubmissionResult(SubmissionStatus.Rejected, "one suggestion per viewer");
                current.Supporters.Remove(viewer.Id);
            }

            target.Supporters.Add(viewer.Id);
            _byViewer[viewer.Id] = target;
            return new SubmissionResult(SubmissionStatus.Accepted, "supports #" + number);
        }

        var check = _moderator.Check(text);
        if (!check.Accepted) return new SubmissionResult(SubmissionStatus.Rejected, check.Reason);
        var key = ContentModerator.DuplicateKey(check.Text);
        var status = SubmissionStatus.Accepted;

        if (_byViewer.TryGetValue(viewer.Id, out var previous))
        {
            if (previous.Key == key) return new SubmissionResult(SubmissionStatus.Duplicate);
            if (!Options.AllowChanges) return new SubmissionResult(SubmissionStatus.Rejected, "one suggestion per viewer");
            previous.Supporters.Remove(viewer.Id);
            status = SubmissionStatus.Changed;
        }

        var entry = _entries.FirstOrDefault(e => e.Key == key);
        if (entry != null)
        {
            if (entry.Banned) return new SubmissionResult(SubmissionStatus.Rejected, "name was removed by a moderator");
            entry.Supporters.Add(viewer.Id);
            _byViewer[viewer.Id] = entry;
            return new SubmissionResult(status == SubmissionStatus.Changed ? status : SubmissionStatus.Duplicate, "supports #" + entry.Number);
        }

        if (_entries.Count >= MaxEntries) return new SubmissionResult(SubmissionStatus.Rejected, "too many suggestions");
        entry = new NameEntry(_entries.Count + 1, check.Text, key, viewer);
        _entries.Add(entry);
        _byViewer[viewer.Id] = entry;
        return new SubmissionResult(status, "#" + entry.Number);
    }

    public NameEntry? FindEntry(string numberOrText)
    {
        var t = (numberOrText ?? string.Empty).Trim().TrimStart('#');
        if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return _entries.FirstOrDefault(e => e.Number == n);
        var key = ContentModerator.DuplicateKey(t);
        return _entries.FirstOrDefault(e => e.Key == key);
    }

    public bool Approve(string numberOrText)
    {
        var e = FindEntry(numberOrText);
        if (e == null || e.Banned) return false;
        e.Approved = true;
        return true;
    }

    public bool Ban(string numberOrText)
    {
        var e = FindEntry(numberOrText);
        if (e == null) return false;
        e.Banned = true;
        return true;
    }

    internal override InteractionResult? Evaluate(Random random)
    {
        if (_awaitingModerator) return null;
        var eligible = Eligible.ToList();
        if (Participants < Options.MinParticipants || eligible.Count == 0)
        {
            return new InteractionResult(false, ResultReason.NoQuorum, eligible.Count == 0 ? "no eligible name" : "only " + Participants + " suggestions") { Lines = TallyLines() };
        }

        switch (Selection)
        {
            case NameSelection.Moderator:
                _awaitingModerator = true;
                return null;
            case NameSelection.MostSubmitted:
                var max = eligible.Max(e => e.Supporters.Count);
                var top = eligible.Where(e => e.Supporters.Count == max).ToList();
                return Winner(top[random.Next(top.Count)], top.Count > 1 ? ResultReason.Tie : ResultReason.Decided);
            case NameSelection.Weighted:
                var total = eligible.Sum(e => e.Supporters.Count);
                var roll = random.Next(total);
                foreach (var e in eligible)
                {
                    roll -= e.Supporters.Count;
                    if (roll < 0) return Winner(e, ResultReason.Decided);
                }

                return Winner(eligible[eligible.Count - 1], ResultReason.Decided);
            default:
                return Winner(eligible[random.Next(eligible.Count)], ResultReason.Decided);
        }
    }

    internal override InteractionResult EvaluateOnTimeout(Random random)
    {
        _awaitingModerator = false;
        var eligible = Eligible.ToList();
        return eligible.Count == 0
            ? new InteractionResult(false, ResultReason.Timeout, "no eligible name")
            : Winner(eligible[random.Next(eligible.Count)], ResultReason.Timeout);
    }

    internal override InteractionResult? Force(string choice, Viewer by)
    {
        var e = FindEntry(choice);
        if (e == null || e.Banned) return null;
        _awaitingModerator = false;
        var r = Winner(e, ResultReason.Override);
        r.DecidedBy = by.DisplayName;
        return r;
    }

    internal override void Clear()
    {
        _entries.Clear();
        _byViewer.Clear();
        _awaitingModerator = false;
    }

    public override IList<string> TallyLines() => _entries
        .Where(e => !e.Banned)
        .OrderByDescending(e => e.Supporters.Count)
        .Take(8)
        .Select(e => "#" + e.Number + " " + e.Text + " (" + e.Supporters.Count + ")" + (RequireApproval && !e.Approved ? " [pending]" : string.Empty))
        .ToList();

    private InteractionResult Winner(NameEntry e, ResultReason reason) => new(true, reason, "'" + e.Text + "' by " + e.Author.DisplayName + " (" + e.Supporters.Count + " supporters)")
    {
        WinningIndex = e.Number - 1,
        WinningText = e.Text,
        Winners = new List<Viewer> { e.Author },
        Lines = TallyLines(),
    };
}
