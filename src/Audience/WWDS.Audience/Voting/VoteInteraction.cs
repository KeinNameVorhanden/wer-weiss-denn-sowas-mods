using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using WWDS.Audience.Engine;
using WWDS.ModCore.Events;

namespace WWDS.Audience.Voting;

public enum TieBreak
{
    /// <summary>Uniform random among the tied options.</summary>
    Random,

    /// <summary>The tied option listed first.</summary>
    First,

    /// <summary>Apply the configured fallback option.</summary>
    Fallback,

    /// <summary>Wait for a moderator/broadcaster to force one (falls back to Random on timeout).</summary>
    Moderator,

    /// <summary>Reopen a short run-off vote between the tied options (once).</summary>
    Revote,
}

public sealed class VoteOption
{
    public VoteOption(string key, string label)
    {
        Key = key;
        Label = label;
    }

    public string Key { get; }

    public string Label { get; }
}

/// <summary>
/// Multiple-choice audience vote. One vote per viewer; changing it is optional. Votes can be weighted
/// (e.g. subscribers count double). Accepts the option key ("a", "B") or its 1-based number.
/// </summary>
public sealed class VoteInteraction : Interaction
{
    private readonly Dictionary<string, Ballot> _ballots = new();
    private readonly List<int> _eligible;
    private bool _revoted;
    private bool _awaitingModerator;

    public VoteInteraction(InteractionOptions options, IList<VoteOption> choices, TieBreak tieBreak = TieBreak.Random, int fallbackIndex = -1, Func<Viewer, double>? weight = null)
        : base("vote", options)
    {
        if (choices == null || choices.Count < 2) throw new ArgumentException("a vote needs at least two options");
        Choices = choices;
        TieBreakMode = tieBreak;
        FallbackIndex = fallbackIndex;
        Weight = weight ?? (_ => 1.0);
        _eligible = Enumerable.Range(0, choices.Count).ToList();
    }

    public IList<VoteOption> Choices { get; }

    public TieBreak TieBreakMode { get; }

    public int FallbackIndex { get; }

    public Func<Viewer, double> Weight { get; }

    public override int Participants => _ballots.Count;

    public override bool AwaitingDecision => _awaitingModerator;

    /// <summary>Options still in play (all, or the tied ones during a run-off).</summary>
    public IList<int> Eligible => _eligible.AsReadOnly();

    /// <summary>Every counted vote (viewer and option index).</summary>
    public IEnumerable<KeyValuePair<Viewer, int>> Ballots => _ballots.Values.Select(b => new KeyValuePair<Viewer, int>(b.Viewer, b.Index));

    public int VotesFor(int index) => _ballots.Values.Count(b => b.Index == index);

    public double WeightFor(int index) => _ballots.Values.Where(b => b.Index == index).Sum(b => b.Weight);

    internal override SubmissionResult Accept(Viewer viewer, string text, double now)
    {
        var index = ParseChoice(text);
        if (index < 0 || !_eligible.Contains(index)) return new SubmissionResult(SubmissionStatus.Rejected, "unknown option");
        if (_ballots.TryGetValue(viewer.Id, out var existing))
        {
            if (existing.Index == index) return new SubmissionResult(SubmissionStatus.Duplicate);
            if (!Options.AllowChanges) return new SubmissionResult(SubmissionStatus.Rejected, "vote already counted");
            existing.Index = index;
            return new SubmissionResult(SubmissionStatus.Changed);
        }

        _ballots[viewer.Id] = new Ballot(viewer, index, Math.Max(0, Weight(viewer)));
        return new SubmissionResult(SubmissionStatus.Accepted);
    }

    public int ParseChoice(string text)
    {
        var t = (text ?? string.Empty).Trim();
        for (var i = 0; i < Choices.Count; i++)
        {
            if (string.Equals(Choices[i].Key, t, StringComparison.OrdinalIgnoreCase)) return i;
        }

        return int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= Choices.Count ? n - 1 : -1;
    }

