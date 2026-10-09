using System;
using WWDS.ModCore.Adapters;
using WWDS.ModCore.Capabilities;
using WWDS.ModCore.Commands;
using WWDS.ModCore.Config;
using WWDS.ModCore.Events;
using WWDS.ModCore.Features;
using WWDS.ModCore.Model;

namespace WWDS.Features.Debug;

/// <summary>
/// Test actions for modders and for unattended runtime validation: start a show programmatically,
/// hand teams to "external control", resolve their decisions automatically, assist humans, quit.
/// Everything is off by default. Also registers local console commands (wwds.*).
/// </summary>
public sealed class DebugToolsFeature : FeatureBase
{
    private IConfigValue<bool> _autoStart = null!;
    private IConfigValue<int> _showId = null!;
    private IConfigValue<bool> _teamAHuman = null!;
    private IConfigValue<string> _teamAName = null!;
    private IConfigValue<string> _teamBName = null!;
    private IConfigValue<string> _externalTeams = null!;
    private IConfigValue<string> _autoResolve = null!;
    private IConfigValue<float> _autoResolveDelay = null!;
    private IConfigValue<int> _betOption = null!;
    private IConfigValue<string> _assistHumans = null!;
    private IConfigValue<int> _quitAfterQuestions = null!;
    private IConfigValue<bool> _quitAfterShow = null!;
    private IConfigValue<string> _teamBLabel = null!;
    private int _questionsFinished;
    private bool _startRequested;

    public override string Id => "DebugTools";

    public override string DisplayName => "Debug tools";

    public override string Description => "Test actions: programmatic shows, automatic decisions, auto-quit. Off unless configured.";

    protected override string LogCategoryName => "Debug";

    protected override void OnStart()
    {
        var c = Context.Config;
        const string s = "Debug";
        _autoStart = c.Bind(s, "AutoStartShow", false, "Start a show from the main menu automatically (saves and achievements suppressed).");
        _showId = c.Bind(s, "ShowId", -1, "Show slot 1..30 for AutoStartShow, -1 = next unplayed.");
        _teamAHuman = c.Bind(s, "TeamAHuman", false, "Team A is a local human on the active input device instead of a bot.");
        _teamAName = c.Bind(s, "TeamABotName", "", "Name for team A's contestant.");
        _teamBName = c.Bind(s, "TeamBBotName", "", "Name for team B's contestant.");
        _teamBLabel = c.Bind(s, "TeamBLabel", "", "Custom team label for team B (empty = game default).");
        _externalTeams = c.Bind(s, "ExternalControlTeams", "", "Teams whose bot decisions the harness makes: '', 'A', 'B' or 'AB'.");
        _autoResolve = c.Bind(s, "AutoResolve", "Off", "Off | Correct | Wrong | First | Random: resolve decision requests automatically.");
        _autoResolveDelay = c.Bind(s, "AutoResolveDelay", 3f, "Seconds before an automatic resolution.");
        _betOption = c.Bind(s, "AutoBetOption", 4, "Bet option index used by AutoResolve.");
        _assistHumans = c.Bind(s, "AutoAssistHumans", "Off", "Off | Correct | Wrong: answer and pick categories for humans (tests SubmitAnswer/SelectCategory).");
        _quitAfterQuestions = c.Bind(s, "QuitAfterQuestions", 0, "Quit the game after N finished questions (0 = off).");
        _quitAfterShow = c.Bind(s, "QuitAfterShow", false, "Quit the game 15 s after a show finished.");

        Track(Context.Bus.Subscribe<GameLoaded>(_ => OnGameLoaded()));
        Track(Context.Bus.Subscribe<SessionCreated>(_ => OnSessionCreated()));
        Track(Context.Bus.Subscribe<DecisionRequested>(e => OnDecision(e.Request)));
        Track(Context.Bus.Subscribe<AnswersPresented>(OnAnswersPresented));
        Track(Context.Bus.Subscribe<RoundStarted>(OnRoundStarted));
        Track(Context.Bus.Subscribe<QuestionFinished>(OnQuestionFinished));
        Track(Context.Bus.Subscribe<GameFinished>(OnGameFinished));
        RegisterCommands();
    }

    private IGameAdapter? Adapter => Context.Adapter;

    private void OnGameLoaded()
    {
        if (!_autoStart.Value || _startRequested || Adapter == null) return;
        _startRequested = true;
        if (Context.Host != null) Context.Host.RunInBackground = true;
        Context.Scheduler.After(8, () =>
        {
            var setup = new ShowSetup
            {
                ShowId = _showId.Value,
                TeamAIsBot = !_teamAHuman.Value,
                TeamBIsBot = true,
                TeamABotName = _teamAName.Value,
                TeamBBotName = _teamBName.Value,
                SuppressPersistence = true,
            };
            Log.Info("AutoStartShow: " + Adapter!.StartShow(setup));
        });
    }

    private void OnSessionCreated()
    {
        if (Adapter == null) return;
        foreach (var slot in new[] { TeamSlot.A, TeamSlot.B })
        {
            if (_externalTeams.Value.ToUpperInvariant().Contains(slot.ToString())) Log.Info(Adapter.SetExternalControl(slot, true).ToString());
        }

        if (_teamBLabel.Value.Length > 0) Log.Info(Adapter.RenameTeam(TeamSlot.B, _teamBLabel.Value).ToString());
    }

