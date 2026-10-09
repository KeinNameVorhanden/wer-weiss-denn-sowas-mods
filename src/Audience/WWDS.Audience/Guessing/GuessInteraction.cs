using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using WWDS.Audience.Engine;
using WWDS.ModCore.Events;

namespace WWDS.Audience.Guessing;

public enum GuessKind
{
    /// <summary>Guess one of fixed options ("A", "Elton", "Team A").</summary>
    Option,

    /// <summary>Guess a number ("25000"); closest wins.</summary>
    Number,
}

/// <summary>
/// "Guess the answer / the money / the winner". Collects one guess per viewer, locks, then waits for the
/// real outcome (<see cref="ProvideOutcome"/>, usually from a game event). Winners: exact option match,
/// or the closest numbers (ties share).
/// </summary>
public sealed class GuessInteraction : Interaction
{
    private readonly Dictionary<string, Guess> _guesses = new();
    private bool _hasOutcome;
    private int _outcomeOption = -1;
    private double _outcomeNumber;

    public GuessInteraction(InteractionOptions options, GuessKind kind, IList<string>? optionKeys = null, int maxWinners = 3)
        : base("guess", options)
    {
        GuessKind = kind;
        OptionKeys = optionKeys ?? new List<string>();
        MaxWinners = maxWinners;
        if (kind == GuessKind.Option && OptionKeys.Count < 2) throw new ArgumentException("option guesses need at least two options");
    }

    public GuessKind GuessKind { get; }

    public IList<string> OptionKeys { get; }

    /// <summary>Text per option key (e.g. who plays a team, an answer's text); shown next to the key.</summary>
    public IList<string> OptionLabels { get; set; } = new List<string>();

    public int MaxWinners { get; }

    public override int Participants => _guesses.Count;

    public override bool AwaitingDecision => !_hasOutcome;

    public IEnumerable<KeyValuePair<Viewer, string>> Guesses => _guesses.Values.Select(g => new KeyValuePair<Viewer, string>(g.Viewer, g.Text));

    internal override SubmissionResult Accept(Viewer viewer, string text, double now)
    {
        var t = (text ?? string.Empty).Trim();
        Guess guess;
        if (GuessKind == GuessKind.Number)
        {
            if (!TryParseNumber(t, out var number)) return new SubmissionResult(SubmissionStatus.Rejected, "not a number");
            guess = new Guess(viewer, number.ToString("0.##", CultureInfo.InvariantCulture), -1, number, now);
        }
        else
        {
            var index = IndexOf(t);
            if (index < 0) return new SubmissionResult(SubmissionStatus.Rejected, "unknown option");
            guess = new Guess(viewer, OptionKeys[index], index, 0, now);
        }

        if (_guesses.TryGetValue(viewer.Id, out var old))
        {
            if (old.Text == guess.Text) return new SubmissionResult(SubmissionStatus.Duplicate);
            if (!Options.AllowChanges) return new SubmissionResult(SubmissionStatus.Rejected, "guess already locked in");
            _guesses[viewer.Id] = guess;
            return new SubmissionResult(SubmissionStatus.Changed);
        }

        _guesses[viewer.Id] = guess;
        return new SubmissionResult(SubmissionStatus.Accepted);
    }

    /// <summary>Supplies the real outcome; the engine evaluates on its next tick.</summary>
    public bool ProvideOutcome(string optionOrNumber)
    {
        if (_hasOutcome) return false;
        if (GuessKind == GuessKind.Number)
        {
            if (!TryParseNumber(optionOrNumber, out _outcomeNumber)) return false;
        }
        else
        {
            _outcomeOption = IndexOf(optionOrNumber);
            if (_outcomeOption < 0) return false;
        }

        _hasOutcome = true;
        return true;
    }

