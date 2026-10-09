using System;
using System.Collections.Generic;
using System.Linq;
using WWDS.Audience.Engine;
using WWDS.Audience.Voting;
using WWDS.ModCore.Adapters;
using WWDS.ModCore.Capabilities;
using WWDS.ModCore.Commands;
using WWDS.ModCore.Config;
using WWDS.ModCore.Events;
using WWDS.ModCore.Features;
using WWDS.ModCore.Logging;
using WWDS.ModCore.Model;

namespace WWDS.Audience.Opponents;

/// <summary>
/// "Twitch as opponent": takes over the decisions of the game's bot contestant and lets the audience
/// make them (crowd majority, a selected or rotating viewer, or a hybrid with a virtual contestant).
/// The game supports exactly one bot per show, so one audience opponent is active at a time; a second
/// one would need the SecondBotInTeam capability, which the adapter withholds.
/// </summary>
public sealed class OpponentFeature : FeatureBase
{
    public const string FeatureId = "TwitchOpponent";

    private readonly Random _random = new();
    private readonly List<Viewer> _volunteers = new();
    private readonly Dictionary<long, Interaction> _voteByDecision = new();
    private IConfigValue<bool> _active = null!;
    private IConfigValue<OpponentMode> _mode = null!;
    private IConfigValue<OpponentBehaviorKind> _behavior = null!;
    private IConfigValue<string> _name = null!;
    private IConfigValue<string> _teamLabel = null!;
    private IConfigValue<string> _selectedViewer = null!;
    private IConfigValue<float> _answerSeconds = null!;
    private IConfigValue<float> _categorySeconds = null!;
    private IConfigValue<float> _difficulty = null!;
    private IConfigValue<int> _minVotes = null!;
    private TeamSlot? _team;
    private bool _planned;
    private TeamSlot? _plannedTeam;
    private Viewer? _currentDecider;
    private int _rotation;
    private int _strikes;
    private readonly Dictionary<string, ChatterInfo> _chatters = new(StringComparer.OrdinalIgnoreCase);
    private IConfigValue<int> _afkStrikes = null!;
    private IConfigValue<RetargetSource> _afkRetarget = null!;
    private IConfigValue<float> _activeMinutes = null!;

    private sealed class ChatterInfo
    {
        public ChatterInfo(Viewer viewer, double lastSeen)
        {
            Viewer = viewer;
            LastSeen = lastSeen;
        }

        public Viewer Viewer { get; set; }

        public double LastSeen { get; set; }
    }

    public override string Id => FeatureId;

    public override string DisplayName => "Twitch opponent";

    public override string Description => "The audience plays the game's bot contestant (crowd, chat team, selected/rotating viewer, hybrid).";

    public override IEnumerable<string> Dependencies => new[] { AudienceFeature.FeatureId };

    public override GameCapability RequiredCapabilities => GameCapability.ControlBotAnswer | GameCapability.ReadContestants;

    protected override string LogCategoryName => LogCategory.Opponent;

    public OpponentDefinition Opponent { get; } = new();

    public TeamSlot? Team => _team;

    public Viewer? CurrentDecider => _currentDecider;

    private AudienceService Audience => Context.Get<AudienceService>()!;

