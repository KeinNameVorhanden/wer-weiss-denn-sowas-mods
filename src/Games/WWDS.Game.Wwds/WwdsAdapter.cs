using System;
using System.Collections.Generic;
using InControl;
using UnityEngine;
using WWDS.ModCore;
using WWDS.ModCore.Adapters;
using WWDS.ModCore.Capabilities;
using WWDS.ModCore.Config;
using WWDS.ModCore.Detection;
using WWDS.ModCore.Events;
using WWDS.ModCore.Logging;
using WWDS.ModCore.Model;
using WWDS.Runtime.Patching;

namespace WWDS.Game.Wwds;

/// <summary>
/// Shared adapter for "Wer weiß denn sowas?" 1 and 2 (identical engine code, see research report C).
/// Observes the game through isolated Harmony patches (<see cref="WwdsHooks"/>), publishes normalized
/// events, and lets the harness take over decisions of bot contestants (<see cref="WwdsDecisions"/>).
/// </summary>
public sealed class WwdsAdapter : IGameAdapter, IPatchStatusProvider
{
    public const string Version = WWDS.WwdsVersion.Value;
    public const int MaxNameLength = 24;

    private readonly GameProfile _profile;
    private readonly bool[] _external = new bool[2];
    private readonly string?[] _teamNames = new string?[2];
    private readonly Dictionary<int, string> _originalHumanNames = new();
    private readonly Dictionary<Team, int> _knownScores = new();
    private readonly List<DecisionRequest> _pending = new();

    private ModContext _ctx = null!;
    private PatchRegistry _patches = null!;
    private bool _gameLoaded;
    private string _state = "(none)";
    private GamePhase _phase = GamePhase.Unknown;
    private ShowSetup? _pendingShow;
    private double _pendingShowSince;

    public WwdsAdapter(GameProfile profile)
    {
        _profile = profile;
    }

    internal static WwdsAdapter? Current { get; private set; }

    public GameId Game => _profile.Id;

    public string DisplayName => _profile.DisplayName;

    public string AdapterVersion => Version;

    public CapabilitySet Capabilities { get; } = new();

    public IList<PatchSpec> Patches => _patches.Patches;

    public IList<PatchInfo> GetPatchStatus()
    {
        var list = new List<PatchInfo>();
        foreach (var p in _patches.Patches) list.Add(new PatchInfo(p.Id, p.TargetName ?? "?", p.Status.ToString(), p.Reason, p.Error));
        return list;
    }

    internal ModLog Log { get; private set; } = null!;

    internal WwdsMembers Members { get; private set; } = null!;

    internal ModContext Context => _ctx;

    internal bool SuppressPersistence { get; private set; }

    internal IConfigValue<float> AnswerTimeout { get; private set; } = null!;

    internal IConfigValue<float> MasterTimeout { get; private set; } = null!;

    internal IConfigValue<float> BetTimeout { get; private set; } = null!;

    internal IConfigValue<float> EstimateTimeout { get; private set; } = null!;

    internal IConfigValue<float> CategoryTimeout { get; private set; } = null!;

    public void Initialize(ModContext context)
    {
        _ctx = context;
        Log = context.Log.For(_profile.Id == GameId.Wwds2 ? LogCategory.Game2 : LogCategory.Game1);
        PatchGuard.Log = context.Log.For(LogCategory.GameAdapter);
        Members = new WwdsMembers();
        foreach (var m in Members.Binder.Missing) Log.Warn("Game member not found: " + m);

        const string s = "Adapter";
        AnswerTimeout = context.Config.Bind(s, "AnswerDecisionTimeout", 90f, "Seconds an externally controlled bot waits for a normal-question decision before it picks randomly (the game has no timer here).");
        MasterTimeout = context.Config.Bind(s, "MasterDecisionTimeout", 16f, "Seconds for a master-question decision. The game's clock is 20 s; keep a safety margin.");
        BetTimeout = context.Config.Bind(s, "BetDecisionTimeout", 30f, "Seconds for a master bet decision before the game's bot logic decides.");
        EstimateTimeout = context.Config.Bind(s, "EstimateDecisionTimeout", 45f, "Seconds for a bonus estimate decision before the game's bot logic decides.");
        CategoryTimeout = context.Config.Bind(s, "CategoryDecisionTimeout", 30f, "Seconds for a category decision before a random open category is picked.");

        Current = this;
        _patches = new PatchRegistry("wwds.adapter", context.Config, context.Log.For(LogCategory.GameAdapter));
        WwdsHooks.Register(_patches, Members);
        _patches.ApplyAll();
        ComputeCapabilities();
        Log.Info("Adapter " + Version + " ready for " + _profile.DisplayName + ". Capabilities:\n" + Capabilities.Describe());
    }

