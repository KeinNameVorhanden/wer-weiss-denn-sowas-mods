using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using WWDS.ModCore.Capabilities;
using WWDS.ModCore.Events;
using WWDS.ModCore.Model;
using WWDS.Runtime.Patching;

namespace WWDS.Game.Wwds;

/// <summary>
/// All Harmony patches of the adapter. Each entry documents why it exists and which capability it
/// backs. Bodies run under <see cref="PatchGuard"/> and never let an exception reach game code.
/// Signatures are identical in both games' Assembly-CSharp.
/// </summary>
internal static class WwdsHooks
{
    /// <summary>Contestant currently picking a category (set when the category screen opens).</summary>
    internal static APlayerAgent? CategoryChooser;

    private static WwdsAdapter? A => WwdsAdapter.Current;

    public static void Register(PatchRegistry r, WwdsMembers m)
    {
        var stateArgs = new[] { typeof(Animator), typeof(AnimatorStateInfo), typeof(int) };
        foreach (var st in typeof(SGameFSM).Assembly.GetTypes().Where(t => typeof(StateMachineBehaviour).IsAssignableFrom(t) && !t.IsAbstract).OrderBy(t => t.Name))
        {
            var type = st;
            // Plain reflection for probing: AccessTools logs a warning for every state without the override.
            var declared = type.GetMethod("OnStateEnter", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly, null, stateArgs, null);
            if (declared == null || WwdsMapping.IsHelperState(type.Name)) continue;
            Add(r, "state." + type.Name, "Track the game state machine (graph lives in an asset, only enter callbacks are observable).",
                () => AccessTools.DeclaredMethod(type, "OnStateEnter", stateArgs), type.Name is "StateShowLoop" or "StateScreenMainMenu" ? GameCapability.ReadPhase : GameCapability.None, post: nameof(StateEnter));
        }

        Add(r, "session.created", "Final line-up of every show (new, resumed, programmatic).",
            () => AccessTools.Constructor(typeof(RuntimeSession), new[] { typeof(Team), typeof(Team), typeof(int), typeof(bool), typeof(float), typeof(bool), typeof(int), typeof(Dictionary<int, DataShowProgress>) }),
            GameCapability.ReadContestants | GameCapability.RenameContestant, post: nameof(SessionCreated));
        Add(r, "question.selected", "Single writer of the active question index.",
            () => AccessTools.Method(typeof(RuntimeSession), "SetQuestionIndex", new[] { typeof(int) }), GameCapability.ReadQuestion | GameCapability.ReadAnswers | GameCapability.ReadCorrectAnswer, post: nameof(QuestionSelected));
        Add(r, "answers.normal", "Input opens after all three options were read out.",
            () => AccessTools.Method(typeof(ViewQuestionUI), "WaitForPlayerInputNormalQuestion"), GameCapability.ReadAnswers, post: nameof(AnswersNormal));
        Add(r, "answers.master", "Master question input opens (20 s clock starts).",
            () => AccessTools.Method(typeof(ViewQuestionUI), "WaitForPlayerInputMasterQuestion"), GameCapability.ReadAnswers, post: nameof(AnswersMaster));
        Add(r, "answer.sent", "Single funnel of every answer (human, bot, joker, master, bonus).",
            () => AccessTools.Method(typeof(AGameEventHandler), "SendAnswer"), GameCapability.ObserveAnswers, pre: nameof(AnswerSent));
        Add(r, "bet.sent", "Master bet locked.",
            () => AccessTools.Method(typeof(AGameEventHandler), "SendBet"), GameCapability.ObserveAnswers, pre: nameof(BetSent));
        Add(r, "reveal.normal", "Correct option highlighted for a normal question.",
            () => AccessTools.Method(typeof(ViewQuestionUI), "ShowCorrectAnswer"), GameCapability.ObserveAnswers, post: nameof(RevealNormal));
        Add(r, "reveal.master", "Correct option highlighted for the master question.",
            () => AccessTools.Method(typeof(ViewQuestionUI), "RevealResultForMasterQuestions"), GameCapability.ObserveAnswers, post: nameof(RevealMaster));
        Add(r, "reveal.bonus", "Bonus estimate target revealed.",
            () => AccessTools.Method(typeof(ViewBonusQuestionUI), "RevealCorrectAnswer", new[] { typeof(float) }), GameCapability.ObserveAnswers, pre: nameof(RevealBonus));
        Add(r, "question.finished", "Persistent result of a normal question.",
            () => AccessTools.Method(typeof(RuntimeSession), "SetQuestionResult"), GameCapability.ObserveAnswers, post: nameof(QuestionFinished));
        Add(r, "score.add", "Score writer: +500 per correct normal answer.",
            () => AccessTools.Method(typeof(Team), "AddScore"), GameCapability.ReadScore, post: nameof(ScoreAdd));
        Add(r, "score.bet", "Score writer: master bet won or lost.",
            () => AccessTools.Method(typeof(Team), "ProcessBet"), GameCapability.ReadScore, post: nameof(ScoreBet));
        Add(r, "score.clear", "Score writer: reset.",
            () => AccessTools.Method(typeof(Team), "ClearScores"), GameCapability.ReadScore, post: nameof(ScoreClear));
        Add(r, "round.finished", "End of every normal/master turn (session autosave).",
            () => AccessTools.Method(typeof(RuntimeSession), "Save"), GameCapability.None, post: nameof(RoundSaved));
        Add(r, "round.category", "Category screen opens for a contestant (one per normal turn).",
            () => AccessTools.Method(typeof(ViewCategoryUI), "WaitForNewCategory"), GameCapability.SelectCategoryForHuman, pre: nameof(CategoryTurn));
        Add(r, "round.bets", "Master bet phase starts.", () => m.ShowWaitForPlayerBets, GameCapability.None, pre: nameof(BetsPhase));
        Add(r, "round.estimate", "Bonus estimate phase starts.", () => m.ShowWaitForPlayerEstimations, GameCapability.None, pre: nameof(EstimatePhase));
        Add(r, "show.outro", "Show finished with (or without) a winner.",
            () => AccessTools.Method(typeof(ShowController), "SetOutroView"), GameCapability.None, post: nameof(Outro));
        Add(r, "show.abort", "Show quit from the pause menu.",
            () => AccessTools.Method(typeof(ViewPauseMenu), "EndGame"), GameCapability.None, pre: nameof(Abort));
        Add(r, "name.set", "Report contestant renames (the game has no event).",
            () => AccessTools.PropertySetter(typeof(APlayerAgent), "Name"), GameCapability.None, pre: nameof(NameSet));
        Add(r, "name.persist-guard", "Keep harness renames out of the Characters save (stat updates write the profile back).",
            () => AccessTools.Method(typeof(SDataStore), "UpdateCharacterProfile"), GameCapability.RenameContestant, pre: nameof(ProfileWrite));
        Add(r, "control.answer", "Let the harness decide a bot's normal answer.", () => m.QuestionNpcSequence, GameCapability.ControlBotAnswer, pre: nameof(ControlAnswer));
        Add(r, "control.master", "Let the harness decide a bot's master answer.", () => m.QuestionNpcMaster, GameCapability.ControlBotMasterAnswer, pre: nameof(ControlMaster));
        Add(r, "control.bet", "Let the harness decide a bot's master bet.", () => m.BetNpc, GameCapability.ControlBotBet, pre: nameof(ControlBet));
        Add(r, "control.estimate", "Let the harness decide a bot's bonus estimate.", () => m.BonusNpc, GameCapability.ControlBotEstimate, pre: nameof(ControlEstimate));
        Add(r, "control.category", "Let the harness decide a bot's category.", () => m.CategoryAuto, GameCapability.ControlBotCategory, pre: nameof(ControlCategory));
        Add(r, "team.label", "Custom team labels in player frames (hard-coded in the game).",
            () => AccessTools.Method(typeof(ViewPlayerFrame), "UpdatePlayerFrameInformation"), GameCapability.RenameTeam, post: nameof(TeamLabel));
        Add(r, "suppress.save", "No savegame writes for harness-started test shows.",
            () => AccessTools.Method(typeof(SSaveData), "Save"), GameCapability.StartShow, pre: nameof(SuppressSave));
        Add(r, "suppress.trophy", "No achievement progress for harness-started test shows.",
            () => AccessTools.Method(typeof(SPlayerStatistics), "UnlockTrophy"), GameCapability.StartShow, pre: nameof(SuppressTrophy));
        Add(r, "suppress.steam", "No Steam achievement unlocks for harness-started test shows.",
            () => AccessTools.Method(typeof(AchievementManagerSteam), "UnlockTrophy"), GameCapability.StartShow, pre: nameof(SuppressTrophy));
    }

