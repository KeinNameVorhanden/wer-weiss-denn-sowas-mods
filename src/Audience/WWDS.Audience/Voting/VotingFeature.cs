using System;
using System.Collections.Generic;
using System.Linq;
using WWDS.Audience.Engine;
using WWDS.ModCore.Capabilities;
using WWDS.ModCore.Commands;
using WWDS.ModCore.Config;
using WWDS.ModCore.Events;
using WWDS.ModCore.Features;
using WWDS.ModCore.Logging;
using WWDS.ModCore.Model;

namespace WWDS.Audience.Voting;

public enum VoteAutoOpen
{
    Off,

    /// <summary>Open a vote whenever a human contestant must answer.</summary>
    HumanTurns,

    /// <summary>Open a vote on every normal and master question (show the chat's opinion).</summary>
    AllQuestions,
}

public enum VoteResolution
{
    Majority,
    WeightedRandom,
}

public enum VoteMode
{
    /// <summary>Only display and announce what chat thinks.</summary>
    ShowOnly,

    /// <summary>Submit the chat's majority for the human whose turn it is (if still unanswered).</summary>
    Assist,
}

/// <summary>
/// Community answer voting on the current multiple-choice question: "!vote A" (or plain "A"), one vote per
/// viewer, live percentages, configurable lock, tie-break and optional submission for the human player.
/// Votes for externally controlled bots are run by the Twitch opponent feature instead.
/// </summary>
public sealed class VotingFeature : FeatureBase
{
    public const string FeatureId = "Voting";

    private IConfigValue<VoteAutoOpen> _autoOpen = null!;
    private IConfigValue<VoteMode> _mode = null!;
    private IConfigValue<float> _duration = null!;
    private IConfigValue<float> _masterDuration = null!;
    private IConfigValue<bool> _allowChanges = null!;
    private IConfigValue<int> _minVotes = null!;
    private IConfigValue<TieBreak> _tieBreak = null!;
    private IConfigValue<bool> _announce = null!;
    private IConfigValue<VoteResolution> _resolution = null!;
    private IConfigValue<bool> _twitchPoll = null!;
    private readonly Random _random = new();
    private VoteInteraction? _current;
    private TeamSlot? _currentTeam;
    private QuestionInfo? _currentQuestion;
    private bool _teamAnswered;

    public override string Id => FeatureId;

    public override string DisplayName => "Community answer voting";

    public override string Description => "Chat votes on the current question; show the result or submit it for the human player.";

    public override IEnumerable<string> Dependencies => new[] { AudienceFeature.FeatureId };

    public override GameCapability RequiredCapabilities => GameCapability.ReadQuestion | GameCapability.ReadAnswers | GameCapability.ObserveAnswers;

    protected override string LogCategoryName => LogCategory.Vote;

    private AudienceService Audience => Context.Get<AudienceService>()!;

    protected override void OnStart()
    {
        var c = Context.Config;
        const string s = "Voting";
        _autoOpen = c.Bind(s, "AutoOpen", VoteAutoOpen.HumanTurns, "Off | HumanTurns | AllQuestions.");
        _mode = c.Bind(s, "Mode", VoteMode.ShowOnly, "ShowOnly | Assist (submit the majority for the human; needs Twitch TestMode = false).");
        _duration = c.Bind(s, "VoteDuration", 25f, "Seconds a vote stays open on normal questions.");
        _masterDuration = c.Bind(s, "MasterVoteDuration", 12f, "Seconds on the master question (game clock is 20 s).");
        _allowChanges = c.Bind(s, "VoteChanging", true, "Viewers may change their vote while it is open.");
        _minVotes = c.Bind(s, "MinimumVotes", 1, "Votes needed for a valid result.");
        _tieBreak = c.Bind(s, "TieBreakMethod", TieBreak.Random, "Random | First | Fallback | Moderator | Revote.");
        _announce = c.Bind(s, "ShowBreakdownInChat", true, "Post the final breakdown in chat.");
        _resolution = c.Bind(s, "Resolution", VoteResolution.Majority, "Majority | WeightedRandom (pick randomly, proportional to the votes).");
        _twitchPoll = c.Bind(s, "UseTwitchPolls", false, "Also start a native Twitch poll; its result decides when it ends first (affiliate/partner only).");
        Track(Context.Bus.Subscribe<PollUpdated>(OnPoll));

        Track(Context.Get<Bindings.BindingActions>()!.Register("vote.open", (_, _) =>
        {
            var q = Context.Adapter?.GetSnapshot().Question;
            return q != null && q.Answers.Count >= 2 && Open(q, null, q.Kind == QuestionKind.Master) ? ActionResult.Success("vote open") : ActionResult.Fail("no multiple-choice question on screen");
        }));
        Track(Context.Bus.Subscribe<AnswersPresented>(OnAnswersPresented));
        Track(Context.Bus.Subscribe<AnswerLocked>(OnAnswerLocked));
        Track(Context.Bus.Subscribe<AnswerRevealed>(_ => _currentQuestion = null));
        Track(Context.Commands.Register(new CommandDefinition("startvote", Authority.Moderator, "Open a chat vote on the current question", (_, who) =>
        {
            var q = Context.Adapter?.GetSnapshot().Question;
            if (q == null || q.Answers.Count < 2) return new CommandResult(CommandOutcome.Handled, "No multiple-choice question on screen");
            return new CommandResult(CommandOutcome.Handled, Open(q, null, q.Kind == QuestionKind.Master) ? "Vote open" : "Could not open a vote");
        })));
    }

