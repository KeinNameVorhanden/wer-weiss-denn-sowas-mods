using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace WWDS.Diagnostics;

/// <summary>
/// Passive observation patches. Every patch only reads state and logs; none changes game behaviour.
/// Each patch is applied individually so one missing target cannot block the others.
/// </summary>
internal static class ProbePatches
{
    internal static ProbeLog Log = null!;
    internal static string LastState = "(none)";

    private static readonly FieldInfo? QuestionUiActivePlayer = AccessTools.Field(typeof(ViewQuestionUI), "activePlayer");

    public static void Apply(Harmony harmony, ProbeLog log)
    {
        Log = log;
        var t = typeof(ProbePatches);

        // FSM: every StateMachineBehaviour in the game that overrides OnStateEnter / OnStateExit.
        var stateTypes = typeof(SGameFSM).Assembly.GetTypes()
            .Where(x => typeof(StateMachineBehaviour).IsAssignableFrom(x) && !x.IsAbstract)
            .OrderBy(x => x.Name)
            .ToList();
        foreach (var st in stateTypes)
        {
            Try(harmony, AccessTools.DeclaredMethod(st, "OnStateEnter", new[] { typeof(Animator), typeof(AnimatorStateInfo), typeof(int) }), null, Post(nameof(StateEnter)), st.Name + ".OnStateEnter", optional: true);
            Try(harmony, AccessTools.DeclaredMethod(st, "OnStateExit", new[] { typeof(Animator), typeof(AnimatorStateInfo), typeof(int) }), null, Post(nameof(StateExit)), st.Name + ".OnStateExit", optional: true);
        }

        Try(harmony, AccessTools.Method(typeof(SGameFSM), "SetInputAndContinue", new[] { typeof(int) }), Pre(nameof(FsmInput)), null, "SGameFSM.SetInputAndContinue");
        Try(harmony, AccessTools.Method(typeof(SGameFSM), "ContinueStateMachine"), Pre(nameof(FsmContinue)), null, "SGameFSM.ContinueStateMachine");
        Try(harmony, AccessTools.Method(typeof(SGameFSM), "RewindStateMachine"), Pre(nameof(FsmRewind)), null, "SGameFSM.RewindStateMachine");

        Try(harmony, AccessTools.Constructor(typeof(RuntimeSession), new[] { typeof(Team), typeof(Team), typeof(int), typeof(bool), typeof(float), typeof(bool), typeof(int), typeof(Dictionary<int, DataShowProgress>) }), null, Post(nameof(SessionCreated)), "RuntimeSession..ctor");
        Try(harmony, AccessTools.Method(typeof(RuntimeSession), "SetQuestionIndex", new[] { typeof(int) }), null, Post(nameof(QuestionSelected)), "RuntimeSession.SetQuestionIndex");
        Try(harmony, AccessTools.Method(typeof(ViewQuestionUI), "WaitForPlayerInputNormalQuestion"), null, Post(nameof(AnswersPresentedNormal)), "ViewQuestionUI.WaitForPlayerInputNormalQuestion");
        Try(harmony, AccessTools.Method(typeof(ViewQuestionUI), "WaitForPlayerInputMasterQuestion"), null, Post(nameof(AnswersPresentedMaster)), "ViewQuestionUI.WaitForPlayerInputMasterQuestion");
        Try(harmony, AccessTools.Method(typeof(AGameEventHandler), "SendAnswer"), Pre(nameof(AnswerSent)), null, "AGameEventHandler.SendAnswer");
        Try(harmony, AccessTools.Method(typeof(AGameEventHandler), "SendBet"), Pre(nameof(BetSent)), null, "AGameEventHandler.SendBet");
        Try(harmony, AccessTools.Method(typeof(ViewQuestionUI), "ShowCorrectAnswer"), null, Post(nameof(CorrectRevealed)), "ViewQuestionUI.ShowCorrectAnswer");
        Try(harmony, AccessTools.Method(typeof(ViewQuestionUI), "RevealResultForMasterQuestions"), null, Post(nameof(MasterRevealed)), "ViewQuestionUI.RevealResultForMasterQuestions");
        Try(harmony, AccessTools.Method(typeof(ViewBonusQuestionUI), "RevealCorrectAnswer"), Pre(nameof(BonusRevealed)), null, "ViewBonusQuestionUI.RevealCorrectAnswer");
        Try(harmony, AccessTools.Method(typeof(RuntimeSession), "SetQuestionResult"), null, Post(nameof(QuestionResult)), "RuntimeSession.SetQuestionResult");
        Try(harmony, AccessTools.Method(typeof(RuntimeSession), "Save"), null, Post(nameof(SessionSaved)), "RuntimeSession.Save");
        Try(harmony, AccessTools.Method(typeof(Team), "AddScore"), null, Post(nameof(ScoreAdded)), "Team.AddScore");
        Try(harmony, AccessTools.Method(typeof(Team), "ProcessBet"), null, Post(nameof(BetProcessed)), "Team.ProcessBet");
        Try(harmony, AccessTools.Method(typeof(Team), "ClearScores"), null, Post(nameof(ScoresCleared)), "Team.ClearScores");
        Try(harmony, AccessTools.Method(typeof(ShowController), "SetOutroView"), null, Post(nameof(Outro)), "ShowController.SetOutroView");
        Try(harmony, AccessTools.Method(typeof(ViewCategoryUI), "WaitForNewCategory"), Pre(nameof(CategoryTurn)), null, "ViewCategoryUI.WaitForNewCategory");
        Try(harmony, AccessTools.Method(typeof(NPCCategoryWeight), "GetNPCSelectionList"), null, Post(nameof(NpcSelectionList)), "NPCCategoryWeight.GetNPCSelectionList");
        Try(harmony, AccessTools.Method(typeof(SDataStore), "UpdateCharacterProfile"), Pre(nameof(ProfileWrite)), null, "SDataStore.UpdateCharacterProfile");
        Try(harmony, AccessTools.PropertySetter(typeof(APlayerAgent), "Name"), Pre(nameof(NameSet)), null, "APlayerAgent.set_Name");
    }