    private void ComputeCapabilities()
    {
        const GameCapability candidates =
            GameCapability.ReadPhase | GameCapability.ReadQuestion | GameCapability.ReadAnswers | GameCapability.ReadCorrectAnswer
            | GameCapability.ReadScore | GameCapability.ReadContestants | GameCapability.ObserveAnswers | GameCapability.RenameContestant
            | GameCapability.ControlBotAnswer | GameCapability.ControlBotMasterAnswer | GameCapability.ControlBotBet
            | GameCapability.ControlBotEstimate | GameCapability.ControlBotCategory | GameCapability.SubmitAnswerForHuman
            | GameCapability.StartShow | GameCapability.RenameTeam | GameCapability.SelectCategoryForHuman;
        _patches.ComputeCapabilities(Capabilities, candidates);

        var m = Members;
        DenyIfMissing(GameCapability.ControlBotAnswer, m.QuestionSelectedButton, m.QuestionAnswerButtons, m.QuestionActivePlayer);
        DenyIfMissing(GameCapability.ControlBotMasterAnswer, m.QuestionConfirmMaster);
        DenyIfMissing(GameCapability.ControlBotBet, m.BetUpdateButtonTexts, m.BetAnswerButtons, m.BetValues, m.BetConfirm, m.BetRandomIndex);
        DenyIfMissing(GameCapability.ControlBotEstimate, m.BonusTeamABet, m.BonusTeamBBet, m.BonusTeamAVoted, m.BonusTeamBVoted, m.BonusConfirm, m.BonusSetTeamBetText, m.BonusLockBet);
        DenyIfMissing(GameCapability.ControlBotCategory, m.CategorySelectedIndex, m.CategoryNavigation, m.CategoryButtons, m.CategoryHasNew, m.CategoryShowBig, m.CategoryIndexSetter);
        DenyIfMissing(GameCapability.SubmitAnswerForHuman, m.ShowViewQuestionUi, m.QuestionSelectedButton, m.QuestionAnswerButtons, m.QuestionActivePlayer, m.QuestionConfirmMaster);
        DenyIfMissing(GameCapability.SelectCategoryForHuman, m.ShowViewCategoryUi, m.ShowActivePlayer);
        DenyIfMissing(GameCapability.RenameTeam, m.FrameTeamNameText);

        Capabilities.Deny(GameCapability.SecondBotInTeam, "game creates at most one bot per show; a second bot is untested");
        Capabilities.Deny(GameCapability.ReplaceQuestions, "extension point only (custom packs need a playable answer video per question)");
    }

    private void DenyIfMissing(GameCapability cap, params object?[] members)
    {
        foreach (var member in members)
        {
            if (member == null)
            {
                Capabilities.Deny(cap, "game member missing (game update?)");
                return;
            }
        }
    }

    public void Tick()
    {
        var now = _ctx.Clock.Now;
        for (var i = _pending.Count - 1; i >= 0; i--)
        {
            var r = _pending[i];
            if (!r.IsPending)
            {
                _pending.RemoveAt(i);
                continue;
            }

            if (r.Deadline.HasValue && now >= r.Deadline.Value)
            {
                r.MarkTimedOut();
                Log.Info(r + " timed out, fallback " + r.FallbackIndex);
                _pending.RemoveAt(i);
            }
        }

        TickPendingShow(now);
    }