    protected override void OnStart()
    {
        var c = Context.Config;
        const string s = "Opponent";
        _active = c.Bind(s, "Active", false, "Take over the bot contestant in the next show (can be toggled with !crowd / !opponent on|off).");
        _mode = c.Bind(s, "Mode", OpponentMode.Crowd, "Crowd | ChatTeam | SelectedViewer | RotatingViewers | Hybrid.");
        _behavior = c.Bind(s, "Behavior", OpponentBehaviorKind.AudienceMajority, "AudienceMajority | AudienceWeighted | Random | AlwaysA | RuleBased | HumanControlled | Hybrid.");
        _name = c.Bind(s, "DisplayName", "Twitch Chat", "Contestant name shown in the game (empty = keep the bot's name).");
        _teamLabel = c.Bind(s, "TeamLabel", "Team Twitch", "Team label shown in the player frames (empty = game default).");
        _selectedViewer = c.Bind(s, "SelectedViewer", "", "Login of the viewer for Mode = SelectedViewer (or use !opponent pick <login>).");
        _answerSeconds = c.Bind(s, "AnswerVoteSeconds", 25f, "Vote time for normal answers (master answers are limited by the 20 s game clock).");
        _categorySeconds = c.Bind(s, "CategoryVoteSeconds", 20f, "Vote time for category, bet and estimate decisions.");
        _difficulty = c.Bind(s, "VirtualContestantSkill", 0.6f, "0..1 chance the virtual contestant (RuleBased/Hybrid) knows the answer.");
        _minVotes = c.Bind(s, "MinimumVotes", 1, "Votes needed; fewer means the behaviour's fallback decides.");
        _afkStrikes = c.Bind(s, "AfkStrikes", 1, "Streamer vs viewer: missed decisions before the opponent moves to another viewer.");
        _afkRetarget = c.Bind(s, "AfkRetarget", RetargetSource.VolunteersThenChatters, "Who takes over from an AFK viewer: Off | Volunteers | ActiveChatters | VolunteersThenChatters.");
        _activeMinutes = c.Bind(s, "ActiveChatterMinutes", 10f, "A chatter counts as active for this many minutes after their last message.");
        Context.Provide(this);
        Track(Context.Bus.Subscribe<ChatMessageReceived>(e =>
        {
            var key = e.Viewer.Login.ToLowerInvariant();
            if (_chatters.TryGetValue(key, out var info))
            {
                info.Viewer = e.Viewer;
                info.LastSeen = Context.Clock.Now;
            }
            else
            {
                _chatters[key] = new ChatterInfo(e.Viewer, Context.Clock.Now);
            }
        }));

        var actions = Context.Get<Bindings.BindingActions>()!;
        Track(actions.Register("opponent.enable", (_, _) => ActionResult.Success(SetActive(true))));
        Track(actions.Register("opponent.disable", (_, _) => ActionResult.Success(SetActive(false))));
        Track(actions.Register("opponent.mode", (args, _) =>
        {
            if (!args.TryGetValue("mode", out var m) || !Enum.IsDefined(typeof(OpponentMode), Capitalize(m))) return ActionResult.Fail("unknown mode");
            _mode.Value = (OpponentMode)Enum.Parse(typeof(OpponentMode), Capitalize(m));
            if (_mode.Value == OpponentMode.Hybrid) _behavior.Value = OpponentBehaviorKind.Hybrid;
            Opponent.Mode = _mode.Value;
            Opponent.Behavior = _behavior.Value;
            return ActionResult.Success("mode " + _mode.Value);
        }));
        Track(Context.Bus.Subscribe<SessionCreated>(OnSessionCreated));
        Track(Context.Bus.Subscribe<DecisionRequested>(e => OnDecision(e.Request)));
        Track(Context.Bus.Subscribe<DecisionCompleted>(e => OnDecisionCompleted(e.Request)));
        Track(Context.Bus.Subscribe<RoundStarted>(OnRoundStarted));
        Track(Context.Bus.Subscribe<QuestionFinished>(OnQuestionFinished));
        Track(Context.Bus.Subscribe<GameFinished>(OnGameFinished));
        RegisterCommands();
    }

    /// <summary>Turns the audience opponent on for the current/next show (bindings action "opponent.enable").</summary>
    public string SetActive(bool active)
    {
        _active.Value = active;
        if (!active && _team.HasValue)
        {
            Context.Adapter?.SetExternalControl(_team.Value, false);
            _team = null;
            return "Twitch opponent OFF (game bot decides again)";
        }

        if (active && Context.Adapter?.GetSnapshot().InShow == true) TakeOver();
        return "Twitch opponent " + (active ? "ON" : "OFF");
    }

    /// <summary>True while the audience opponent is switched on (config [Opponent] Active).</summary>
    public bool IsActive => _active.Value;

    /// <summary>
    /// Plans the next show only (quickplay line-up): the audience takes over exactly <paramref name="team"/>'s
    /// bot, or no bot at all when null, regardless of [Opponent] Active.
    /// </summary>
    public void PlanNextShow(TeamSlot? team)
    {
        _planned = true;
        _plannedTeam = team;
    }

