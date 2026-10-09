using System;
using System.Collections.Generic;
using System.Linq;
using WWDS.Audience.Guessing;
using WWDS.Audience.Opponents;
using WWDS.ModCore.Capabilities;
using WWDS.ModCore.Commands;
using WWDS.ModCore.Config;
using WWDS.ModCore.Events;
using WWDS.ModCore.Features;
using WWDS.ModCore.Model;

namespace WWDS.Audience.Predictions;

/// <summary>
/// "Who wins?" bets at the start of every show: a native Twitch prediction (channel points, affiliate or
/// partner) and/or a chat bet ("!vote A/B"). Outcomes are labelled by who plays each team: the streamer,
/// a second local player, the game's AI, or Twitch (the chat or the selected viewer). Resolved by the result.
/// </summary>
public sealed class WhoWinsFeature : FeatureBase
{
    public const string FeatureId = "WhoWins";

    private IConfigValue<bool> _prediction = null!;
    private IConfigValue<bool> _chatBets = null!;
    private IConfigValue<int> _window = null!;
    private IConfigValue<string> _streamer = null!;
    private IConfigValue<string> _player = null!;
    private IConfigValue<string> _ai = null!;
    private IConfigValue<string> _twitch = null!;
    private bool _predictionOpen;
    private bool _predictionLocked;

    public override string Id => FeatureId;

    public override string DisplayName => "Who wins? bets";

    public override string Description => "Twitch prediction and chat bet on the winner: streamer, AI, Twitch viewer or player 2.";

    public override IEnumerable<string> Dependencies => new[] { AudienceFeature.FeatureId, GuessingFeature.FeatureId };

    public override GameCapability RequiredCapabilities => GameCapability.ReadContestants | GameCapability.ReadScore;

    /// <summary>Labels of the current bet (A, B), for the overlay.</summary>
    public string[] Labels { get; private set; } = new string[0];

    public PredictionUpdated? LastPrediction { get; private set; }

    private AudienceService Audience => Context.Get<AudienceService>()!;

    protected override void OnStart()
    {
        var c = Context.Config;
        const string s = "WhoWins";
        _prediction = c.Bind(s, "TwitchPrediction", false, "Start a channel-points prediction on the winner at show start (affiliate/partner; spends viewers' channel points).");
        _chatBets = c.Bind(s, "ChatBets", true, "Chat bets on the winner with !vote A/B (leaderboard points).");
        _window = c.Bind(s, "WindowSeconds", 120, "Seconds the bet stays open.");
        _streamer = c.Bind(s, "StreamerLabel", "Streamer", "Label of the first local player.");
        _player = c.Bind(s, "PlayerLabel", "Spieler", "Label prefix of further local players (\"Spieler 2\").");
        _ai = c.Bind(s, "AiLabel", "KI", "Label of the game's AI contestant.");
        _twitch = c.Bind(s, "TwitchLabel", "Twitch", "Label of the audience-controlled contestant.");
        Context.Provide(this);

        Track(Context.Bus.Subscribe<GameStarted>(_ => Context.Scheduler.After(2, Open)));
        Track(Context.Bus.Subscribe<GameFinished>(OnFinished));
        Track(Context.Bus.Subscribe<QuestionStarted>(_ => LockPrediction()));
        Track(Context.Bus.Subscribe<PredictionUpdated>(e => LastPrediction = e));
        Track(Context.Get<Bindings.BindingActions>()!.Register("whowins.open", (_, _) =>
        {
            Open();
            return ActionResult.Success("who-wins bet opened");
        }));
        Track(Context.Commands.Register(new CommandDefinition("bet", Authority.Moderator, "Open the who-wins bet now", (_, _) =>
        {
            Open();
            return new CommandResult(CommandOutcome.Handled, Labels.Length == 2 ? "Bet: A " + Labels[0] + " / B " + Labels[1] : "No show running");
        })));
    }

    /// <summary>Describes who plays each team from the current snapshot.</summary>
    public string[] DescribeTeams()
    {
        var snap = Context.Adapter?.GetSnapshot();
        if (snap?.TeamA == null || snap.TeamB == null) return new string[0];
        var humans = 0;
        var opponent = Context.Get<OpponentFeature>();
        string Describe(TeamInfo team)
        {
            var parts = new List<string>();
            foreach (var m in team.Members)
            {
                if (m.Kind == ContestantKind.Human)
                {
                    humans++;
                    parts.Add((humans == 1 ? _streamer.Value : _player.Value + " " + humans) + " (" + m.Name + ")");
                }
                else if (m.ExternallyControlled)
                {
                    var decider = opponent?.CurrentDecider;
                    parts.Add(decider != null && opponent!.Opponent.Mode == OpponentMode.SelectedViewer ? _twitch.Value + " @" + decider.DisplayName : _twitch.Value);
                }
                else
                {
                    parts.Add(_ai.Value + " (" + m.Name + ")");
                }
            }

            return string.Join(" + ", parts.ToArray());
        }

        return new[] { Describe(snap.TeamA), Describe(snap.TeamB) };
    }

    public void Open()
    {
        Labels = DescribeTeams();
        if (Labels.Length != 2) return;
        if (_chatBets.Value) Context.Get<GuessingFeature>()?.OpenWinner(_window.Value, Labels[0], Labels[1]);
        if (_prediction.Value && !_predictionOpen)
        {
            if (!Audience.Gate.Allow("start Twitch prediction 'Wer gewinnt?'")) return;
            _predictionOpen = true;
            _predictionLocked = false;
            Context.Bus.Publish(new PredictionCreateRequested("Wer gewinnt diese Show?", Labels.Select(Short).ToList(), _window.Value));
        }
    }

    /// <summary>Closes betting when the first question appears (the chat bet is closed by the guessing feature).</summary>
    private void LockPrediction()
    {
        if (!_predictionOpen || _predictionLocked) return;
        _predictionLocked = true;
        Context.Bus.Publish(new PredictionLockRequested());
        Log.Info("Prediction locked at the first question");
    }

    private static string Short(string label) => label.Length <= 25 ? label : label.Substring(0, 25);

    private void OnFinished(GameFinished e)
    {
        if (!_predictionOpen) return;
        _predictionOpen = false;
        var index = e.Aborted || e.Winner == null ? -1 : e.Winner == TeamSlot.A ? 0 : 1;
        Context.Bus.Publish(new PredictionResolveRequested(index));
        Log.Info(index < 0 ? "Prediction cancelled (no winner), points refunded" : "Prediction resolved: " + Labels[index]);
    }

}
