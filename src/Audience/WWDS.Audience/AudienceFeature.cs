using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WWDS.Audience.Bindings;
using WWDS.Audience.Engine;
using WWDS.Audience.Guessing;
using WWDS.Audience.Moderation;
using WWDS.Audience.Naming;
using WWDS.Audience.Voting;
using WWDS.ModCore.Adapters;
using WWDS.ModCore.Commands;
using WWDS.ModCore.Config;
using WWDS.ModCore.Events;
using WWDS.ModCore.Features;
using WWDS.ModCore.Logging;
using WWDS.ModCore.Model;

namespace WWDS.Audience;

/// <summary>
/// Shared audience infrastructure for all audience features: the interaction engine, content
/// moderation, chat commands (!vote, !name, !guess) and moderator/broadcaster controls (!wwds ...).
/// Other features obtain it with <c>Context.Get&lt;AudienceService&gt;()</c>.
/// </summary>
public sealed class AudienceFeature : FeatureBase
{
    public const string FeatureId = "Audience";

    private AudienceService _service = null!;
    private IConfigValue<string> _voteCommand = null!;
    private IConfigValue<string> _nameCommand = null!;
    private IConfigValue<string> _guessCommand = null!;
    private IConfigValue<string> _controlCommand = null!;
    private IConfigValue<bool> _bareLetters = null!;
    private IConfigValue<bool> _moderatorOverride = null!;

    public override string Id => FeatureId;

    public override string DisplayName => "Audience engine";

    public override string Description => "Interaction engine, moderation and chat commands shared by voting, naming, guessing and the Twitch opponent.";

    protected override string LogCategoryName => LogCategory.Audience;

    protected override void OnStart()
    {
        var c = Context.Config;
        const string s = "Audience";
        _voteCommand = c.Bind(s, "VoteCommand", "vote,answer", "Chat command(s) for votes, comma separated (first is primary).");
        _nameCommand = c.Bind(s, "NameCommand", "name", "Chat command for name suggestions.");
        _guessCommand = c.Bind(s, "GuessCommand", "guess", "Chat command for guesses.");
        _controlCommand = c.Bind(s, "ControlCommand", "wwds", "Moderator command: !wwds cancel|lock|reopen [s]|clear|extend <s>|force <choice>|approve <n>|ban <n>|pick <n>|off|on|status.");
        _bareLetters = c.Bind(s, "AcceptBareVotes", true, "During a vote, a chat message that is only an option key (\"A\", \"b\") counts as a vote.");
        _moderatorOverride = c.Bind(s, "ModeratorOverride", true, "Moderators (and trusted users) may use the control command; the broadcaster always may.");
        var spamMax = c.Bind(s, "SpamMaxPerWindow", 8, "Max submissions per viewer per window (moderators exempt).");
        var spamWindow = c.Bind(s, "SpamWindowSeconds", 10f, "Flood window in seconds.");
        var subWeight = c.Bind(s, "SubscriberVoteWeight", 1f, "Vote weight of subscribers (1 = same as everyone).");

        var rules = new ModerationRules
        {
            MinLength = c.Bind(s, "NameMinLength", 2, "Minimum name length.").Value,
            MaxLength = c.Bind(s, "NameMaxLength", 14, "Maximum name length (the game's own name field allows 14).").Value,
            MaxRepeatedChars = c.Bind(s, "NameMaxRepeatedChars", 3, "Longest run of one character.").Value,
            AllowedSymbols = c.Bind(s, "NameAllowedSymbols", " -_.'!?&", "Characters allowed besides letters and digits.").Value,
            TitleCase = c.Bind(s, "NameTitleCase", false, "Capitalize each word of chosen names.").Value,
            Denylist = LoadDenylist(c.Bind(s, "Denylist", "", "Comma separated blocked words (also read from BepInEx/wwds/denylist.txt, one per line).").Value),
        };

        var engine = new InteractionEngine(Context.Bus.Publish, Context.Clock, Log);
        engine.Spam.MaxPerWindow = spamMax.Value;
        engine.Spam.WindowSeconds = spamWindow.Value;
        _service = new AudienceService(Context, engine, new ContentModerator(rules), Log)
        {
            SubscriberWeight = subWeight.Value,
            NumberAggregation = c.Bind(s, "NumberAggregation", Voting.NumberAggregation.Average, "How numeric chat votes combine: Average | Median | TrimmedAverage.").Value,
        };
        Context.Provide(_service);
        var actions = Context.Get<BindingActions>() ?? new BindingActions();
        Context.Provide(actions);
        Track(actions.Register("audience.pause", (_, _) => { _service.Paused = true; return ActionResult.Success("paused"); }));
        Track(actions.Register("audience.resume", (_, _) => { _service.Paused = false; return ActionResult.Success("resumed"); }));

        RegisterCommands();
        Track(Context.Bus.Subscribe<ChatMessageReceived>(OnChat));
        Log.Info("Audience engine ready (" + rules.Denylist.Count + " denylist entries)");
    }