    private void OnSessionCreated(SessionCreated e)
    {
        _team = null;
        _rotation = 0;
        if (_planned)
        {
            _planned = false;
            var planned = _plannedTeam;
            if (planned != null) Context.Scheduler.After(0.5, () => TakeOver(planned));
            return;
        }

        if (_active.Value) Context.Scheduler.After(0.5, () => TakeOver(null));
    }

    private void TakeOver() => TakeOver(null);

    private void TakeOver(TeamSlot? preferred)
    {
        var adapter = Context.Adapter;
        var snap = adapter?.GetSnapshot();
        if (adapter == null || snap == null) return;
        var order = preferred == TeamSlot.A ? new[] { snap.TeamA } : preferred == TeamSlot.B ? new[] { snap.TeamB } : new[] { snap.TeamB, snap.TeamA };
        var team = order.FirstOrDefault(t => t != null && t.Members.Any(m => m.Kind == ContestantKind.Bot));
        if (team == null)
        {
            Log.Info("No bot contestant in this show: the Twitch opponent needs a free team (play 1 human vs bot).");
            return;
        }

        if (!Audience.Gate.Allow("hand team " + team.Slot + "'s bot to the audience")) return;
        var r = adapter.SetExternalControl(team.Slot, true);
        if (!r.Ok)
        {
            Log.Warn(r.Message);
            return;
        }

        _team = team.Slot;
        Opponent.Mode = _mode.Value;
        Opponent.Behavior = _behavior.Value;
        Opponent.Difficulty = _difficulty.Value;
        Opponent.DisplayName = _name.Value;
        Opponent.Team = team.Slot;
        var bot = team.Members.First(m => m.Kind == ContestantKind.Bot);
        if (_name.Value.Trim().Length > 0) adapter.RenameContestant(team.Slot, team.Members.IndexOf(bot), _name.Value);
        if (_teamLabel.Value.Trim().Length > 0) adapter.RenameTeam(team.Slot, _teamLabel.Value);
        if (_mode.Value == OpponentMode.ChatTeam) Context.Get<Naming.NamingFeature>()?.OpenNaming("opponent-team", 40);
        Log.Info("Audience controls team " + team.Slot + " (" + _mode.Value + ", " + _behavior.Value + ")");
        Audience.Announce("Der Chat spielt mit! " + (_teamLabel.Value.Length > 0 ? _teamLabel.Value : "Team " + team.Slot) + " wird von euch gesteuert: !vote A/B/C wenn ihr dran seid.");
    }

    private void OnRoundStarted(RoundStarted e)
    {
        if (_team == null || e.Team != _team || _mode.Value != OpponentMode.RotatingViewers) return;
        if (_volunteers.Count == 0)
        {
            _currentDecider = null;
            Audience.Announce("Niemand hat sich gemeldet (!join): der ganze Chat entscheidet diese Runde.");
            return;
        }

        _currentDecider = _volunteers[_rotation++ % _volunteers.Count];
        Audience.Announce("@" + _currentDecider.Login + " entscheidet diese Runde fuer " + TeamName() + "!");
    }

    private void OnDecision(DecisionRequest request)
    {
        if (_team == null || request.Team != _team) return;
        var behavior = OpponentBehaviors.Get(Opponent.Behavior);
        Opponent.Statistics.Decisions++;
        if (!behavior.UsesVotes)
        {
            Context.Scheduler.After(2, () => Resolve(request, behavior.Choose(request, null, Opponent, _random), null, "virtual contestant"));
            return;
        }

        var now = Context.Clock.Now;
        var budget = request.Deadline.HasValue ? request.Deadline.Value - now - 2 : double.MaxValue;
        var wanted = request.Kind == DecisionKind.Answer ? _answerSeconds.Value : _categorySeconds.Value;
        var duration = Math.Max(3, Math.Min(wanted, budget));
        var options = new InteractionOptions
        {
            Title = TeamName() + ": " + Describe(request),
            Duration = duration,
            AllowChanges = true,
            MinParticipants = Math.Max(1, _minVotes.Value),
            DecisionTimeout = 2,
        };
        var decider = Decider();
        if (decider != null)
        {
            options.AllowedUserIds = new List<string> { decider.Id, "login:" + decider.Login.ToLowerInvariant() };
            options.EarlyCompleteAt = 1;
        }

        Interaction? interaction;
        if (request.Kind == DecisionKind.Estimate)
        {
            var number = new NumberVoteInteraction(options, request.EstimateMin ?? 0, request.EstimateMax ?? 1e9, Audience.NumberAggregation)
            {
                OnApply = r => ResolveEstimate(request, behavior, r),
            };
            interaction = Audience.Engine.Open(number, ignoreCooldown: true) ? number : null;
        }
        else
        {
            var choices = request.Options.Select(o => new VoteOption(o.Key, o.Label)).ToList();
            interaction = Audience.OpenVote(options, choices, TieBreak.Random, -1, r => ResolveVote(request, behavior, r), ignoreCooldown: true);
        }

        if (interaction == null)
        {
            Log.Warn("Could not open a vote for " + request + "; the adapter's fallback will decide");
            return;
        }

        interaction.Subject = request.Question?.Id ?? request.Kind.ToString();
        _voteByDecision[request.Id] = interaction;
        Audience.Announce(options.Title + " (" + (int)duration + " s) " + (decider != null ? "@" + decider.Login + ": " : string.Empty) + Hint(request));
    }