    public void Shutdown()
    {
        foreach (var r in _pending) r.Cancel("shutdown");
        _pending.Clear();
        _patches?.UnpatchAll();
        Current = null;
    }

    // ---- snapshot -------------------------------------------------------------------------------

    public GameSnapshot GetSnapshot()
    {
        var session = SDataAccess.ActiveSession;
        if (session == null) return new GameSnapshot(_phase, _state, null, null, null, null, null, false);

        QuestionInfo? question = null;
        try
        {
            if (session.CurrentShow != null && _phase != GamePhase.CategorySelection && _phase != GamePhase.ShowIntro && _phase != GamePhase.Loading)
            {
                question = WwdsMapping.Question(Game, session, session.ActiveQuestionIndex);
            }
        }
        catch (Exception ex)
        {
            Log.Debug("snapshot question failed: " + ex.Message);
        }

        var a = WwdsMapping.Team(session, TeamSlot.A, TeamName(TeamSlot.A), _external[0]);
        var b = WwdsMapping.Team(session, TeamSlot.B, TeamName(TeamSlot.B), _external[1]);
        var activeAgent = SDataAccess.ActiveController == null ? null : Members.ShowActivePlayer?.GetValue(SDataAccess.ActiveController) as APlayerAgent;
        var active = WwdsMapping.Contestant(session, activeAgent, _external);
        TeamSlot? activeTeam = session.TeamAActive ? TeamSlot.A : TeamSlot.B;
        return new GameSnapshot(_phase, _state, question, a, b, active?.Team ?? activeTeam, active, true);
    }

    internal string TeamName(TeamSlot slot) => _teamNames[(int)slot] ?? ShowFormat.DefaultTeamName(slot);

    internal string? CustomTeamName(bool isTeamA) => _teamNames[isTeamA ? 0 : 1];

    // ---- control API ---------------------------------------------------------------------------

    public bool IsExternallyControlled(TeamSlot team) => _external[(int)team];

    public ActionResult SetExternalControl(TeamSlot team, bool enabled)
    {
        if (enabled && !Capabilities.Has(GameCapability.ControlBotAnswer))
        {
            return ActionResult.Fail("ControlBotAnswer unavailable: " + Capabilities.ReasonDenied(GameCapability.ControlBotAnswer));
        }

        _external[(int)team] = enabled;
        if (!enabled)
        {
            foreach (var r in _pending.ToArray())
            {
                if (r.Team == team) r.Cancel("external control disabled");
            }
        }

        var t = WwdsMapping.TeamOf(SDataAccess.ActiveSession, team);
        var note = t != null && !t.IsNpcOnlyTeam() ? " (team has no bot now: applies only to bot contestants)" : string.Empty;
        Log.Info("External control team " + team + ": " + enabled + note);
        return ActionResult.Success("team " + team + " external control " + (enabled ? "on" : "off") + note);
    }

    public ActionResult RenameContestant(TeamSlot team, int memberIndex, string name)
    {
        if (!Capabilities.Has(GameCapability.RenameContestant)) return ActionResult.Fail("RenameContestant unavailable");
        var session = SDataAccess.ActiveSession;
        var t = WwdsMapping.TeamOf(session, team);
        if (t == null) return ActionResult.Fail("no active session");
        if (memberIndex < 0 || memberIndex >= t.PlayerAgents.Count) return ActionResult.Fail("team " + team + " has no member " + memberIndex);
        var clean = SanitizeName(name);
        if (clean.Length == 0) return ActionResult.Fail("name is empty after sanitizing");

        var agent = t.PlayerAgents[memberIndex];
        if (!(agent is PlayerAgentBot) && !_originalHumanNames.ContainsKey(agent.ProfileId))
        {
            _originalHumanNames[agent.ProfileId] = agent.Name;
        }

        agent.Name = clean;
        return ActionResult.Success("renamed " + team + memberIndex + " to '" + clean + "' (shown from the next UI refresh)");
    }