    private static void Add(PatchRegistry r, string id, string reason, Func<MethodBase?> target, GameCapability backs, string? pre = null, string? post = null)
    {
        r.Add(new PatchSpec(id, reason, target, backs)
        {
            Prefix = pre == null ? null : typeof(WwdsHooks).GetMethod(pre, BindingFlags.Static | BindingFlags.NonPublic),
            Postfix = post == null ? null : typeof(WwdsHooks).GetMethod(post, BindingFlags.Static | BindingFlags.NonPublic),
        });
    }

    // ---- observation ---------------------------------------------------------------------------

    private static void StateEnter(StateMachineBehaviour __instance) => PatchGuard.Run("StateEnter", () => A?.OnStateEnter(__instance.GetType().Name));

    private static void SessionCreated(RuntimeSession __instance) => PatchGuard.Run("SessionCreated", () => A?.OnSessionCreated(__instance));

    private static void QuestionSelected(RuntimeSession __instance, int index) => PatchGuard.Run("QuestionSelected", () =>
    {
        var a = A;
        if (a == null || __instance.CurrentShow == null) return;
        var q = WwdsMapping.Question(a.Game, __instance, index);
        a.SetPhase(GamePhase.QuestionPresented);
        TeamSlot? team = q.Kind == QuestionKind.Normal ? (__instance.TeamAActive ? TeamSlot.A : TeamSlot.B) : null;
        a.Publish(new QuestionStarted(q, team));
    });

