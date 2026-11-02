using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using WWDS.ModCore.Config;
using WWDS.ModCore.Features;
using WWDS.ModCore.Model;
using WWDS.Runtime.Patching;

namespace WWDS.Game.Wwds.Mods;

/// <summary>
/// A fair estimate question. Teams type their numbers one after another and the game shows each team's
/// number to the other; now a logged-in number stays hidden ("???") until the answer is revealed. When
/// both are equally far off, the game always gave the win to Team Elton; now the team that logged in its
/// final number first wins (configurable).
/// </summary>
public sealed class EstimateFeature : FeatureBase
{
    public const string FeatureId = "Estimate";
    private const string Hidden = "???";

    private static EstimateFeature? _current;
    private readonly Dictionary<bool, float> _lockedAt = new();
    private IConfigValue<bool> _hide = null!;
    private IConfigValue<EstimateTie> _tie = null!;
    private ViewBonusQuestionUI? _view;
    private bool _typingTeamA;
    private bool _revealed;

    public override string Id => FeatureId;

    public override string DisplayName => "Fair estimate";

    public override string Description => "Hides the other team's estimate until the reveal; equal distance: the team that logged in first wins.";

    protected override string LogCategoryName => "Mods";

    protected override void OnStart()
    {
        _hide = Context.Config.Bind("Estimate", "HideOtherTeam", true, "The other team's logged-in number stays hidden until the answer is revealed.");
        _tie = Context.Config.Bind("Estimate", "Tie", EstimateTie.FirstLoggedIn, "Equal distance: FirstLoggedIn (the team that logged in its final number first) | TeamB (the game: always Team Elton).");
        _current = this;
        var ui = ModUi.For(Context);
        var none = ModCore.Capabilities.GameCapability.None;
        var t = typeof(ViewBonusQuestionUI);
        ui.Patch(new PatchSpec("mods.estimate.start", "Reset hiding and lock times for a new estimate.",
            () => AccessTools.Method(t, nameof(ViewBonusQuestionUI.WaitForPlayerInput)), none)
        {
            Prefix = AccessTools.Method(typeof(EstimateFeature), nameof(StartPrefix)),
        });
        ui.Patch(new PatchSpec("mods.estimate.turn", "Know which team is typing.",
            () => AccessTools.Method(t, "OnWaitForAnswerUpdated", new[] { typeof(Team) }), none)
        {
            Prefix = AccessTools.Method(typeof(EstimateFeature), nameof(TurnPrefix)),
        });
        ui.Patch(new PatchSpec("mods.estimate.lock", "Remember when each team logged in its number.",
            () => AccessTools.Method(t, "LockBet", new[] { typeof(bool) }), none)
        {
            Prefix = AccessTools.Method(typeof(EstimateFeature), nameof(LockPrefix)),
        });
        ui.Patch(new PatchSpec("mods.estimate.text", "The other team's number shows as ??? until the reveal.",
            () => AccessTools.Method(t, "SetTeamBetText", new[] { typeof(int), typeof(bool) }), none)
        {
            Postfix = AccessTools.Method(typeof(EstimateFeature), nameof(TextPostfix)),
        });
        ui.Patch(new PatchSpec("mods.estimate.reveal", "Show both numbers at the reveal.",
            () => AccessTools.Method(t, nameof(ViewBonusQuestionUI.RevealCorrectAnswer), new[] { typeof(float) }), none)
        {
            Prefix = AccessTools.Method(typeof(EstimateFeature), nameof(RevealPrefix)),
        });
        ui.Patch(new PatchSpec("mods.estimate.tie-result", "Tie rule for the result.",
            () => AccessTools.Method(t, "ConfirmAnswerButton"), none)
        {
            Prefix = AccessTools.Method(typeof(EstimateFeature), nameof(ConfirmPrefix)),
        });
        ui.Patch(new PatchSpec("mods.estimate.tie-show", "Tie rule for the reveal colours.",
            () => AccessTools.Method(t, "ShowCorrectAnswer", new[] { typeof(float) }), none)
        {
            Prefix = AccessTools.Method(typeof(EstimateFeature), nameof(ShowPrefix)),
        });
    }

    protected override void OnStop() => _current = null;

    private static T F<T>(ViewBonusQuestionUI view, string name) => (T)AccessTools.Field(typeof(ViewBonusQuestionUI), name).GetValue(view);

    private static void StartPrefix(ViewBonusQuestionUI __instance)
    {
        var self = _current;
        if (self == null) return;
        self._view = __instance;
        self._lockedAt.Clear();
        self._revealed = false;
    }

