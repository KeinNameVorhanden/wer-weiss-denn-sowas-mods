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

namespace WWDS.Audience.Naming;

public enum NamingAutoOpen
{
    Off,

    /// <summary>When a show starts with a bot contestant, chat names the bot.</summary>
    BotContestant,

    /// <summary>When a show starts with a bot contestant, chat names the bot's team label.</summary>
    BotTeam,
}

/// <summary>
/// Community naming: chat suggests names with "!name ...", suggestions are moderated and merged, and
/// the chosen one is applied to a naming target. Targets: "contestant:A0" (team + member index),
/// "team:B" (team label), "opponent" (the bot contestant), "opponent-team" (the bot's team label).
/// </summary>
public sealed class NamingFeature : FeatureBase
{
    public const string FeatureId = "Naming";

    private IConfigValue<NamingAutoOpen> _autoOpen = null!;
    private IConfigValue<float> _duration = null!;
    private IConfigValue<int> _minSubmissions = null!;
    private IConfigValue<NameSelection> _selection = null!;
    private IConfigValue<bool> _approval = null!;
    private IConfigValue<bool> _allowChanges = null!;

    public override string Id => FeatureId;

    public override string DisplayName => "Community naming";

    public override string Description => "Chat suggests and selects names for contestants and teams.";

    public override IEnumerable<string> Dependencies => new[] { AudienceFeature.FeatureId };

    public override GameCapability RequiredCapabilities => GameCapability.ReadContestants;

    protected override string LogCategoryName => LogCategory.Naming;

    private AudienceService Audience => Context.Get<AudienceService>()!;

    protected override void OnStart()
    {
        var c = Context.Config;
        const string s = "Naming";
        _autoOpen = c.Bind(s, "AutoOpen", NamingAutoOpen.Off, "Off | BotContestant | BotTeam: open a naming round when a show with a bot starts.");
        _duration = c.Bind(s, "NameSubmissionDuration", 40f, "Seconds chat may suggest names.");
        _minSubmissions = c.Bind(s, "MinimumSubmissions", 1, "Suggestions needed for a result.");
        _selection = c.Bind(s, "SelectionMode", NameSelection.MostSubmitted, "Random | MostSubmitted | Weighted | Moderator.");
        _approval = c.Bind(s, "ModeratorApproval", false, "Suggestions must be approved with '!wwds approve <n>' before they can win.");
        _allowChanges = c.Bind(s, "AllowChanges", true, "A viewer may replace their own suggestion.");

        Track(Context.Get<Bindings.BindingActions>()!.Register("naming.open", (args, _) =>
        {
            var target = args.TryGetValue("target", out var t) && t.Length > 0 ? t : "opponent";
            var seconds = args.TryGetValue("seconds", out var sec) && double.TryParse(sec, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : _duration.Value;
            return OpenNaming(target, seconds) ? ActionResult.Success("naming open for " + target) : ActionResult.Fail("could not open naming for " + target);
        }));
        Track(Context.Bus.Subscribe<SessionCreated>(OnSessionCreated));
        Track(Context.Commands.Register(new CommandDefinition("startname", Authority.Moderator, "!startname opponent|opponent-team|contestant:A0|team:B [seconds]", (cmd, _) =>
        {
            var target = cmd.Arg(0).Length > 0 ? cmd.Arg(0) : "opponent";
            var seconds = float.TryParse(cmd.Arg(1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sec) ? sec : _duration.Value;
            return new CommandResult(CommandOutcome.Handled, OpenNaming(target, seconds) ? "Naming open for " + target : "Could not open naming for " + target);
        })));
        Context.Provide(this);
    }

    private void OnSessionCreated(SessionCreated e)
    {
        if (_autoOpen.Value == NamingAutoOpen.Off) return;
        if (!e.Contestants.Any(x => x.Kind == ContestantKind.Bot)) return;

        // Let the game finish placing contestants before chat output starts.
        Context.Scheduler.After(1, () => OpenNaming(_autoOpen.Value == NamingAutoOpen.BotTeam ? "opponent-team" : "opponent", _duration.Value));
    }

    /// <summary>Opens a naming round for a target. Also used by bindings ("naming.open").</summary>
    public bool OpenNaming(string target, double seconds)
    {
        var resolved = Resolve(target);
        if (resolved == null)
        {
            Log.Warn("Unknown naming target '" + target + "' (no matching contestant)");
            return false;
        }

        var options = new InteractionOptions
        {
            Title = "Name für " + resolved.Label,
            Duration = Math.Max(5, seconds),
            MinParticipants = Math.Max(1, _minSubmissions.Value),
            AllowChanges = _allowChanges.Value,
            DecisionTimeout = 30,
        };
        var naming = Audience.OpenNaming(options, _selection.Value, _approval.Value, target, r => Apply(target, r));
        if (naming == null) return false;
        Audience.Announce("Wie soll " + resolved.Label + " heissen? Vorschlaege mit !name <Name> (" + (int)options.Duration + " s)");
        return true;
    }

    private NamingTarget? Resolve(string target)
    {
        var snap = Context.Adapter?.GetSnapshot();
        var t = target.Trim().ToLowerInvariant();
        if (t == "opponent" || t == "opponent-team")
        {
            foreach (var team in new[] { snap?.TeamB, snap?.TeamA })
            {
                var bot = team?.Members.FirstOrDefault(m => m.Kind == ContestantKind.Bot);
                if (bot != null) return new NamingTarget(team!.Slot, team.Members.IndexOf(bot), t == "opponent-team", t == "opponent" ? "den Gegner" : "das Gegner-Team");
            }

            return null;
        }

        if (t.StartsWith("team:", StringComparison.Ordinal) && TeamSlotExtensions.TryParse(t.Substring(5), out var slot)) return new NamingTarget(slot, 0, true, "Team " + slot);
        if (t.StartsWith("contestant:", StringComparison.Ordinal) && t.Length >= 13 && TeamSlotExtensions.TryParse(t.Substring(11, 1), out var cs)
            && int.TryParse(t.Substring(12), out var member))
        {
            var team = snap?.Team(cs);
            if (team == null || member < 0 || member >= team.Members.Count) return null;
            return new NamingTarget(cs, member, false, team.Members[member].Name);
        }

        return null;
    }

    private void Apply(string target, InteractionResult result)
    {
        if (!result.Success || result.WinningText == null)
        {
            Audience.Announce("Kein Name gewaehlt (" + result.Summary + ")");
            return;
        }

        var resolved = Resolve(target);
        var adapter = Context.Adapter;
        if (resolved == null || adapter == null) return;
        if (!Audience.Gate.Allow("rename " + target + " to '" + result.WinningText + "'")) return;

        var r = resolved;
        var outcome = r.TeamLabel ? adapter.RenameTeam(r.Team, result.WinningText) : adapter.RenameContestant(r.Team, r.Member, result.WinningText);
        Log.Info("Apply name: " + outcome);
        if (outcome.Ok) Audience.Announce("Neuer Name: " + result.WinningText + (result.Winners.Count > 0 ? " (Vorschlag von " + result.Winners[0].DisplayName + ")" : string.Empty));
    }
}

internal sealed class NamingTarget
{
    public NamingTarget(TeamSlot team, int member, bool teamLabel, string label)
    {
        Team = team;
        Member = member;
        TeamLabel = teamLabel;
        Label = label;
    }

    public TeamSlot Team { get; }

    public int Member { get; }

    public bool TeamLabel { get; }

    public string Label { get; }
}