    private List<string> LoadDenylist(string inline)
    {
        var list = ConfigList.Parse(inline);
        var dir = Context.Host?.DataDirectory;
        if (dir == null) return list;
        var file = Path.Combine(dir, "denylist.txt");
        try
        {
            if (File.Exists(file))
            {
                foreach (var line in File.ReadAllLines(file))
                {
                    var t = line.Trim();
                    if (t.Length > 0 && !t.StartsWith("#", StringComparison.Ordinal)) list.Add(t);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("denylist.txt unreadable: " + ex.Message);
        }

        return list;
    }

    public override void Tick() => _service.Engine.Tick();

    private void RegisterCommands()
    {
        var votes = ConfigList.Parse(_voteCommand.Value);
        if (votes.Count > 0)
        {
            var def = new CommandDefinition(votes[0], Authority.Viewer, "Vote for an option", (cmd, who) => Submit("vote", cmd.RawArgs, who));
            foreach (var alias in votes.Skip(1)) def.Aliases.Add(alias);
            Track(Context.Commands.Register(def));
        }

        Track(Context.Commands.Register(new CommandDefinition(_nameCommand.Value, Authority.Viewer, "Suggest a name", (cmd, who) => Submit("name", cmd.RawArgs, who))));
        Track(Context.Commands.Register(new CommandDefinition(_guessCommand.Value, Authority.Viewer, "Submit a guess", (cmd, who) => Submit("guess", cmd.RawArgs, who))));
        Track(Context.Commands.Register(new CommandDefinition(_controlCommand.Value, Authority.Viewer, "Moderator controls", Control)));
    }

    private CommandResult Submit(string kind, string text, Invoker who)
    {
        if (_service.Paused) return new CommandResult(CommandOutcome.Handled);
        var viewer = new Viewer(who.UserId, who.Login, who.DisplayName, who.Authority, who.Source);
        var r = _service.Engine.Submit(kind, viewer, text);

        // "!vote X" is the universal command: with no vote open it answers the running guess round or
        // naming round (numbers support existing name entries).
        if (kind == "vote" && r.Status == SubmissionStatus.Closed)
        {
            foreach (var other in new[] { "guess", "name" })
            {
                r = _service.Engine.Submit(other, viewer, text);
                if (r.Status != SubmissionStatus.Closed) break;
            }
        }

        Log.Debug(kind + " from " + who.DisplayName + ": " + r.Status + (r.Detail.Length > 0 ? " (" + r.Detail + ")" : string.Empty));
        return new CommandResult(CommandOutcome.Handled);
    }

    private void OnChat(ChatMessageReceived e)
    {
        if (!_bareLetters.Value || _service.Paused) return;
        var vote = _service.Engine.Current("vote") as VoteInteraction;
        if (vote == null || vote.State != InteractionState.Collecting) return;
        var text = e.Text.Trim();
        if (text.Length == 0 || text.Length > 2) return;
        if (vote.ParseChoice(text) >= 0) _service.Engine.Submit("vote", e.Viewer, text);
    }

    private CommandResult Control(ParsedCommand cmd, Invoker who)
    {
        var allowed = who.Authority >= Authority.Broadcaster || (_moderatorOverride.Value && who.Authority >= Authority.Moderator);
        if (!allowed) return new CommandResult(CommandOutcome.Denied);
        var verb = cmd.Arg(0).ToLowerInvariant();
        var by = new Viewer(who.UserId, who.Login, who.DisplayName, who.Authority, who.Source);
        var current = _service.Engine.Active.LastOrDefault(i => i.IsActive);
        string reply;
        switch (verb)
        {
            case "off":
                _service.Paused = true;
                foreach (var i in _service.Engine.Active.Where(i => i.IsActive).ToList()) _service.Engine.Cancel(i, "audience mode disabled by " + who.DisplayName);
                reply = "Audience mode OFF";
                break;
            case "on":
                _service.Paused = false;
                reply = "Audience mode ON";
                break;
            case "status":
                reply = current == null ? "No interaction running" + (_service.Paused ? " (audience mode off)" : string.Empty) : current.Title + " " + current.State + ": " + string.Join(" | ", current.TallyLines().ToArray());
                break;
            default:
                if (current == null) return new CommandResult(CommandOutcome.Handled, "No interaction running");
                reply = Apply(verb, cmd, current, by) ?? "usage: !" + cmd.Name + " cancel|lock|reopen|clear|extend <s>|force <choice>|approve <n>|ban <n>|pick <n>|off|on|status";
                break;
        }

        Log.Info("!" + cmd.Name + " " + verb + " by " + who.DisplayName + ": " + reply);
        return new CommandResult(CommandOutcome.Handled, reply);
    }

    private string? Apply(string verb, ParsedCommand cmd, Interaction current, Viewer by)
    {
        var engine = _service.Engine;
        switch (verb)
        {
            case "cancel":
                return engine.Cancel(current, "cancelled by " + by.DisplayName) ? "Cancelled" : "Cannot cancel";
            case "lock":
                return engine.Lock(current, "locked by " + by.DisplayName) ? "Locked" : "Not collecting";
            case "reopen":
                return engine.Reopen(current, ParseSeconds(cmd.Arg(1), current.Options.Duration)) ? "Reopened" : "Cannot reopen";
            case "clear":
                return engine.Clear(current) ? "Cleared" : "Cannot clear";
            case "extend":
                return engine.Extend(current, ParseSeconds(cmd.Arg(1), 15)) ? "Extended" : "Not collecting";
            case "force":
            case "pick":
                var choice = string.Join(" ", cmd.Args.Skip(1).ToArray());
                return engine.Force(current, choice, by) ? "Decided: " + choice : "Unknown choice '" + choice + "'";
            case "approve":
                return current is NamingInteraction n1 && n1.Approve(cmd.Arg(1)) ? "Approved " + cmd.Arg(1) : "Unknown entry";
            case "ban":
                return current is NamingInteraction n2 && n2.Ban(string.Join(" ", cmd.Args.Skip(1).ToArray())) ? "Removed " + cmd.Arg(1) : "Unknown entry";
            default:
                return null;
        }
    }

    private static double ParseSeconds(string text, double fallback) =>
        double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) && s > 0 && s < 3600 ? s : fallback;

    protected override void OnStop()
    {
        foreach (var i in _service.Engine.Active.Where(i => i.IsActive).ToList()) _service.Engine.Cancel(i, "audience engine stopped");
    }
}

/// <summary>API other features use to run audience interactions.</summary>
public sealed class AudienceService
{
    private readonly WWDS.ModCore.ModContext _ctx;
    private readonly ModLog _log;

