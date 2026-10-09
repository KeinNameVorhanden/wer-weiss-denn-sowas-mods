using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace WWDS.Diagnostics;

/// <summary>
/// Opt-in unattended test driver: walks from the main menu into a bot-vs-bot show, optionally
/// drives Team B's answers from outside (the mechanism a Twitch opponent will use) and renames
/// Team B's bot. While active, savegame writes and Steam achievement unlocks are suppressed so a
/// test run leaves no trace in the player's progress.
/// </summary>
internal sealed class Autopilot
{
    public sealed class Settings
    {
        public float TimeScale = 1f;
        public string DriveTeamB = "Off"; // Off | Correct | Wrong
        public string RenameTeamB = "";
        public bool QuitAfterShow = true;
        public int ShowId = -1;
    }

    private enum Phase { WaitMainMenu, WaitSelection, InShow, Outro, Done }

    private static Autopilot? _active;
    private static readonly MethodInfo? MenuSubmit = AccessTools.Method(typeof(MainMenuController), "Submit", new[] { typeof(int) });
    private static readonly MethodInfo? CreateAiPlayer = AccessTools.Method(typeof(ViewSelectPlayers), "CreateAiPlayer");
    private static readonly FieldInfo? SelectedShowId = AccessTools.Field(typeof(ViewSelectPlayers), "selectedShowID");
    private static readonly FieldInfo? SelectedAnswerButton = AccessTools.Field(typeof(ViewQuestionUI), "selectedAnswerButton");
    private static readonly FieldInfo? AnswerButtons = AccessTools.Field(typeof(ViewQuestionUI), "answerButtons");
    private static readonly MethodInfo? ConfirmMaster = AccessTools.Method(typeof(ViewQuestionUI), "ConfirmAnswerButton", new[] { typeof(Team), typeof(int) });

    private readonly Settings _settings;
    private readonly ProbeLog _log;
    private Phase _phase = Phase.WaitMainMenu;
    private float _phaseSince;

    public Autopilot(Settings settings, ProbeLog log)
    {
        _settings = settings;
        _log = log;
    }

    public void Install(Harmony harmony)
    {
        _active = this;
        ProbePatches.Try(harmony, AccessTools.Method(typeof(SSaveData), "Save"), Pre(nameof(BlockSave)), null, "autopilot:SSaveData.Save");
        ProbePatches.Try(harmony, AccessTools.Method(typeof(SPlayerStatistics), "UnlockTrophy"), Pre(nameof(BlockTrophy)), null, "autopilot:SPlayerStatistics.UnlockTrophy");
        ProbePatches.Try(harmony, AccessTools.Method(typeof(AchievementManagerSteam), "UnlockTrophy"), Pre(nameof(BlockSteamTrophy)), null, "autopilot:AchievementManagerSteam.UnlockTrophy");
        if (!string.Equals(_settings.DriveTeamB, "Off", StringComparison.OrdinalIgnoreCase))
        {
            ProbePatches.Try(harmony, AccessTools.Method(typeof(ViewQuestionUI), "NPCSelectQuestionSequence"), Pre(nameof(DriveNormal)), null, "autopilot:ViewQuestionUI.NPCSelectQuestionSequence");
            ProbePatches.Try(harmony, AccessTools.Method(typeof(ViewQuestionUI), "OnWaitForNPCAnswer", new[] { typeof(APlayerAgent) }), Pre(nameof(DriveMaster)), null, "autopilot:ViewQuestionUI.OnWaitForNPCAnswer");
        }

        _log.Event("autopilot.installed", "timeScale", _settings.TimeScale, "driveTeamB", _settings.DriveTeamB, "renameTeamB", _settings.RenameTeamB, "showId", _settings.ShowId);
    }