    private void OnDecision(DecisionRequest request)
    {
        var mode = _autoResolve.Value;
        if (string.Equals(mode, "Off", StringComparison.OrdinalIgnoreCase)) return;
        Context.Scheduler.After(_autoResolveDelay.Value, () =>
        {
            if (!request.IsPending) return;
            bool ok;
            if (request.Kind == DecisionKind.Estimate)
            {
                var target = request.Question?.NumericAnswer ?? 100;
                ok = request.TryResolveNumber(mode.Equals("Wrong", StringComparison.OrdinalIgnoreCase) ? target * 3 : target, "debug:" + mode);
            }
            else
            {
                ok = request.TryResolve(Pick(request, mode), "debug:" + mode);
            }

            Log.Info("AutoResolve " + request + " ok=" + ok);
        });
    }

    private int Pick(DecisionRequest r, string mode)
    {
        var count = r.Options.Count;
        if (r.Kind == DecisionKind.Bet) return Math.Max(0, Math.Min(_betOption.Value, count - 1));
        if (r.Kind == DecisionKind.Category || mode.Equals("First", StringComparison.OrdinalIgnoreCase)) return 0;
        if (mode.Equals("Random", StringComparison.OrdinalIgnoreCase)) return new Random().Next(count);
        var correct = r.Question?.CorrectIndex ?? 0;
        if (correct < 0) correct = 0;
        return mode.Equals("Wrong", StringComparison.OrdinalIgnoreCase) ? (correct + 1) % count : correct;
    }

    private void OnAnswersPresented(AnswersPresented e)
    {
        var mode = _assistHumans.Value;
        if (mode.Equals("Off", StringComparison.OrdinalIgnoreCase) || Adapter == null) return;
        if (e.Answering == null || e.Answering.Kind != ContestantKind.Human || e.Question.Kind != QuestionKind.Normal) return;
        var correct = e.Question.CorrectIndex;
        var index = mode.Equals("Wrong", StringComparison.OrdinalIgnoreCase) ? (correct + 1) % 3 : correct;
        var team = e.Answering.Team;
        Context.Scheduler.After(3, () => Log.Info("AutoAssist answer: " + Adapter!.SubmitAnswer(team, index)));
    }

    private void OnRoundStarted(RoundStarted e)
    {
        if (_assistHumans.Value.Equals("Off", StringComparison.OrdinalIgnoreCase) || Adapter == null) return;
        if (e.Kind != QuestionKind.Normal || e.Contestant == null || e.Contestant.Kind != ContestantKind.Human) return;
        var team = e.Contestant.Team;
        Context.Scheduler.After(4, () =>
        {
            var open = Adapter!.GetOpenCategories();
            if (open.Count > 0) Log.Info("AutoAssist category: " + Adapter.SelectCategory(team, open[0].Index));
        });
    }

    private void OnQuestionFinished(QuestionFinished e)
    {
        _questionsFinished++;
        if (_quitAfterQuestions.Value > 0 && _questionsFinished >= _quitAfterQuestions.Value) Quit("QuitAfterQuestions reached");
    }

    private void OnGameFinished(GameFinished e)
    {
        Log.Info("Show finished: winner " + (e.Winner?.ToString() ?? "none") + " " + e.ScoreA + ":" + e.ScoreB + (e.Aborted ? " (aborted)" : string.Empty));
        if (_quitAfterShow.Value) Context.Scheduler.After(15, () => Quit("QuitAfterShow"));
    }

    private void Quit(string reason)
    {
        Log.Info("Quitting game: " + reason);
        Context.Host?.QuitGame();
    }

    private void RegisterCommands()
    {
        Track(Context.Commands.Register(new CommandDefinition("wwds.caps", Authority.LocalOperator, "List adapter capabilities",
            (_, _) => new CommandResult(CommandOutcome.Handled, Context.Capabilities.Describe()))));
        Track(Context.Commands.Register(new CommandDefinition("wwds.state", Authority.LocalOperator, "Show the game snapshot",
            (_, _) =>
            {
                var s = Adapter?.GetSnapshot() ?? GameSnapshot.Empty;
                return new CommandResult(CommandOutcome.Handled, s.Phase + " / " + s.RawState + " / " + (s.Question?.Text ?? "-") + " / " + (s.TeamA?.Score ?? 0) + ":" + (s.TeamB?.Score ?? 0));
            })));
        Track(Context.Commands.Register(new CommandDefinition("wwds.control", Authority.LocalOperator, "wwds.control A|B on|off",
            (cmd, _) =>
            {
                if (Adapter == null || !TeamSlotExtensions.TryParse(cmd.Arg(0), out var slot)) return new CommandResult(CommandOutcome.Failed, "usage: wwds.control A|B on|off");
                return new CommandResult(CommandOutcome.Handled, Adapter.SetExternalControl(slot, cmd.Arg(1) != "off").ToString());
            })));
        Track(Context.Commands.Register(new CommandDefinition("wwds.startshow", Authority.LocalOperator, "Start a bot-vs-bot test show",
            (_, _) => new CommandResult(CommandOutcome.Handled, Adapter?.StartShow(new ShowSetup()).ToString() ?? "no adapter"))));
    }

    public override GameCapability RequiredCapabilities => GameCapability.None;
}