    private static void TurnPrefix(ViewBonusQuestionUI __instance, Team team)
    {
        var self = _current;
        if (self == null) return;
        self._typingTeamA = SDataAccess.ActiveSession != null && SDataAccess.ActiveSession.TeamA == team;
        PatchGuard.Run("mods.estimate.turn", () => self.Mask(__instance));
    }

    private static void LockPrefix(bool isTeamA)
    {
        if (_current != null) _current._lockedAt[isTeamA] = Time.unscaledTime;
    }

    private static void TextPostfix(ViewBonusQuestionUI __instance)
    {
        var self = _current;
        if (self != null) PatchGuard.Run("mods.estimate.text", () => self.Mask(__instance));
    }

    /// <summary>The team not typing sees nothing of the other's number once it was logged in.</summary>
    private void Mask(ViewBonusQuestionUI view)
    {
        if (!_hide.Value || _revealed) return;
        var other = !_typingTeamA;
        if (!_lockedAt.ContainsKey(other)) return;
        F<ViewAnswerButton>(view, other ? "answerButtonTeamA" : "answerButtonTeamB").SetAnswerText(Hidden);
        F<ViewAnswerButton>(view, _typingTeamA ? "answerButtonTeamA" : "answerButtonTeamB").SetAnswerText(F<int>(view, _typingTeamA ? "teamABet" : "teamBBet").ToString());
    }

    private static void RevealPrefix(ViewBonusQuestionUI __instance)
    {
        var self = _current;
        if (self == null) return;
        self._revealed = true;
        PatchGuard.Run("mods.estimate.reveal", () =>
        {
            F<ViewAnswerButton>(__instance, "answerButtonTeamA").SetAnswerText(F<int>(__instance, "teamABet").ToString());
            F<ViewAnswerButton>(__instance, "answerButtonTeamB").SetAnswerText(F<int>(__instance, "teamBBet").ToString());
        });
    }

    private bool TieGoesToA(ViewBonusQuestionUI view, float answer)
    {
        var a = F<int>(view, "teamABet");
        var b = F<int>(view, "teamBBet");
        return EstimateRules.IsTie(answer, a, b) && EstimateRules.TeamAWins(answer, a, b, _tie.Value, Locked(true), Locked(false));
    }

    private float? Locked(bool teamA) => _lockedAt.TryGetValue(teamA, out var t) ? t : (float?)null;

    /// <summary>Only on a tie that Team A wins: the game's result with the winner switched.</summary>
    private static bool ConfirmPrefix(ViewBonusQuestionUI __instance)
    {
        var self = _current;
        if (self == null || F<bool>(__instance, "isTutorial")) return true;
        var answer = SDataAccess.ActiveSession.GetActiveQuestionData().Antwort;
        if (!self.TieGoesToA(__instance, answer)) return true;
        var handled = false;
        PatchGuard.Run("mods.estimate.tie-result", () =>
        {
            var teamA = F<Team>(__instance, "teamA");
            var distance = Math.Abs(answer - F<int>(__instance, "teamABet")) / 100f;
            foreach (var agent in teamA)
            {
                if (agent is not PlayerAgentBot) agent.SetBonusQuestionAccuracyRecord(distance);
            }

            foreach (var agent in F<Team>(__instance, "teamB"))
            {
                if (agent is not PlayerAgentBot) agent.SetBonusQuestionAccuracyRecord(distance);
            }

            if (distance <= 0.1) SPlayerStatistics.Instance.UnlockTrophy(19);
            if (distance <= 0.05) SPlayerStatistics.Instance.UnlockTrophy(20);
            if (distance <= 0.01) SPlayerStatistics.Instance.UnlockTrophy(21);
            F<ViewAnswerButton>(__instance, "answerButtonTeamA").ChangeToDefaultSprite();
            F<ViewAnswerButton>(__instance, "answerButtonTeamB").ChangeToDefaultSprite();
            SSoundManager.Instance.PlaySFX(F<AudioClip>(__instance, "selectAnswerClip"));
            __instance.SetControlIconVisibility(show: false);
            F<AGameEventHandler>(__instance, "eventHandler").SendAnswer(teamA, 0, true);
            self.Log.Info("Estimate tie: Team A logged in first and wins");
            handled = true;
        });
        return !handled;
    }

    private static bool ShowPrefix(ViewBonusQuestionUI __instance, float answer)
    {
        var self = _current;
        if (self == null || !self.TieGoesToA(__instance, answer)) return true;
        F<ViewAnswerButton>(__instance, "answerButtonTeamA").ChangeToCorrectSprite(highlighted: true);
        F<ViewAnswerButton>(__instance, "answerButtonTeamB").ChangeToWrongSprite();
        SSoundManager.Instance.PlaySFX(F<AudioClip>(__instance, "revealAnswerClip"));
        return false;
    }
}