    private static HarmonyMethod Pre(string name) => new(typeof(Autopilot).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));

    public void Update()
    {
        var now = Time.realtimeSinceStartup;
        if (_settings.TimeScale > 0f && _phase == Phase.InShow && Math.Abs(Time.timeScale - _settings.TimeScale) > 0.001f && Time.timeScale > 0.01f)
        {
            Time.timeScale = _settings.TimeScale;
        }

        switch (_phase)
        {
            case Phase.WaitMainMenu:
                if (ProbePatches.LastState == "StateScreenMainMenu" && MainMenuController.Instance != null)
                {
                    if (_phaseSince == 0f) _phaseSince = now;
                    if (now - _phaseSince > 8f) EnterSelection(now);
                }

                break;
            case Phase.WaitSelection:
                if (ProbePatches.LastState == "StateScreenSelectPlayers" && now - _phaseSince > 4f) StartBotShow(now);
                else if (now - _phaseSince > 60f) Fail("selection screen not reached");
                break;
            case Phase.InShow:
                if (ProbePatches.LastState == "StateShowOutro") Switch(Phase.Outro, now);
                break;
            case Phase.Outro:
                if (now - _phaseSince > 15f)
                {
                    _log.Event("autopilot.done", "quit", _settings.QuitAfterShow);
                    Switch(Phase.Done, now);
                    if (_settings.QuitAfterShow) Application.Quit();
                }

                break;
        }
    }

    private void Switch(Phase phase, float now)
    {
        _phase = phase;
        _phaseSince = now;
        _log.Event("autopilot.phase", "phase", phase.ToString());
    }

    private void Fail(string reason)
    {
        _log.Event("autopilot.failed", "reason", reason, "state", ProbePatches.LastState);
        _phase = Phase.Done;
    }

    private void EnterSelection(float now)
    {
        if (MenuSubmit == null)
        {
            Fail("MainMenuController.Submit(int) not found");
            return;
        }

        _log.Event("autopilot.action", "action", "MainMenu.Submit(Main)");
        MenuSubmit.Invoke(MainMenuController.Instance, new object[] { (int)MainMenuState.Main });
        Switch(Phase.WaitSelection, now);
    }

    private void StartBotShow(float now)
    {
        var view = UnityEngine.Object.FindObjectOfType<ViewSelectPlayers>();
        if (view == null || CreateAiPlayer == null || SelectedShowId == null)
        {
            Fail("ViewSelectPlayers or its members not found");
            return;
        }

        var botA = (PlayerAgentBot)CreateAiPlayer.Invoke(view, null);
        var botB = (PlayerAgentBot)CreateAiPlayer.Invoke(view, null);
        if (_settings.RenameTeamB.Length > 0) botB.Name = _settings.RenameTeamB;

        var teamA = new Team();
        var teamB = new Team();
        teamA.Add(botA);
        teamB.Add(botB);
        teamA.SetAudienceCount(60);
        teamB.SetAudienceCount(55);
        teamA.ClearScores();
        teamB.ClearScores();

        var showId = _settings.ShowId > 0 ? _settings.ShowId : (int)SelectedShowId.GetValue(view);
        _log.Event("autopilot.action", "action", "StartBotShow", "show", showId, "botA", botA.Name, "botB", botB.Name);
        SDataAccess.ActiveSession = new RuntimeSession(teamA, teamB, showId, false, 0f);
        SGameFSM.Instance.SetInputAndContinue(0);
        Switch(Phase.InShow, now);
    }

    // ---- driving Team B (the mechanism a Twitch-controlled contestant will use) ---------------

    private static bool IsDrivenTeam(APlayerAgent? player)
    {
        var s = SDataAccess.ActiveSession;
        return _active != null && player != null && s != null && ReferenceEquals(player.ParentTeam, s.TeamB);
    }

    private static int ChooseIndex(ViewQuestionUI ui)
    {
        var correct = ui.GetCorrectAnswerIndex();
        return string.Equals(_active!._settings.DriveTeamB, "Wrong", StringComparison.OrdinalIgnoreCase) ? (correct + 1) % 3 : correct;
    }

    private static bool DriveNormal(ViewQuestionUI __instance, ref IEnumerator __result)
    {
        var player = ProbePatches.ActivePlayerOf(__instance);
        if (!IsDrivenTeam(player)) return true;
        __result = DriveNormalRoutine(__instance);
        return false;
    }

    private static IEnumerator DriveNormalRoutine(ViewQuestionUI ui)
    {
        yield return new WaitForSeconds(2f);
        var index = ChooseIndex(ui);
        var buttons = (ViewAnswerButton[])AnswerButtons!.GetValue(ui);
        SelectedAnswerButton!.SetValue(ui, buttons[index]);
        _active!._log.Event("autopilot.drive", "phase", "normal", "index", index);
        ui.SendAnswer(index);
    }

    private static bool DriveMaster(ViewQuestionUI __instance, APlayerAgent player, ref IEnumerator __result)
    {
        if (!IsDrivenTeam(player)) return true;
        __result = DriveMasterRoutine(__instance, player.ParentTeam);
        return false;
    }

    private static IEnumerator DriveMasterRoutine(ViewQuestionUI ui, Team team)
    {
        yield return new WaitForSeconds(3f);
        var index = ChooseIndex(ui);
        _active!._log.Event("autopilot.drive", "phase", "master", "index", index);
        ConfirmMaster!.Invoke(ui, new object[] { team, index });
    }

    // ---- side-effect suppression -------------------------------------------------------------

    private static bool BlockSave(string file)
    {
        _active?._log.Event("autopilot.blocked", "what", "save", "file", file);
        return false;
    }

    private static bool BlockTrophy(int trophyID)
    {
        _active?._log.Event("autopilot.blocked", "what", "trophy", "id", trophyID);
        return false;
    }

    private static bool BlockSteamTrophy(string apiName)
    {
        _active?._log.Event("autopilot.blocked", "what", "steamTrophy", "api", apiName);
        return false;
    }
}