    private void OnAnswersPresented(AnswersPresented e)
    {
        if (e.Question.Kind == QuestionKind.Bonus || e.Question.Answers.Count < 2) return;
        var auto = _autoOpen.Value;
        if (auto == VoteAutoOpen.Off) return;
        var answering = e.Answering;
        if (answering != null && answering.ExternallyControlled) return; // the opponent feature runs that vote
        var human = answering != null && answering.Kind == ContestantKind.Human;
        if (auto == VoteAutoOpen.HumanTurns && !human && e.Question.Kind != QuestionKind.Master) return;
        Open(e.Question, human ? answering!.Team : null, e.Question.Kind == QuestionKind.Master);
    }

    private bool Open(QuestionInfo q, TeamSlot? team, bool master)
    {
        if (_current != null && _current.IsActive) Audience.Engine.Cancel(_current, "new question");
        var options = new InteractionOptions
        {
            Title = master ? "Finalfrage: Was tippt der Chat?" : "Was tippt der Chat?",
            Duration = master ? Math.Min(_masterDuration.Value, ShowFormat.MasterClockSeconds - 6) : _duration.Value,
            AllowChanges = _allowChanges.Value,
            MinParticipants = Math.Max(1, _minVotes.Value),
            DecisionTimeout = master ? 3 : 20,
        };
        var choices = q.Answers.Select(a => new VoteOption(a.Key, a.Text)).ToList();
        _currentTeam = team;
        _currentQuestion = q;
        _teamAnswered = false;
        _current = Audience.OpenVote(options, choices, _tieBreak.Value, -1, r => Apply(q, team, r), ignoreCooldown: true);
        if (_current == null) return false;
        _current.Subject = q.Id;
        Audience.Announce("Abstimmung: " + string.Join(" | ", choices.Select(c => c.Key + ") " + Shorten(c.Label, 30)).ToArray()) + " - !vote A/B/C");
        if (_twitchPoll.Value)
        {
            var pollChoices = choices.Select(c => Shorten(c.Key + ": " + c.Label, 25)).ToList();
            Context.Bus.Publish(new PollCreateRequested(Shorten(q.Text, 60), pollChoices, (int)Math.Max(15, options.Duration)));
        }

        return true;
    }

    private void OnAnswerLocked(AnswerLocked e)
    {
        // The contestant answered before chat finished: close the vote early, keep the result for display/statistics.
        if (_currentTeam != e.Team) return;
        _teamAnswered = true;
        if (_current != null && _current.State == InteractionState.Collecting) Audience.Engine.Lock(_current, "contestant answered");
    }

    /// <summary>A native Twitch poll for the current question ended: its winner decides the vote.</summary>
    private void OnPoll(PollUpdated e)
    {
        if (!_twitchPoll.Value || e.Status != "end" || _current == null || !_current.IsActive || e.Choices.Count == 0) return;
        var best = e.Choices.OrderByDescending(c => c.Value).First();
        if (best.Value <= 0) return;
        var key = best.Key.Split(':')[0].Trim();
        var by = new Viewer("twitch-poll", "twitch-poll", "Twitch-Umfrage", Authority.Broadcaster, "twitch");
        if (Audience.Engine.Force(_current, key, by)) Log.Info("Twitch poll decided " + key + " (" + best.Value + " votes)");
    }

    private void Apply(QuestionInfo q, TeamSlot? team, InteractionResult result)
    {
        if (result.Success && _resolution.Value == VoteResolution.WeightedRandom && result.Reason != ResultReason.Override && _current != null)
        {
            var weights = Enumerable.Range(0, _current.Choices.Count).Select(_current.WeightFor).ToList();
            var roll = _random.NextDouble() * weights.Sum();
            for (var i = 0; i < weights.Count; i++)
            {
                roll -= weights[i];
                if (roll >= 0) continue;
                result.WinningIndex = i;
                result.WinningText = _current.Choices[i].Key;
                break;
            }
        }

        if (_announce.Value && result.Success) Audience.Announce("Chat sagt " + result.WinningText + ": " + Breakdown(result.Lines));
        if (!result.Success || _mode.Value != VoteMode.Assist || team == null) return;
        if (_teamAnswered)
        {
            Log.Info("Vote result " + result.WinningText + " not submitted: the contestant answered first");
            return;
        }

        var adapter = Context.Adapter;
        if (adapter == null || !Context.Capabilities.Has(GameCapability.SubmitAnswerForHuman)) return;
        if (!ReferenceEquals(_currentQuestion, q))
        {
            Log.Info("Vote result " + result.WinningText + " not submitted: the question already moved on");
            return;
        }

        if (!Audience.Gate.Allow("submit chat answer " + result.WinningText + " for team " + team)) return;
        Log.Info("Submitting chat answer: " + adapter.SubmitAnswer(team.Value, result.WinningIndex));
    }

    /// <summary>"A  52% (13)  label" lines to "A 52% | B 31% | C 17%".</summary>
    internal static string Breakdown(IList<string> lines) => string.Join(" | ", lines.Select(l =>
    {
        var parts = l.Split(new[] { "  " }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[0] + " " + parts[1] : l;
    }).ToArray());

    private static string Shorten(string text, int max) => text.Length <= max ? text : text.Substring(0, max - 3) + "...";
}