    public ActionResult RenameTeam(TeamSlot team, string? name)
    {
        if (!Capabilities.Has(GameCapability.RenameTeam)) return ActionResult.Fail("RenameTeam unavailable");
        _teamNames[(int)team] = name == null ? null : SanitizeName(name);
        return ActionResult.Success("team " + team + " label: " + TeamName(team));
    }

    /// <summary>Removes TextMeshPro rich-text markup and control characters; clamps the length.</summary>
    public static string SanitizeName(string? name)
    {
        if (name == null) return string.Empty;
        var chars = new List<char>();
        foreach (var c in name)
        {
            if (char.IsControl(c) || c == '<' || c == '>') continue;
            chars.Add(c);
        }

        var s = new string(chars.ToArray()).Trim();
        return s.Length > MaxNameLength ? s.Substring(0, MaxNameLength).Trim() : s;
    }

    public ActionResult SubmitAnswer(TeamSlot team, int optionIndex)
    {
        if (!Capabilities.Has(GameCapability.SubmitAnswerForHuman)) return ActionResult.Fail("SubmitAnswerForHuman unavailable");
        if (optionIndex < 0 || optionIndex >= ShowFormat.OptionCount) return ActionResult.Fail("option out of range");
        var session = SDataAccess.ActiveSession;
        var ui = Members.QuestionUi(SDataAccess.ActiveController);
        if (session == null || ui == null) return ActionResult.Fail("no question on screen");
        if (_phase != GamePhase.WaitingForAnswer) return ActionResult.Fail("not waiting for an answer (phase " + _phase + ")");

        var t = WwdsMapping.TeamOf(session, team)!;
        var type = session.GetActiveQuestionType();
        if (type == EQuestionType.Master)
        {
            Members.QuestionConfirmMaster!.Invoke(ui, new object[] { t, optionIndex });
            return ActionResult.Success("master answer " + AnswerOption.KeyOf(optionIndex) + " for team " + team);
        }

        if (type != EQuestionType.Question) return ActionResult.Fail("bonus estimates are numeric");
        var active = Members.ActivePlayerOf(ui);
        if (active == null || !ReferenceEquals(active.ParentTeam, t)) return ActionResult.Fail("it is not team " + team + "'s turn");
        if (active is PlayerAgentBot) return ActionResult.Fail("active contestant is a bot; use external control");

        // Stop the human input coroutine first, otherwise a later button press sends a second answer.
        ui.StopTimer();
        var buttons = (ViewAnswerButton[])Members.QuestionAnswerButtons!.GetValue(ui);
        Members.QuestionSelectedButton!.SetValue(ui, buttons[optionIndex]);
        ui.SendAnswer(optionIndex);
        return ActionResult.Success("answer " + AnswerOption.KeyOf(optionIndex) + " for team " + team);
    }

    public IList<DecisionOption> GetOpenCategories()
    {
        var result = new List<DecisionOption>();
        var session = SDataAccess.ActiveSession;
        if (session?.CurrentShow == null) return result;
        for (var i = 0; i < ShowFormat.NormalQuestionsPerShow && i < session.CurrentShow.dataArray.Length; i++)
        {
            if (session.ShowProgress.ContainsKey(i)) continue;
            result.Add(new DecisionOption(i, (i + 1).ToString(), WwdsMapping.CleanText(session.CurrentShow.dataArray[i].Kategoriename)));
        }

        return result;
    }