    internal override InteractionResult? Evaluate(Random random)
    {
        if (_awaitingModerator) return null;
        if (Participants < Options.MinParticipants)
        {
            // Retries come first; the fallback only applies once they are used up.
            if (Retries < Options.MaxRetries) return new InteractionResult(false, ResultReason.NoQuorum, "quorum missed");
            return FallbackOrNothing(ResultReason.NoQuorum, "only " + Participants + " of " + Options.MinParticipants + " required votes");
        }

        var best = _eligible.Max(WeightFor);
        var leaders = _eligible.Where(i => Math.Abs(WeightFor(i) - best) < 1e-9).ToList();
        if (leaders.Count == 1) return Winner(leaders[0], ResultReason.Decided);

        switch (TieBreakMode)
        {
            case TieBreak.First:
                return Winner(leaders[0], ResultReason.Tie);
            case TieBreak.Fallback when FallbackIndex >= 0:
                return Winner(FallbackIndex, ResultReason.Fallback);
            case TieBreak.Moderator:
                _awaitingModerator = true;
                return null;
            case TieBreak.Revote when !_revoted:
                _revoted = true;
                _eligible.Clear();
                _eligible.AddRange(leaders);
                foreach (var key in _ballots.Where(kv => !leaders.Contains(kv.Value.Index)).Select(kv => kv.Key).ToList()) _ballots.Remove(key);
                return new InteractionResult(false, ResultReason.Tie, "run-off between " + string.Join(", ", leaders.Select(i => Choices[i].Key).ToArray())) { Reopen = true };
            default:
                return Winner(leaders[random.Next(leaders.Count)], ResultReason.Tie);
        }
    }

    internal override InteractionResult EvaluateOnTimeout(Random random)
    {
        _awaitingModerator = false;
        var best = _eligible.Max(WeightFor);
        var leaders = _eligible.Where(i => Math.Abs(WeightFor(i) - best) < 1e-9).ToList();
        return Winner(leaders[random.Next(leaders.Count)], ResultReason.Timeout);
    }

    internal override InteractionResult? Force(string choice, Viewer by)
    {
        var index = ParseChoice(choice);
        if (index < 0) return null;
        _awaitingModerator = false;
        var r = Winner(index, ResultReason.Override);
        r.DecidedBy = by.DisplayName;
        return r;
    }

    internal override void Clear()
    {
        _ballots.Clear();
        _awaitingModerator = false;
    }

    public override IList<string> TallyLines()
    {
        var total = _eligible.Sum(WeightFor);
        var lines = new List<string>();
        foreach (var i in _eligible)
        {
            var w = WeightFor(i);
            var pct = total > 0 ? (int)Math.Round(100 * w / total) : 0;
            lines.Add(Choices[i].Key + "  " + pct.ToString(CultureInfo.InvariantCulture) + "% (" + VotesFor(i) + ")  " + Choices[i].Label);
        }

        return lines;
    }

    /// <summary>Percent per option (eligible only), for overlays.</summary>
    public IDictionary<int, double> Percentages()
    {
        var total = _eligible.Sum(WeightFor);
        return _eligible.ToDictionary(i => i, i => total > 0 ? 100 * WeightFor(i) / total : 0);
    }

    private InteractionResult Winner(int index, ResultReason reason) => new(true, reason, Choices[index].Key + " (" + Choices[index].Label + ") with " + VotesFor(index) + " of " + Participants + " votes")
    {
        WinningIndex = index,
        WinningText = Choices[index].Key,
        Lines = TallyLines(),
    };

    private InteractionResult FallbackOrNothing(ResultReason reason, string why) => FallbackIndex >= 0
        ? new InteractionResult(true, ResultReason.Fallback, why + ", fallback " + Choices[FallbackIndex].Key) { WinningIndex = FallbackIndex, WinningText = Choices[FallbackIndex].Key, Lines = TallyLines() }
        : new InteractionResult(false, reason, why) { Lines = TallyLines() };

    private sealed class Ballot
    {
        public Ballot(Viewer viewer, int index, double weight)
        {
            Viewer = viewer;
            Index = index;
            Weight = weight;
        }

        public Viewer Viewer { get; }

        public int Index { get; set; }

        public double Weight { get; }
    }
}