    private static void AnswersNormal(APlayerAgent activePlayer) => PatchGuard.Run("AnswersNormal", () =>
    {
        var a = A;
        var s = SDataAccess.ActiveSession;
        if (a == null || s == null) return;
        a.SetPhase(GamePhase.WaitingForAnswer);
        var who = WwdsMapping.Contestant(s, activePlayer, new[] { a.IsExternallyControlled(TeamSlot.A), a.IsExternallyControlled(TeamSlot.B) });
        a.Publish(new AnswersPresented(WwdsMapping.Question(a.Game, s, s.ActiveQuestionIndex), who));
    });

    private static void AnswersMaster() => PatchGuard.Run("AnswersMaster", () =>
    {
        var a = A;
        var s = SDataAccess.ActiveSession;
        if (a == null || s == null) return;
        a.SetPhase(GamePhase.WaitingForAnswer);
        a.Publish(new AnswersPresented(WwdsMapping.Question(a.Game, s, s.ActiveQuestionIndex), null));
    });

    private static void AnswerSent(Team team, int index, bool correct, bool isAudience) => PatchGuard.Run("AnswerSent", () =>
    {
        var a = A;
        var s = SDataAccess.ActiveSession;
        var slot = WwdsMapping.Slot(s, team);
        if (a == null || s == null || slot == null) return;
        var kind = s.GetActiveQuestionType() switch
        {
            EQuestionType.Master => QuestionKind.Master,
            EQuestionType.Bonus => QuestionKind.Bonus,
            _ => QuestionKind.Normal,
        };
        if (kind == QuestionKind.Normal) a.SetPhase(GamePhase.Reveal);
        a.Publish(new AnswerLocked(slot.Value, index, correct, isAudience, kind));
    });

    private static void BetSent(Team team) => PatchGuard.Run("BetSent", () =>
    {
        var slot = WwdsMapping.Slot(SDataAccess.ActiveSession, team);
        if (slot != null) A?.Publish(new BetLocked(slot.Value, team.Bet));
    });

    private static void RevealNormal(ViewQuestionUI __instance) => PatchGuard.Run("RevealNormal", () => Reveal(__instance.GetCorrectAnswerIndex()));

    private static void RevealMaster(ViewQuestionUI __instance) => PatchGuard.Run("RevealMaster", () => Reveal(__instance.GetCorrectAnswerIndex()));