    private Viewer? Decider() => _mode.Value switch
    {
        OpponentMode.SelectedViewer => SelectedViewer(),
        OpponentMode.RotatingViewers => _currentDecider,
        _ => null,
    };

    /// <summary>
    /// Streamer-vs-viewer: the configured viewer, else the current pick; with nobody picked yet, one is
    /// chosen from volunteers (!join) or recently active chatters.
    /// </summary>
    private Viewer? SelectedViewer()
    {
        if (_currentDecider != null) return _currentDecider;
        var login = _selectedViewer.Value.Trim().TrimStart('@').ToLowerInvariant();
        if (login.Length > 0)
        {
            _currentDecider = FindViewer(login) ?? new Viewer("login:" + login, login, login, Authority.Viewer, "twitch");
            return _currentDecider;
        }

        Retarget("no viewer selected");
        return _currentDecider;
    }

    private Viewer? FindViewer(string login)
    {
        login = login.Trim().TrimStart('@').ToLowerInvariant();
        var v = _volunteers.FirstOrDefault(x => x.Login.ToLowerInvariant() == login);
        if (v != null) return v;
        return _chatters.TryGetValue(login, out var c) ? c.Viewer : null;
    }

    /// <summary>Viewers who could take over: volunteers first, then recently active chatters.</summary>
    public IList<Viewer> Candidates()
    {
        var now = Context.Clock.Now;
        var window = _activeMinutes.Value * 60;
        var list = new List<Viewer>(_volunteers);
        if (_afkRetarget.Value == RetargetSource.VolunteersThenChatters || _afkRetarget.Value == RetargetSource.ActiveChatters)
        {
            foreach (var c in _chatters.Values.Where(c => now - c.LastSeen <= window && c.Viewer.Authority < Authority.Broadcaster).OrderByDescending(c => c.LastSeen))
            {
                if (list.All(v => v.Id != c.Viewer.Id)) list.Add(c.Viewer);
            }
        }

        if (_afkRetarget.Value == RetargetSource.ActiveChatters) list.RemoveAll(v => _volunteers.Contains(v) && !_chatters.ContainsKey(v.Login.ToLowerInvariant()));
        return list;
    }

    /// <summary>Hands the opponent to the next candidate (AFK, forced by a moderator or the streamer).</summary>
    public string Retarget(string reason)
    {
        var candidates = Candidates().Where(v => _currentDecider == null || v.Id != _currentDecider.Id).ToList();
        _strikes = 0;
        if (candidates.Count == 0)
        {
            _currentDecider = null;
            var msg = "Kein weiterer Zuschauer verfuegbar (" + reason + "): der ganze Chat entscheidet. Mit !join meldet ihr euch.";
            Audience.Announce(msg);
            return msg;
        }

        _currentDecider = candidates[_rotation++ % candidates.Count];
        Log.Info("Opponent decider -> " + _currentDecider.DisplayName + " (" + reason + ")");
        Audience.Announce("@" + _currentDecider.Login + " spielt jetzt fuer " + TeamName() + " gegen den Streamer! (" + reason + ")");
        return "now " + _currentDecider.DisplayName;
    }