    public ActionResult SelectCategory(TeamSlot team, int categoryIndex)
    {
        if (!Capabilities.Has(GameCapability.SelectCategoryForHuman)) return ActionResult.Fail("SelectCategoryForHuman unavailable");
        if (_phase != GamePhase.CategorySelection) return ActionResult.Fail("not in category selection (phase " + _phase + ")");
        var session = SDataAccess.ActiveSession;
        var ui = Members.CategoryUi(SDataAccess.ActiveController);
        if (session == null || ui == null) return ActionResult.Fail("no category screen");
        if (session.ShowProgress.ContainsKey(categoryIndex) || categoryIndex < 0 || categoryIndex >= ShowFormat.NormalQuestionsPerShow) return ActionResult.Fail("category " + categoryIndex + " is not open");
        if (WwdsHooks.CategoryChooser == null || WwdsHooks.CategoryChooser is PlayerAgentBot) return ActionResult.Fail("no human is choosing a category");
        if (!ReferenceEquals(WwdsHooks.CategoryChooser.ParentTeam, WwdsMapping.TeamOf(session, team))) return ActionResult.Fail("team " + team + " is not choosing");

        // First call highlights, second call with the same index confirms (game behaviour).
        ui.SelectCategory(categoryIndex, false);
        ui.SelectCategory(categoryIndex, false);
        return ActionResult.Success("category " + (categoryIndex + 1) + " for team " + team);
    }

    // ---- programmatic show start ---------------------------------------------------------------

    public ActionResult StartShow(ShowSetup setup)
    {
        if (!Capabilities.Has(GameCapability.StartShow)) return ActionResult.Fail("StartShow unavailable");
        if (SDataAccess.ActiveSession != null) return ActionResult.Fail("a show is already running");
        if (_phase != GamePhase.MainMenu && _phase != GamePhase.PlayerSelection) return ActionResult.Fail("go to the main menu first (phase " + _phase + ")");
        _pendingShow = setup;
        _pendingShowSince = _ctx.Clock.Now;
        if (_phase == GamePhase.MainMenu)
        {
            var submit = HarmonyLib.AccessTools.Method(typeof(MainMenuController), "Submit", new[] { typeof(int) });
            if (submit == null || MainMenuController.Instance == null) return ActionResult.Fail("main menu not ready");
            submit.Invoke(MainMenuController.Instance, new object[] { (int)MainMenuState.Main });
        }

        return ActionResult.Success("starting show " + (setup.ShowId > 0 ? setup.ShowId.ToString() : "(next)"));
    }

    private void TickPendingShow(double now)
    {
        if (_pendingShow == null) return;
        if (now - _pendingShowSince > 60)
        {
            Log.Warn("StartShow gave up: player selection not reached");
            _pendingShow = null;
            return;
        }

        if (_state != "StateScreenSelectPlayers" || now - _pendingShowSince < 3) return;
        var setup = _pendingShow;
        _pendingShow = null;
        try
        {
            CreateSession(setup);
        }
        catch (Exception ex)
        {
            Log.Error("StartShow failed", ex);
        }
    }

    private void CreateSession(ShowSetup setup)
    {
        var view = UnityEngine.Object.FindObjectOfType<ViewSelectPlayers>();
        var createBot = HarmonyLib.AccessTools.Method(typeof(ViewSelectPlayers), "CreateAiPlayer");
        var selected = HarmonyLib.AccessTools.Field(typeof(ViewSelectPlayers), "selectedShowID");
        if (view == null || createBot == null || selected == null) throw new InvalidOperationException("player selection members missing");

        SuppressPersistence = setup.SuppressPersistence;
        var characters = SDataStore.Instance.AvailableCharacters;
        var nextCharacter = 0;
        APlayerAgent Make(bool bot, string? name)
        {
            APlayerAgent agent;
            if (bot)
            {
                agent = (PlayerAgentBot)createBot.Invoke(view, null);
            }
            else
            {
                var local = new PlayerAgentLocal();
                local.UpdateData(characters[Math.Min(nextCharacter++, characters.Length - 1)], completeRefresh: true);
                if (local.Input is InputPlayer input) input.Device = InputManager.ActiveDevice;
                agent = local;
            }

            if (!string.IsNullOrEmpty(name)) agent.Name = SanitizeName(name);
            return agent;
        }

        var teamA = new Team();
        var teamB = new Team();
        teamA.Add(Make(setup.TeamAIsBot, setup.TeamABotName));
        teamB.Add(Make(setup.TeamBIsBot, setup.TeamBBotName));
        teamA.SetAudienceCount(60);
        teamB.SetAudienceCount(55);
        teamA.ClearScores();
        teamB.ClearScores();
        var showId = setup.ShowId > 0 ? setup.ShowId : (int)selected.GetValue(view);
        Log.Info("Starting show " + showId + ": " + teamA[0].Name + " vs " + teamB[0].Name + (SuppressPersistence ? " (saves and achievements suppressed)" : string.Empty));
        SDataAccess.ActiveSession = new RuntimeSession(teamA, teamB, showId, false, 0f);
        SGameFSM.Instance.SetInputAndContinue(0);
    }

