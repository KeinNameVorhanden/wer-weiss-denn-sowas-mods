using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using WWDS.Audience.Engine;
using WWDS.Audience.Guessing;
using WWDS.ModCore.Events;

namespace WWDS.Audience.Voting;

public enum NumberAggregation
{
    /// <summary>Arithmetic mean of all entries.</summary>
    Average,

    /// <summary>Middle value: robust against trolls entering huge numbers.</summary>
    Median,

    /// <summary>Mean after dropping the lowest and highest 10 % (at least one each with 5+ entries).</summary>
    TrimmedAverage,
}

/// <summary>
/// Chat enters numbers ("!vote 1500"); the result is the configured aggregate (average by default,
/// median or trimmed average). Used for the bonus estimate of an audience-controlled contestant.
/// </summary>
public sealed class NumberVoteInteraction : Interaction
{
    private readonly Dictionary<string, double> _values = new();

    public NumberVoteInteraction(InteractionOptions options, double min, double max, NumberAggregation aggregation = NumberAggregation.Average)
        : base("vote", options)
    {
        Min = min;
        Max = max;
        Aggregation = aggregation;
    }

    public double Min { get; }

    public double Max { get; }

    public NumberAggregation Aggregation { get; }

    public override int Participants => _values.Count;

    internal override SubmissionResult Accept(Viewer viewer, string text, double now)
    {
        if (!GuessInteraction.TryParseNumber(text, out var v)) return new SubmissionResult(SubmissionStatus.Rejected, "not a number");
        v = Math.Max(Min, Math.Min(Max, v));
        if (_values.ContainsKey(viewer.Id))
        {
            if (!Options.AllowChanges) return new SubmissionResult(SubmissionStatus.Rejected, "already counted");
            _values[viewer.Id] = v;
            return new SubmissionResult(SubmissionStatus.Changed);
        }

        _values[viewer.Id] = v;
        return new SubmissionResult(SubmissionStatus.Accepted);
    }

    public double? Median() => Aggregate(_values.Values, NumberAggregation.Median);

    public double? Aggregate() => Aggregate(_values.Values, Aggregation);

    /// <summary>Aggregates numbers; null for no entries.</summary>
    public static double? Aggregate(IEnumerable<double> values, NumberAggregation aggregation)
    {
        var sorted = values.OrderBy(x => x).ToList();
        if (sorted.Count == 0) return null;
        switch (aggregation)
        {
            case NumberAggregation.Median:
                var mid = sorted.Count / 2;
                return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
            case NumberAggregation.TrimmedAverage:
                var cut = sorted.Count >= 5 ? Math.Max(1, (int)(sorted.Count * 0.1)) : 0;
                return sorted.Skip(cut).Take(sorted.Count - 2 * cut).Average();
            default:
                return sorted.Average();
        }
    }

    private string Name => Aggregation switch
    {
        NumberAggregation.Median => "median",
        NumberAggregation.TrimmedAverage => "trimmed average",
        _ => "average",
    };

    internal override InteractionResult? Evaluate(Random random)
    {
        var value = Aggregate();
        if (value == null || Participants < Options.MinParticipants) return new InteractionResult(false, ResultReason.NoQuorum, "only " + Participants + " numbers");
        return new InteractionResult(true, ResultReason.Decided, Name + " " + value.Value.ToString("0.##", CultureInfo.InvariantCulture) + " of " + Participants + " entries")
        {
            Number = Math.Round(value.Value),
            Lines = TallyLines(),
        };
    }

    internal override InteractionResult? Force(string choice, Viewer by)
    {
        if (!GuessInteraction.TryParseNumber(choice, out var v)) return null;
        return new InteractionResult(true, ResultReason.Override, "set to " + v.ToString("0.##", CultureInfo.InvariantCulture)) { Number = v, DecidedBy = by.DisplayName };
    }

    internal override void Clear() => _values.Clear();

    public override IList<string> TallyLines()
    {
        var r = Aggregate();
        return new List<string> { Participants + " entries", Name + " " + (r.HasValue ? r.Value.ToString("0.##", CultureInfo.InvariantCulture) : "-") };
    }
}