    internal static void Try(Harmony harmony, MethodBase? target, HarmonyMethod? prefix, HarmonyMethod? postfix, string label, bool optional = false)
    {
        if (target == null)
        {
            if (!optional) Log.Event("patch", "target", label, "ok", false, "error", "target not found");
            return;
        }

        try
        {
            harmony.Patch(target, prefix, postfix);
            Log.Event("patch", "target", label, "ok", true);
        }
        catch (Exception ex)
        {
            Log.Event("patch", "target", label, "ok", false, "error", ex.GetType().Name + ": " + ex.Message);
        }
    }

    internal static HarmonyMethod Pre(string name) => new(typeof(ProbePatches).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public));

    internal static HarmonyMethod Post(string name) => Pre(name);

    // ---- helpers -------------------------------------------------------------------------

    internal static string TeamTag(Team? team)
    {
        var s = SDataAccess.ActiveSession;
        if (team == null) return "null";
        if (s != null && ReferenceEquals(team, s.TeamA)) return "A";
        if (s != null && ReferenceEquals(team, s.TeamB)) return "B";
        return "?";
    }

    internal static List<object?> DescribeTeam(Team team)
    {
        var list = new List<object?>();
        foreach (var agent in team.PlayerAgents)
        {
            list.Add(new Dictionary<string, object?>
            {
                ["name"] = agent.Name,
                ["type"] = agent.GetType().Name,
                ["profileId"] = agent.ProfileId,
            });
        }

        return list;
    }

    internal static Dictionary<string, object?> DescribeQuestion(ShowContentData q, int index)
    {
        return new Dictionary<string, object?>
        {
            ["index"] = index,
            ["id"] = q.ID,
            ["type"] = q.EQUESTIONTYPE.ToString(),
            ["category"] = q.Kategoriename,
            ["text"] = q.Frage,
            ["a"] = q.A,
            ["b"] = q.B,
            ["c"] = q.C,
            ["answer"] = q.Antwort,
            ["video"] = q.Antwortvideofile,
        };
    }

    // ---- patch bodies ----------------------------------------------------------------------

    /// <summary>Behaviours on decorative layers that run alongside the main flow states.</summary>
    private static readonly HashSet<string> HelperStates = new() { "StateLightshow", "StateCameraFade", "StateTriggerTimeline", "StateAnimVariationBlend" };

    private static void StateEnter(StateMachineBehaviour __instance)
    {
        var name = __instance.GetType().Name;
        if (!HelperStates.Contains(name)) LastState = name;
        Log.Event("fsm.enter", "state", name, "timeScale", Time.timeScale);
    }

    private static void StateExit(StateMachineBehaviour __instance) => Log.Event("fsm.exit", "state", __instance.GetType().Name);

    private static void FsmInput(int arg) => Log.Event("fsm.input", "arg", arg, "from", LastState);

    private static void FsmContinue() => Log.Event("fsm.continue", "from", LastState);

    private static void FsmRewind() => Log.Event("fsm.rewind", "from", LastState);

    private static void SessionCreated(RuntimeSession __instance, int showIndex, bool playTutorial)
    {
        Log.Event("session.created", "show", showIndex, "tutorial", playTutorial,
            "teamA", DescribeTeam(__instance.TeamA), "teamB", DescribeTeam(__instance.TeamB),
            "audienceA", __instance.TeamA.AudienceCount, "audienceB", __instance.TeamB.AudienceCount,
            "questionsInShow", __instance.CurrentShow == null ? -1 : __instance.CurrentShow.dataArray.Length);
    }

    private static void QuestionSelected(RuntimeSession __instance, int index)
    {
        var data = __instance.CurrentShow != null && index >= 0 && index < __instance.CurrentShow.dataArray.Length
            ? DescribeQuestion(__instance.CurrentShow.dataArray[index], index)
            : null;
        Log.Event("question.selected", "index", index, "teamAActive", __instance.TeamAActive, "question", data);
    }

    private static void AnswersPresentedNormal(ViewQuestionUI __instance, APlayerAgent activePlayer, APlayerAgent passivePlayer)
    {
        Log.Event("answers.presented", "phase", "normal", "active", activePlayer?.Name, "activeType", activePlayer?.GetType().Name,
            "team", TeamTag(activePlayer?.ParentTeam), "passive", passivePlayer?.Name, "correctIndex", __instance.GetCorrectAnswerIndex());
    }

    private static void AnswersPresentedMaster(ViewQuestionUI __instance, APlayerAgent playerTeamA, APlayerAgent playerTeamB)
    {
        Log.Event("answers.presented", "phase", "master", "a", playerTeamA?.Name, "b", playerTeamB?.Name, "correctIndex", __instance.GetCorrectAnswerIndex());
    }

    private static void AnswerSent(Team team, int index, bool correct, bool isAudience)
    {
        Log.Event("answer.locked", "team", TeamTag(team), "index", index, "correct", correct, "audience", isAudience, "state", LastState);
    }

    private static void BetSent(Team team) => Log.Event("bet.locked", "team", TeamTag(team), "bet", team.Bet, "score", team.GetScore());

    private static void CorrectRevealed(ViewQuestionUI __instance) => Log.Event("answer.revealed", "phase", "normal", "correctIndex", __instance.GetCorrectAnswerIndex());

    private static void MasterRevealed(ViewQuestionUI __instance) => Log.Event("answer.revealed", "phase", "master", "correctIndex", __instance.GetCorrectAnswerIndex());

    private static void BonusRevealed(float answer) => Log.Event("answer.revealed", "phase", "bonus", "answer", answer);

    private static void QuestionResult(RuntimeSession __instance, DataShowProgress progress)
    {
        Log.Event("question.finished", "index", __instance.ActiveQuestionIndex, "correct", progress.IsCorrect, "teamA", progress.AnsweredByTeamA,
            "scoreA", __instance.TeamA.GetScore(), "scoreB", __instance.TeamB.GetScore(), "count", __instance.GetQuestionCount());
    }

    private static void SessionSaved(bool isFinished) => Log.Event("session.saved", "finished", isFinished);

    private static void ScoreAdded(Team __instance, int amount) => Log.Event("score.changed", "team", TeamTag(__instance), "via", "AddScore", "amount", amount, "old", __instance.OldScore, "new", __instance.GetScore());

    private static void BetProcessed(Team __instance, bool correct) => Log.Event("score.changed", "team", TeamTag(__instance), "via", "ProcessBet", "correct", correct, "bet", __instance.Bet, "old", __instance.OldScore, "new", __instance.GetScore());

    private static void ScoresCleared(Team __instance) => Log.Event("score.changed", "team", TeamTag(__instance), "via", "ClearScores", "new", __instance.GetScore());

    private static void Outro(Team team)
    {
        var s = SDataAccess.ActiveSession;
        Log.Event("show.finished", "winner", TeamTag(team), "scoreA", s?.TeamA.GetScore(), "scoreB", s?.TeamB.GetScore());
    }

    private static void CategoryTurn(APlayerAgent player, bool isLast) => Log.Event("turn.category", "player", player?.Name, "team", TeamTag(player?.ParentTeam), "isLast", isLast);

    private static void NpcSelectionList(Dictionary<int, EMood> __result)
    {
        var map = new Dictionary<string, object?>();
        foreach (var kv in __result) map[kv.Key.ToString()] = kv.Value.ToString();
        Log.Event("bot.moods", "moods", map);
    }

    private static void ProfileWrite(DataCharacterProfile profile) => Log.Event("profile.write", "id", profile.Id, "name", profile.Name);

    private static void NameSet(APlayerAgent __instance, string value) => Log.Event("player.rename", "old", __instance.Name, "new", value, "type", __instance.GetType().Name);

    internal static APlayerAgent? ActivePlayerOf(ViewQuestionUI ui) => QuestionUiActivePlayer?.GetValue(ui) as APlayerAgent;
}