    private static void Reveal(int correct)
    {
        var a = A;
        var s = SDataAccess.ActiveSession;
        if (a == null || s == null) return;
        a.SetPhase(GamePhase.Reveal);
        a.Publish(new AnswerRevealed(WwdsMapping.Question(a.Game, s, s.ActiveQuestionIndex), correct, null));
    }

    private static void RevealBonus(float answer) => PatchGuard.Run("RevealBonus", () =>
    {
        var a = A;
        var s = SDataAccess.ActiveSession;
        if (a == null || s == null) return;
        a.SetPhase(GamePhase.Reveal);
        a.Publish(new AnswerRevealed(WwdsMapping.Question(a.Game, s, s.ActiveQuestionIndex), -1, answer));
    });

    private static void QuestionFinished(RuntimeSession __instance, DataShowProgress progress) => PatchGuard.Run("QuestionFinished", () =>
    {
        var a = A;
        if (a == null) return;
        a.Publish(new QuestionFinished(WwdsMapping.Question(a.Game, __instance, __instance.ActiveQuestionIndex), progress.AnsweredByTeamA ? TeamSlot.A : TeamSlot.B, progress.IsCorrect));
    });

    private static void ScoreAdd(Team __instance) => PatchGuard.Run("ScoreAdd", () => A?.PublishScore(__instance, "correct answer"));

    private static void ScoreBet(Team __instance, bool correct) => PatchGuard.Run("ScoreBet", () =>
    {
        var a = A;
        var s = SDataAccess.ActiveSession;
        if (a == null || s == null) return;
        a.PublishScore(__instance, correct ? "bet won" : "bet lost");
        var slot = WwdsMapping.Slot(s, __instance);
        if (slot != null) a.Publish(new QuestionFinished(WwdsMapping.Question(a.Game, s, s.ActiveQuestionIndex), slot, correct));
    });

    private static void ScoreClear(Team __instance) => PatchGuard.Run("ScoreClear", () => A?.PublishScore(__instance, "reset"));

    private static void RoundSaved(RuntimeSession __instance, bool isFinished) => PatchGuard.Run("RoundSaved", () =>
    {
        if (!isFinished) A?.Publish(new RoundFinished(__instance.GetQuestionCount()));
    });

    private static void CategoryTurn(APlayerAgent player, bool isLast) => PatchGuard.Run("CategoryTurn", () =>
    {
        var a = A;
        var s = SDataAccess.ActiveSession;
        CategoryChooser = player;
        if (a == null || s == null) return;
        a.SetPhase(GamePhase.CategorySelection);
        var who = WwdsMapping.Contestant(s, player, new[] { a.IsExternallyControlled(TeamSlot.A), a.IsExternallyControlled(TeamSlot.B) });
        a.Publish(new RoundStarted(QuestionKind.Normal, who?.Team, who));
    });

    private static void BetsPhase() => PatchGuard.Run("BetsPhase", () =>
    {
        A?.SetPhase(GamePhase.MasterBet);
        A?.Publish(new RoundStarted(QuestionKind.Master, null, null));
    });

    private static void EstimatePhase() => PatchGuard.Run("EstimatePhase", () =>
    {
        A?.SetPhase(GamePhase.BonusEstimate);
        A?.Publish(new RoundStarted(QuestionKind.Bonus, null, null));
    });

    private static void Outro(Team team) => PatchGuard.Run("Outro", () =>
    {
        var s = SDataAccess.ActiveSession;
        A?.Publish(new GameFinished(WwdsMapping.Slot(s, team), s?.TeamA.GetScore() ?? 0, s?.TeamB.GetScore() ?? 0, false));
    });

    private static void Abort() => PatchGuard.Run("Abort", () =>
    {
        var s = SDataAccess.ActiveSession;
        A?.Publish(new GameFinished(null, s?.TeamA.GetScore() ?? 0, s?.TeamB.GetScore() ?? 0, true));
    });

    private static void NameSet(APlayerAgent __instance, string value) => PatchGuard.Run("NameSet", () =>
    {
        var s = SDataAccess.ActiveSession;
        var slot = WwdsMapping.Slot(s, __instance.ParentTeam);
        if (slot != null && __instance.Name != value) A?.Publish(new PlayerNameChanged(slot.Value, __instance.Name, value));
    });

