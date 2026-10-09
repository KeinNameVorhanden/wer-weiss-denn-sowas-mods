using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using WWDS.Audience.Engine;
using WWDS.ModCore.Capabilities;
using WWDS.ModCore.Commands;
using WWDS.ModCore.Config;
using WWDS.ModCore.Events;
using WWDS.ModCore.Features;
using WWDS.ModCore.Logging;
using WWDS.ModCore.Model;

namespace WWDS.Audience.Guessing;

/// <summary>Points per viewer across guess rounds (session scoped; Statistics may persist it).</summary>
public sealed class GuessLeaderboard
{
    private readonly Dictionary<string, Entry> _entries = new();

    public void Award(Viewer viewer, int points)
    {
        if (!_entries.TryGetValue(viewer.Id, out var e)) _entries[viewer.Id] = e = new Entry(viewer.DisplayName);
        e.Points += points;
        e.Wins++;
    }

    public IList<KeyValuePair<string, int>> Top(int count) => _entries.Values
        .OrderByDescending(e => e.Points).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
        .Take(count).Select(e => new KeyValuePair<string, int>(e.Name, e.Points)).ToList();

    private sealed class Entry
    {
        public Entry(string name) => Name = name;

        public string Name { get; }

        public int Points { get; set; }

        public int Wins { get; set; }
    }
}

/// <summary>
/// Guess rounds: "guess the answer" (A/B/C before the reveal), "guess the winner" (team at show start),
/// "guess the prize" (winner's final money). Outcomes come from game events; winners earn leaderboard
/// points.
/// </summary>
public sealed class GuessingFeature : FeatureBase
{
    public const string FeatureId = "Guessing";

    private IConfigValue<bool> _answer = null!;
    private IConfigValue<bool> _winner = null!;
    private IConfigValue<bool> _prize = null!;
    private IConfigValue<float> _duration = null!;
    private IConfigValue<bool> _allowChanges = null!;
    private GuessInteraction? _answerRound;
    private GuessInteraction? _winnerRound;
    private GuessInteraction? _prizeRound;
    private IConfigValue<bool> _contestant = null!;
    private IConfigValue<bool> _closeWinnerAtFirstQuestion = null!;
    private GuessInteraction? _contestantRound;
    private TeamSlot? _contestantTeam;

    public override string Id => FeatureId;

    public override string DisplayName => "Community guessing";

    public override string Description => "Chat guesses answers, the winning team or the prize money; correct guesses earn points.";

    public override IEnumerable<string> Dependencies => new[] { AudienceFeature.FeatureId };

    public override GameCapability RequiredCapabilities => GameCapability.ObserveAnswers | GameCapability.ReadScore;

    protected override string LogCategoryName => LogCategory.Guess;

    public GuessLeaderboard Leaderboard { get; } = new();

    private AudienceService Audience => Context.Get<AudienceService>()!;

    protected override void OnStart()
    {
        var c = Context.Config;
        const string s = "Guessing";
        _answer = c.Bind(s, "GuessAnswers", false, "Open a guess round (A/B/C) on every normal question; points when right.");
        _winner = c.Bind(s, "GuessWinner", false, "At show start, chat guesses the winning team (A/B).");
        _prize = c.Bind(s, "GuessPrize", false, "At show start, chat guesses the winner's final money (closest wins).");
        _duration = c.Bind(s, "GuessDuration", 30f, "Seconds a guess round collects guesses.");
        _allowChanges = c.Bind(s, "AllowChanges", false, "Viewers may change their guess.");
        _closeWinnerAtFirstQuestion = c.Bind(s, "CloseWinnerAtFirstQuestion", true, "Stop taking winner bets when the first question appears (the bet still pays out at the end).");
        _contestant = c.Bind(s, "GuessContestantCorrect", false, "On every normal question: will the contestant answer correctly? (!guess ja/nein). GuessAnswers takes precedence.");
        Context.Provide(this);
        Track(Context.Bus.Subscribe<AnswerLocked>(OnAnswerLocked));

        Track(Context.Get<Bindings.BindingActions>()!.Register("guess.open", (args, _) =>
        {
            var kind = args.TryGetValue("kind", out var k) ? k.ToLowerInvariant() : "answer";
            var ok = kind == "winner" ? OpenWinner(_duration.Value) : kind == "prize" ? OpenPrize(_duration.Value) : OpenAnswer(Context.Adapter?.GetSnapshot().Question, _duration.Value);
            return ok ? ActionResult.Success("guess round open") : ActionResult.Fail("could not open guess round");
        }));
        Track(Context.Bus.Subscribe<GameStarted>(_ => OnShowStarted()));
        Track(Context.Bus.Subscribe<AnswersPresented>(OnAnswersPresented));
        Track(Context.Bus.Subscribe<AnswerRevealed>(OnRevealed));
        Track(Context.Bus.Subscribe<GameFinished>(OnFinished));
        Track(Context.Bus.Subscribe<InteractionLocked>(OnLocked));
        Track(Context.Bus.Subscribe<QuestionStarted>(_ => CloseWinnerBet("first question")));
        Track(Context.Commands.Register(new CommandDefinition("startguess", Authority.Moderator, "!startguess answer|winner|prize [seconds]", (cmd, _) =>
        {
            var seconds = double.TryParse(cmd.Arg(1), NumberStyles.Float, CultureInfo.InvariantCulture, out var sec) ? sec : _duration.Value;
            var ok = cmd.Arg(0).ToLowerInvariant() switch
            {
                "winner" => OpenWinner(seconds),
                "prize" => OpenPrize(seconds),
                _ => OpenAnswer(Context.Adapter?.GetSnapshot().Question, seconds),
            };
            return new CommandResult(CommandOutcome.Handled, ok ? "Guess round open" : "Could not open a guess round");
        })));
        Track(Context.Commands.Register(new CommandDefinition("top", Authority.Viewer, "Guess leaderboard", (_, _) =>
            new CommandResult(CommandOutcome.Handled, "Top: " + string.Join(", ", Leaderboard.Top(5).Select(kv => kv.Key + " " + kv.Value).ToArray())))
        { GlobalCooldown = 30 }));
    }

