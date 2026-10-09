using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace WWDS.Diagnostics;

[BepInPlugin(Guid, "WWDS Diagnostics", WWDS.WwdsVersion.Value)]
public sealed class DiagnosticsPlugin : BaseUnityPlugin
{
    public const string Guid = "wwds.diagnostics";

    private ProbeLog? _probe;
    private Autopilot? _autopilot;
    private float _nextHeartbeat;
    private bool _menuDumped;
    private bool _showDumped;
    private static readonly string[] UiDumpStates = { "StateScreenMainMenu", "StateScreenSelectPlayers", "StateShowIntro", "StateShowLoop" };
    private readonly System.Collections.Generic.List<string> _uiDumped = new();
    private string? _uiDumpState;
    private float _uiDumpAt;

    private void Awake()
    {
        var gameTag = Paths.ProcessName.IndexOf("WWDS2", StringComparison.OrdinalIgnoreCase) >= 0 ? "game2" : "game1";
        _probe = new ProbeLog(Logger, Path.Combine(Paths.BepInExRootPath, "wwds-diagnostics"), gameTag);
        _probe.Event("boot", "process", Paths.ProcessName, "unity", Application.unityVersion, "runtime", Environment.Version.ToString(),
            "acsHash", HashOf(typeof(SGameFSM).Assembly.Location), "file", _probe.FilePath);

        var harmony = new Harmony(Guid);
        ProbePatches.Apply(harmony, _probe);

        var enabled = Config.Bind("Autopilot", "Enabled", false, "Run an unattended bot-vs-bot show (blocks saves and achievements while active).");
        if (enabled.Value)
        {
            var s = new Autopilot.Settings
            {
                TimeScale = Config.Bind("Autopilot", "TimeScale", 1f, "Time.timeScale during the show.").Value,
                DriveTeamB = Config.Bind("Autopilot", "DriveTeamB", "Off", "Off | Correct | Wrong: answer for Team B from outside.").Value,
                RenameTeamB = Config.Bind("Autopilot", "RenameTeamB", "", "Rename Team B's bot before the show.").Value,
                QuitAfterShow = Config.Bind("Autopilot", "QuitAfterShow", true, "Quit the game after the outro.").Value,
                ShowId = Config.Bind("Autopilot", "ShowId", -1, "Show slot 1..30, -1 = game's choice.").Value,
            };
            _autopilot = new Autopilot(s, _probe);
            _autopilot.Install(harmony);

            // The game pauses its player loop when unfocused; an unattended run must keep going.
            Application.runInBackground = true;
        }
    }

    private void Update()
    {
        if (_probe == null) return;
        try
        {
            _autopilot?.Update();
            if (!_menuDumped && ProbePatches.LastState == "StateIntro")
            {
                _menuDumped = true;
                DumpMenuData();
            }

            if (!_showDumped && ProbePatches.LastState == "StateShowLoop")
            {
                _showDumped = true;
                DumpShowData();
            }

            var state = ProbePatches.LastState;
            if (state != _uiDumpState)
            {
                _uiDumpState = state;
                _uiDumpAt = Array.IndexOf(UiDumpStates, state) >= 0 && !_uiDumped.Contains(state) ? Time.realtimeSinceStartup + 6f : 0;
            }

            if (_uiDumpAt > 0 && Time.realtimeSinceStartup >= _uiDumpAt)
            {
                UiDump.Write(_probe, state);
                _uiDumped.Add(state);
                _uiDumpAt = 0;
            }

            if (Time.realtimeSinceStartup >= _nextHeartbeat)
            {
                _nextHeartbeat = Time.realtimeSinceStartup + 15f;
                var s = SDataAccess.ActiveSession;
                _probe.Event("heartbeat", "state", ProbePatches.LastState, "timeScale", Time.timeScale, "session", s != null,
                    "question", s?.ActiveQuestionIndex, "scoreA", s?.TeamA.GetScore(), "scoreB", s?.TeamB.GetScore());
            }
        }
        catch (Exception ex)
        {
            _probe.Event("error", "where", "Update", "message", ex.ToString());
        }
    }

    private void DumpMenuData()
    {
        var store = SDataStore.Instance;
        var npcs = store.AvailableNPCs.Select(p => (object?)(p.Id + ":" + p.Name)).ToList();
        var stars = store.AvailableCharacters.Select(p => (object?)(p.Id + ":" + p.Name)).ToList();
        var nameFields = Resources.FindObjectsOfTypeAll<ViewDialogChangeName>();
        var limits = new List<object?>();
        var limitField = AccessTools.Field(typeof(ViewDialogChangeName), "nameInputField");
        foreach (var f in nameFields)
        {
            var input = limitField?.GetValue(f) as TMPro.TMP_InputField;
            limits.Add(input == null ? -1 : input.characterLimit);
        }

        _probe!.Event("dump.menu", "normalShowQuestions", store.normalShowQuestions, "npcs", npcs, "characters", stars,
            "nameInputLimits", limits, "viewSelectNameObjects", Resources.FindObjectsOfTypeAll<ViewSelectName>().Length,
            "availableShows", SAssetBundleManager.Instance.GetAvailableShowIDs().Cast<object?>().ToList());
    }

    private void DumpShowData()
    {
        var betField = AccessTools.Field(typeof(ViewMasterBetAnswerFrame), "betValues");
        var bets = new List<object?>();
        foreach (var frame in Resources.FindObjectsOfTypeAll<ViewMasterBetAnswerFrame>())
        {
            if (betField?.GetValue(frame) is Array arr) bets.Add(arr.Cast<object?>().ToList());
        }

        var s = SDataAccess.ActiveSession;
        _probe!.Event("dump.show", "betValues", bets, "showIndex", s?.ActiveShowIndex,
            "rows", s?.CurrentShow == null ? -1 : s.CurrentShow.dataArray.Length);
    }

    private static string HashOf(string path)
    {
        try
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            using var fs = File.OpenRead(path);
            return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").Substring(0, 16);
        }
        catch (Exception ex)
        {
            return "error:" + ex.GetType().Name;
        }
    }

    private void OnDestroy() => _probe?.Dispose();
}