    internal override InteractionResult? Evaluate(Random random)
    {
        if (!_hasOutcome) return null;
        if (Participants < Options.MinParticipants) return new InteractionResult(false, ResultReason.NoQuorum, "only " + Participants + " guesses");

        List<Guess> winners;
        string outcome;
        if (GuessKind == GuessKind.Number)
        {
            outcome = _outcomeNumber.ToString("0.##", CultureInfo.InvariantCulture);
            var ranked = _guesses.Values.OrderBy(g => Math.Abs(g.Number - _outcomeNumber)).ThenBy(g => g.At).ToList();
            var bestDistance = ranked.Count > 0 ? Math.Abs(ranked[0].Number - _outcomeNumber) : 0;
            winners = ranked.Where(g => Math.Abs(Math.Abs(g.Number - _outcomeNumber) - bestDistance) < 1e-9).Take(Math.Max(1, MaxWinners)).ToList();
        }
        else
        {
            outcome = OptionKeys[_outcomeOption];
            winners = _guesses.Values.Where(g => g.Option == _outcomeOption).OrderBy(g => g.At).ToList();
        }

        var summary = "answer " + outcome + ": " + (winners.Count == 0 ? "nobody was right" : string.Join(", ", winners.Take(5).Select(w => w.Viewer.DisplayName).ToArray()) + (winners.Count > 5 ? " and " + (winners.Count - 5) + " more" : string.Empty));
        return new InteractionResult(true, ResultReason.Decided, summary)
        {
            WinningText = outcome,
            Number = GuessKind == GuessKind.Number ? _outcomeNumber : null,
            WinningIndex = _outcomeOption,
            Winners = winners.Select(w => w.Viewer).ToList(),
            Lines = TallyLines(),
        };
    }

    internal override InteractionResult EvaluateOnTimeout(Random random) => new(false, ResultReason.Timeout, "outcome never arrived");

    internal override InteractionResult? Force(string choice, Viewer by)
    {
        if (!ProvideOutcome(choice)) return null;
        var r = Evaluate(new Random(0));
        if (r != null) r.DecidedBy = by.DisplayName;
        return r;
    }

    internal override void Clear() => _guesses.Clear();

    public override IList<string> TallyLines()
    {
        if (GuessKind == GuessKind.Option)
        {
            // Same shape as vote tallies ("A  50% (2)  Streamer (James)") so overlays show the option text.
            var total = _guesses.Count;
            return OptionKeys.Select((k, i) =>
            {
                var n = _guesses.Values.Count(g => g.Option == i);
                var pct = total > 0 ? (int)Math.Round(100.0 * n / total) : 0;
                var label = i < OptionLabels.Count ? OptionLabels[i] : string.Empty;
                return k + "  " + pct.ToString(CultureInfo.InvariantCulture) + "% (" + n + ")" + (label.Length > 0 ? "  " + label : string.Empty);
            }).ToList();
        }

        if (_guesses.Count == 0) return new List<string> { "no guesses yet" };
        var values = _guesses.Values.Select(g => g.Number).OrderBy(v => v).ToList();
        var median = values[values.Count / 2];
        return new List<string>
        {
            _guesses.Count + " guesses",
            "range " + values[0].ToString("0.##", CultureInfo.InvariantCulture) + " .. " + values[values.Count - 1].ToString("0.##", CultureInfo.InvariantCulture),
            "median " + median.ToString("0.##", CultureInfo.InvariantCulture),
        };
    }

    private int IndexOf(string text)
    {
        var t = (text ?? string.Empty).Trim();
        for (var i = 0; i < OptionKeys.Count; i++)
        {
            if (string.Equals(OptionKeys[i], t, StringComparison.OrdinalIgnoreCase)) return i;
        }

        return int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= OptionKeys.Count ? n - 1 : -1;
    }

    /// <summary>Accepts "25000", "25.000", "25,000", "25k", "1.5k", "2 Mio".</summary>
    public static bool TryParseNumber(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrEmpty(text)) return false;
        var t = text!.Trim().ToLowerInvariant().Replace(" ", string.Empty).Replace(((char)0x20AC).ToString(), string.Empty).Replace("eur", string.Empty);
        var factor = 1.0;
        if (t.EndsWith("k", StringComparison.Ordinal))
        {
            factor = 1000;
            t = t.Substring(0, t.Length - 1);
        }
        else if (t.EndsWith("mio", StringComparison.Ordinal))
        {
            factor = 1000000;
            t = t.Substring(0, t.Length - 3);
        }

        // Thousands separators: "25.000" / "25,000" (exactly three digits after the separator).
        if (factor == 1 && t.Length > 4 && (t[t.Length - 4] == '.' || t[t.Length - 4] == ',') && t.IndexOfAny(new[] { '.', ',' }) == t.Length - 4)
        {
            t = t.Remove(t.Length - 4, 1);
        }

        t = t.Replace(',', '.');
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || double.IsNaN(v) || double.IsInfinity(v)) return false;
        value = v * factor;
        return Math.Abs(value) < 1e12;
    }

    private sealed class Guess
    {
        public Guess(Viewer viewer, string text, int option, double number, double at)
        {
            Viewer = viewer;
            Text = text;
            Option = option;
            Number = number;
            At = at;
        }

        public Viewer Viewer { get; }

        public string Text { get; }

        public int Option { get; }

        public double Number { get; }

        public double At { get; }
    }
}