    private void OnShowStarted()
    {
        _prizeRound = null;
        if (_winner.Value) OpenWinner(_duration.Value);
        else if (_prize.Value) OpenPrize(_duration.Value);
    }

    public bool OpenWinner(double seconds) => OpenWinner(seconds, "Team Bernhard", "Team Elton");

    /// <summary>Chat bet on the winner; labels describe who plays each team (e.g. "Streamer", "Twitch").</summary>
    public bool OpenWinner(double seconds, string labelA, string labelB)
    {
        if (_winnerRound != null && _winnerRound.IsActive) return true;
        _winnerRound = Audience.OpenGuess(Options("Wer gewinnt?", seconds), GuessKind.Option, new List<string> { "A", "B" }, "winner", r => Award(r, 3), new List<string> { labelA, labelB });
        if (_winnerRound != null) Audience.Announce("Wette: Wer gewinnt? !vote A (" + labelA + ") oder !vote B (" + labelB + ")");
        return _winnerRound != null;
    }

    public bool OpenPrize(double seconds)
    {
        _prizeRound = Audience.OpenGuess(Options("Wie viel Geld gewinnt das Siegerteam?", seconds), GuessKind.Number, null, "prize", r => Award(r, 5));
        if (_prizeRound != null) Audience.Announce("Tipp: Wie viel Geld gewinnt das Siegerteam? !guess <Betrag>");
        return _prizeRound != null;
    }

    public bool OpenAnswer(QuestionInfo? q, double seconds)
    {
        if (q == null || q.Answers.Count < 2) return false;
        _answerRound = Audience.OpenGuess(Options("Richtige Antwort?", seconds), GuessKind.Option, q.Answers.Select(a => a.Key).ToList(), q.Id, r => Award(r, 1), q.Answers.Select(a => a.Text).ToList());
        return _answerRound != null;
    }

    private InteractionOptions Options(string title, double seconds) => new()
    {
        Title = title,
        Duration = Math.Max(5, seconds),
        AllowChanges = _allowChanges.Value,
        MinParticipants = 1,
        DecisionTimeout = 3600,
    };

    private void OnAnswersPresented(AnswersPresented e)
    {
        if (e.Question.Kind != QuestionKind.Normal) return;
        if (_answer.Value)
        {
            OpenAnswer(e.Question, _duration.Value);
            return;
        }

        if (_contestant.Value && e.Answering != null) OpenContestant(e.Answering, Math.Min(_duration.Value, 20));
    }

    /// <summary>"Will this contestant answer correctly?" Outcome comes from the contestant's locked answer.</summary>
    public bool OpenContestant(ContestantInfo who, double seconds)
    {
        _contestantTeam = who.Team;
        _contestantRound = Audience.OpenGuess(Options("Antwortet " + who.Name + " richtig?", seconds), GuessKind.Option, new List<string> { "ja", "nein" }, "contestant:" + who.Id, r => Award(r, 1));
        if (_contestantRound != null) Audience.Announce("Tipp: Antwortet " + who.Name + " richtig? !guess ja oder !guess nein");
        return _contestantRound != null;
    }

    private void OnAnswerLocked(AnswerLocked e)
    {
        var round = _contestantRound;
        if (round == null || !round.IsActive || e.Team != _contestantTeam || e.Kind != QuestionKind.Normal) return;
        if (round.State == InteractionState.Collecting) Audience.Engine.Lock(round, "contestant answered");
        round.ProvideOutcome(e.Correct ? "ja" : "nein");
    }

    private void OnRevealed(AnswerRevealed e)
    {
        var round = _answerRound;
        if (round == null || !round.IsActive || e.CorrectIndex < 0) return;
        if (round.State == InteractionState.Collecting) Audience.Engine.Lock(round, "answer revealed");
        round.ProvideOutcome(AnswerOption.KeyOf(e.CorrectIndex));
    }

    /// <summary>Stops taking winner bets (the first question is on screen); the bet resolves at the end of the show.</summary>
    public void CloseWinnerBet(string why)
    {
        if (_closeWinnerAtFirstQuestion.Value && _winnerRound != null && _winnerRound.State == InteractionState.Collecting) Audience.Engine.Lock(_winnerRound, why);
    }

    private void OnFinished(GameFinished e)
    {
        if (_winnerRound != null && _winnerRound.IsActive)
        {
            if (e.Winner == null) Audience.Engine.Cancel(_winnerRound, "no winner");
            else _winnerRound.ProvideOutcome(e.Winner.Value.ToString());
        }

        if (_prizeRound != null && _prizeRound.IsActive)
        {
            if (e.Aborted) Audience.Engine.Cancel(_prizeRound, "show aborted");
            else _prizeRound.ProvideOutcome(Math.Max(e.ScoreA, e.ScoreB).ToString(CultureInfo.InvariantCulture));
        }
    }

    private void OnLocked(InteractionLocked e)
    {
        // Winner and prize rounds share !guess, so the prize round opens once the winner round is locked.
        if (ReferenceEquals(e.Interaction, _winnerRound) && _prize.Value && _prizeRound == null) OpenPrize(_duration.Value);
    }

    private void Award(InteractionResult result, int points)
    {
        if (!result.Success) return;
        foreach (var w in result.Winners) Leaderboard.Award(w, points);
        Audience.Announce("Tipp-Ergebnis: " + result.Summary);
    }
}