    private static void ProfileWrite(ref DataCharacterProfile profile)
    {
        var p = profile;
        var original = A?.OriginalHumanName(p.Id);
        if (original != null && p.Name != original)
        {
            p.Name = original;
            profile = p;
        }
    }

    private static void TeamLabel(ViewPlayerFrame __instance, bool isTeamA) => PatchGuard.Run("TeamLabel", () =>
    {
        var custom = A?.CustomTeamName(isTeamA);
        if (custom == null) return;
        if (A!.Members.FrameTeamNameText?.GetValue(__instance) is TMPro.TMP_Text label) label.text = custom;
    });

    private static bool SuppressSave() => !(A?.SuppressPersistence ?? false);

    private static bool SuppressTrophy() => !(A?.SuppressPersistence ?? false);

    // ---- external control (replace the bot coroutine when the team is externally controlled) -----

    private static bool ControlAnswer(ViewQuestionUI __instance, ref IEnumerator __result)
    {
        var a = A;
        if (a == null) return true;
        try
        {
            var player = a.Members.ActivePlayerOf(__instance);
            var slot = WwdsMapping.Slot(SDataAccess.ActiveSession, player?.ParentTeam);
            if (slot == null || !a.IsExternallyControlled(slot.Value)) return true;
            __result = WwdsDecisions.NormalAnswer(a, __instance, player!, slot.Value);
            return false;
        }
        catch (Exception ex)
        {
            a.Log.Error("control.answer failed, game bot decides", ex);
            return true;
        }
    }

    private static bool ControlMaster(ViewQuestionUI __instance, APlayerAgent player, ref IEnumerator __result)
    {
        var a = A;
        if (a == null) return true;
        try
        {
            var slot = WwdsMapping.Slot(SDataAccess.ActiveSession, player?.ParentTeam);
            if (slot == null || !a.IsExternallyControlled(slot.Value)) return true;
            __result = WwdsDecisions.MasterAnswer(a, __instance, player!, slot.Value);
            return false;
        }
        catch (Exception ex)
        {
            a.Log.Error("control.master failed, game bot decides", ex);
            return true;
        }
    }

    private static bool ControlBet(ViewMasterBetAnswerFrame __instance, APlayerAgent player, Team enemyTeam, ref IEnumerator __result)
    {
        var a = A;
        if (a == null) return true;
        try
        {
            var slot = WwdsMapping.Slot(SDataAccess.ActiveSession, player?.ParentTeam);
            if (slot == null || !a.IsExternallyControlled(slot.Value)) return true;
            __result = WwdsDecisions.Bet(a, __instance, player!, enemyTeam, slot.Value);
            return false;
        }
        catch (Exception ex)
        {
            a.Log.Error("control.bet failed, game bot decides", ex);
            return true;
        }
    }

    private static bool ControlEstimate(ViewBonusQuestionUI __instance, Team team, ref IEnumerator __result)
    {
        var a = A;
        if (a == null) return true;
        try
        {
            var slot = WwdsMapping.Slot(SDataAccess.ActiveSession, team);
            if (slot == null || !a.IsExternallyControlled(slot.Value)) return true;
            __result = WwdsDecisions.Estimate(a, __instance, team, slot.Value);
            return false;
        }
        catch (Exception ex)
        {
            a.Log.Error("control.estimate failed, game bot decides", ex);
            return true;
        }
    }

    private static bool ControlCategory(ViewCategoryUI __instance, bool isLast, bool isTutorial, ref IEnumerator __result)
    {
        var a = A;
        if (a == null || isLast || isTutorial) return true;
        try
        {
            var slot = WwdsMapping.Slot(SDataAccess.ActiveSession, CategoryChooser?.ParentTeam);
            if (slot == null || !a.IsExternallyControlled(slot.Value)) return true;
            __result = WwdsDecisions.Category(a, __instance, CategoryChooser!, slot.Value);
            return false;
        }
        catch (Exception ex)
        {
            a.Log.Error("control.category failed, game bot decides", ex);
            return true;
        }
    }
}