    /// <summary>Picks a specific viewer (streamer or moderator choice).</summary>
    public string PickViewer(string login)
    {
        var clean = login.Trim().TrimStart('@');
        if (clean.Length == 0) return "no viewer given";
        _currentDecider = FindViewer(clean) ?? new Viewer("login:" + clean.ToLowerInvariant(), clean.ToLowerInvariant(), clean, Authority.Viewer, "twitch");
        _strikes = 0;
        if (_mode.Value != OpponentMode.SelectedViewer) _mode.Value = OpponentMode.SelectedViewer;
        Audience.Announce("@" + _currentDecider.Login + " spielt jetzt fuer " + TeamName() + " gegen den Streamer!");
        return "@" + _currentDecider.Login + " plays for the chat";
    }

    public OpponentMode Mode
    {
        get => _mode.Value;
        set => _mode.Value = value;
    }

    /// <summary>The decider did not decide in time: count a strike, retarget when the limit is reached.</summary>
    private void NoteDeciderSilence(Viewer? decider)
    {
        if (decider == null || _afkRetarget.Value == RetargetSource.Off) return;
        _strikes++;
        Log.Info("@" + decider.Login + " did not decide (" + _strikes + "/" + _afkStrikes.Value + ")");
        if (_strikes >= Math.Max(1, _afkStrikes.Value)) Retarget("@" + decider.Login + " ist AFK");
    }

    private void ResolveVote(DecisionRequest request, IOpponentBehavior behavior, InteractionResult result)
    {
        var interaction = _voteByDecision.TryGetValue(request.Id, out var i) ? i as VoteInteraction : null;
        VoteSummary? summary = null;
        if (interaction != null)
        {
            summary = new VoteSummary(Enumerable.Range(0, interaction.Choices.Count).Select(interaction.WeightFor).ToList(), interaction.Participants);
        }

        var index = result.Reason == ResultReason.Override ? result.WinningIndex : behavior.Choose(request, summary, Opponent, _random);
        if (summary == null || summary.Participants == 0)
        {
            Opponent.Statistics.NoVotes++;
            if (result.Reason != ResultReason.Override) NoteDeciderSilence(interaction?.Options.AllowedUserIds != null ? _currentDecider : null);
        }
        else
        {
            _strikes = 0;
        }
        Resolve(request, index, result, result.Reason == ResultReason.Override ? "override by " + result.DecidedBy : behavior.Kind + " (" + (summary?.Participants ?? 0) + " votes)");
    }

    private void ResolveEstimate(DecisionRequest request, IOpponentBehavior behavior, InteractionResult result)
    {
        var value = behavior.Estimate(request, result.Number, Opponent, _random);
        if (result.Number == null && result.Reason != ResultReason.Override && _voteByDecision.TryGetValue(request.Id, out var est) && est.Options.AllowedUserIds != null) NoteDeciderSilence(_currentDecider);
        if (!request.IsPending) return;
        if (value == null)
        {
            Log.Info("No estimate from chat; the game's bot logic decides");
            return;
        }

        if (!Audience.Gate.Allow("estimate " + value + " for team " + request.Team)) return;
        request.TryResolveNumber(value.Value, "audience");
        Audience.Announce(TeamName() + " schaetzt " + value.Value);
    }

    private void Resolve(DecisionRequest request, int index, InteractionResult? result, string how)
    {
        if (!request.IsPending) return;
        if (index < 0 || index >= request.Options.Count)
        {
            // No usable audience decision: apply the request's fallback now instead of letting the game
            // wait for the adapter deadline (up to 90 s on normal questions).
            if (request.FallbackIndex < 0 || request.FallbackIndex >= request.Options.Count)
            {
                Log.Info("No usable decision (" + how + "); the game's own logic decides at the deadline");
                return;
            }

            index = request.FallbackIndex;
            how += ", no votes: fallback";
        }

        if (!Audience.Gate.Allow("decide " + request.Kind + " = " + request.Options[index].Key + " for team " + request.Team)) return;
        request.TryResolve(index, "audience: " + how);
        var option = request.Options[index];
        Audience.Announce(TeamName() + " waehlt " + option.Key + (request.Kind == DecisionKind.Category ? " (" + option.Label + ")" : string.Empty) + (result != null && result.Lines.Count > 0 ? " - " + VotingFeature.Breakdown(result.Lines) : string.Empty));
    }