    // ---- hook callbacks (main thread, called from WwdsHooks) -------------------------------------

    internal void OnStateEnter(string state)
    {
        if (WwdsMapping.IsHelperState(state)) return;
        _state = state;
        _phase = WwdsMapping.PhaseForState(state);
        Publish(new GameStateChanged(state, _phase));
        if (state == "StateScreenMainMenu" && !_gameLoaded)
        {
            _gameLoaded = true;
            Publish(new GameLoaded());
        }

        if (state == "StateShowLoop") Publish(new GameStarted(SDataAccess.ActiveSession?.ActiveShowIndex ?? -1));
        if (state == "StateLoadMainMenu")
        {
            SuppressPersistence = false;
            _originalHumanNames.Clear();
            _knownScores.Clear();
        }
    }

    internal void OnSessionCreated(RuntimeSession session)
    {
        _knownScores.Clear();
        _knownScores[session.TeamA] = session.TeamA.GetScore();
        _knownScores[session.TeamB] = session.TeamB.GetScore();
        var all = new List<ContestantInfo>();
        foreach (var slot in new[] { TeamSlot.A, TeamSlot.B }) all.AddRange(WwdsMapping.Team(session, slot, TeamName(slot), _external[(int)slot]).Members);
        Publish(new SessionCreated(session.ActiveShowIndex, all));
    }

    internal void SetPhase(GamePhase phase) => _phase = phase;

    internal void Publish(object evt)
    {
        try
        {
            _ctx.Bus.Publish(evt);
        }
        catch (Exception ex)
        {
            Log.Error("publish " + evt.GetType().Name + " failed", ex);
        }
    }

    internal void PublishScore(Team team, string reason)
    {
        var session = SDataAccess.ActiveSession;
        var slot = WwdsMapping.Slot(session, team);
        if (slot == null) return;
        var newScore = team.GetScore();
        var old = _knownScores.TryGetValue(team, out var k) ? k : 0;
        _knownScores[team] = newScore;
        if (old != newScore) Publish(new ScoreChanged(slot.Value, old, newScore, reason));
    }

    internal string? OriginalHumanName(int profileId) => _originalHumanNames.TryGetValue(profileId, out var n) ? n : null;

    internal DecisionRequest OpenDecision(DecisionKind kind, TeamSlot team, APlayerAgent? agent, IList<DecisionOption> options, float timeoutSeconds, int fallback)
    {
        var session = SDataAccess.ActiveSession;
        QuestionInfo? q = null;
        if (kind is DecisionKind.Answer or DecisionKind.MasterAnswer or DecisionKind.Estimate)
        {
            try
            {
                q = WwdsMapping.Question(Game, session!, session!.ActiveQuestionIndex);
            }
            catch (Exception ex)
            {
                Log.Debug("decision question mapping failed: " + ex.Message);
            }
        }

        var now = _ctx.Clock.Now;
        var request = new DecisionRequest(kind, team, WwdsMapping.Contestant(session, agent, _external), q, options, now, now + Math.Max(1f, timeoutSeconds))
        {
            FallbackIndex = fallback,
        };
        request.Completed += r => Publish(new DecisionCompleted(r));
        _pending.Add(request);
        Log.Info("Decision requested: " + request);
        Publish(new DecisionRequested(request));
        return request;
    }
}