    internal AudienceService(WWDS.ModCore.ModContext ctx, InteractionEngine engine, ContentModerator moderator, ModLog log)
    {
        _ctx = ctx;
        Engine = engine;
        Moderator = moderator;
        _log = log;
    }

    public InteractionEngine Engine { get; }

    public ContentModerator Moderator { get; }

    /// <summary>Set by "!wwds off": submissions are ignored and features must not open interactions.</summary>
    public bool Paused { get; set; }

    public double SubscriberWeight { get; set; } = 1;

    public Voting.NumberAggregation NumberAggregation { get; set; } = Voting.NumberAggregation.Average;

    /// <summary>The real-Twitch test-mode gate (never null).</summary>
    public GameActionGate Gate => _ctx.Get<GameActionGate>() ?? new GameActionGate(_log);

    public double VoteWeight(Viewer v) => v.Authority == Authority.Subscriber || v.Authority == Authority.Vip ? SubscriberWeight : 1;

    public VoteInteraction? OpenVote(InteractionOptions options, IList<VoteOption> choices, TieBreak tieBreak, int fallback, Action<InteractionResult> onApply, bool ignoreCooldown = false)
    {
        if (Paused) return null;
        var vote = new VoteInteraction(options, choices, tieBreak, fallback, VoteWeight) { OnApply = onApply };
        return Engine.Open(vote, ignoreCooldown) ? vote : null;
    }

    public NamingInteraction? OpenNaming(InteractionOptions options, NameSelection selection, bool requireApproval, string target, Action<InteractionResult> onApply)
    {
        if (Paused) return null;
        var naming = new NamingInteraction(options, Moderator, selection, requireApproval) { Target = target, OnApply = onApply };
        return Engine.Open(naming) ? naming : null;
    }

    public GuessInteraction? OpenGuess(InteractionOptions options, GuessKind kind, IList<string>? optionKeys, string subject, Action<InteractionResult> onApply, IList<string>? optionLabels = null)
    {
        if (Paused) return null;
        var guess = new GuessInteraction(options, kind, optionKeys) { Subject = subject, OnApply = onApply, OptionLabels = optionLabels ?? new List<string>() };
        return Engine.Open(guess) ? guess : null;
    }

    /// <summary>Posts in chat through the Twitch feature (rate limited, test-mode aware).</summary>
    public void Announce(string text) => _ctx.Bus.Publish(new ChatSendRequested(text));
}