    private void OnDecisionCompleted(DecisionRequest request)
    {
        if (!_voteByDecision.TryGetValue(request.Id, out var interaction)) return;
        _voteByDecision.Remove(request.Id);
        if (request.Status == DecisionStatus.TimedOut)
        {
            Opponent.Statistics.Timeouts++;
            if (interaction.IsActive) Audience.Engine.Cancel(interaction, "game deadline reached");
        }
    }

    private void OnQuestionFinished(QuestionFinished e)
    {
        if (_team == null || e.Team != _team) return;
        if (e.Correct) Opponent.Statistics.Correct++;
        else Opponent.Statistics.Wrong++;
    }

    private void OnGameFinished(GameFinished e)
    {
        if (_team == null) return;
        Log.Info("Show over. Audience opponent: " + Opponent.Statistics);
        Audience.Announce(e.Winner == _team ? "Der Chat hat gewonnen! GG" : e.Winner == null ? "Kein Sieger." : "Der Chat hat verloren. Naechstes Mal!");
    }

    private string TeamName() => _teamLabel.Value.Length > 0 ? _teamLabel.Value : "Team " + _team;

    private static string Describe(DecisionRequest r) => r.Kind switch
    {
        DecisionKind.Answer => "Antwort?",
        DecisionKind.MasterAnswer => "Finalfrage!",
        DecisionKind.Bet => "Einsatz?",
        DecisionKind.Estimate => "Schaetzfrage!",
        DecisionKind.Category => "Kategorie?",
        _ => r.Kind.ToString(),
    };

    private static string Hint(DecisionRequest r) => r.Kind == DecisionKind.Estimate
        ? "!vote <Zahl>"
        : "!vote " + string.Join("/", r.Options.Select(o => o.Key).ToArray()) + (r.Kind == DecisionKind.Category || r.Kind == DecisionKind.Bet ? " | " + string.Join(", ", r.Options.Select(o => o.Key + "=" + o.Label).ToArray()) : string.Empty);

    private void RegisterCommands()
    {
        Track(Context.Commands.Register(new CommandDefinition("crowd", Authority.Moderator, "Enable Twitch Crowd mode", (_, _) =>
        {
            _mode.Value = OpponentMode.Crowd;
            return new CommandResult(CommandOutcome.Handled, SetActive(true));
        })));
        Track(Context.Commands.Register(new CommandDefinition("join", Authority.Viewer, "Volunteer as decider", (_, who) =>
        {
            if (_volunteers.All(v => v.Id != who.UserId)) _volunteers.Add(new Viewer(who.UserId, who.Login, who.DisplayName, who.Authority, who.Source));
            return new CommandResult(CommandOutcome.Handled);
        }) { UserCooldown = 30 }));
        Track(Context.Commands.Register(new CommandDefinition("opponent", Authority.Moderator, "!opponent on|off|mode <m>|pick <login>|status", (cmd, _) =>
        {
            switch (cmd.Arg(0).ToLowerInvariant())
            {
                case "on":
                    return new CommandResult(CommandOutcome.Handled, SetActive(true));
                case "off":
                    return new CommandResult(CommandOutcome.Handled, SetActive(false));
                case "mode" when Enum.IsDefined(typeof(OpponentMode), Capitalize(cmd.Arg(1))):
                    _mode.Value = (OpponentMode)Enum.Parse(typeof(OpponentMode), Capitalize(cmd.Arg(1)));
                    return new CommandResult(CommandOutcome.Handled, "Mode " + _mode.Value);
                case "pick" when cmd.Arg(1).Length > 0:
                    return new CommandResult(CommandOutcome.Handled, PickViewer(cmd.Arg(1)));
                case "next":
                    return new CommandResult(CommandOutcome.Handled, Retarget("vom Moderator gewechselt"));
                default:
                    return new CommandResult(CommandOutcome.Handled, "Opponent " + (_team.HasValue ? "controls team " + _team : "inactive") + ", mode " + _mode.Value + ", " + Opponent.Statistics + ", volunteers " + _volunteers.Count);
            }
        })));
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

    protected override void OnStop()
    {
        if (_team.HasValue) Context.Adapter?.SetExternalControl(_team.Value, false);
        _team = null;
    }
}
