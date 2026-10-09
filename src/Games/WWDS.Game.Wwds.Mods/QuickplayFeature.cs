using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using InControl;
using WWDS.Audience.Opponents;
using WWDS.ModCore.Config;
using WWDS.ModCore.Features;
using WWDS.ModCore.Model;
using WWDS.Runtime.Patching;

namespace WWDS.Game.Wwds.Mods;

/// <summary>Who sits on a quickplay seat.</summary>
public enum Seat
{
    Empty,
    Keyboard,
    Gamepad1,
    Gamepad2,
    Gamepad3,
    Gamepad4,
    Bot,
    Twitch,
}

/// <summary>
/// Quickplay: the player selection screen fills itself from a configured line-up (up to two seats per
/// team: keyboard, gamepads, game AI or the Twitch audience) with fixed characters, skips the tutorial
/// question and starts after a short countdown. It drives the game's own selection view (register,
/// confirm, countdown, session creation), so the result is a normal show.
/// A resumed show reconnects the saved humans to the line-up devices instead.
/// </summary>
public sealed class QuickplayFeature : FeatureBase
{
    public const string FeatureId = "Quickplay";
    public static readonly string[] SeatKeys = { "TeamA1", "TeamA2", "TeamB1", "TeamB2" };

    private static QuickplayFeature? _current;
    private readonly IConfigValue<Seat>[] _seats = new IConfigValue<Seat>[4];
    private readonly IConfigValue<int>[] _characters = new IConfigValue<int>[4];
    private IConfigValue<bool> _enabled = null!;
    private IConfigValue<bool> _resume = null!;
    private IConfigValue<int> _countdown = null!;
    private IConfigValue<int> _showId = null!;
    private ModUi _ui = null!;
    private bool _handled;

    public override string Id => FeatureId;

    public override string DisplayName => "Quickplay";

    public override string Description => "Start shows with a configured line-up instead of the team and character selection.";

    protected override string LogCategoryName => "Mods";

    public IConfigValue<bool> Enabled => _enabled;

    public IConfigValue<bool> QuickResume => _resume;

    public IConfigValue<int> Countdown => _countdown;

    public IConfigValue<int> ShowId => _showId;

    public string LastMessage { get; private set; } = string.Empty;

    public Seat GetSeat(int index) => _seats[index].Value;

    public void SetSeat(int index, Seat seat) => _seats[index].Value = seat;

    /// <summary>Character profile id for a human seat, -1 = first free.</summary>
    public int GetCharacter(int index) => _characters[index].Value;

    public void SetCharacter(int index, int id) => _characters[index].Value = id;

    protected override void OnStart()
    {
        var c = Context.Config;
        const string s = "Quickplay";
        _enabled = c.Bind(s, "Enabled", true, "Fill the player selection from the line-up below (the Main door starts the show directly).");
        _resume = c.Bind(s, "QuickResume", true, "When resuming a saved show, reconnect its players to the line-up devices automatically.");
        _countdown = c.Bind(s, "CountdownSeconds", 1, "Start countdown on the selection screen (game default 3).");
        _showId = c.Bind(s, "ShowId", -1, "Show to play (1..30), -1 = the game's next unplayed show.");
        var defaults = new[] { Seat.Keyboard, Seat.Empty, Seat.Bot, Seat.Empty };
        for (var i = 0; i < 4; i++)
        {
            _seats[i] = c.Bind(s, SeatKeys[i], defaults[i], "Seat " + SeatKeys[i] + ": Empty | Keyboard | Gamepad1..4 | Bot (game AI) | Twitch (bot played by the audience).");
            _characters[i] = c.Bind(s, SeatKeys[i] + "Character", -1, "Character profile id for a human on " + SeatKeys[i] + " (-1 = first free).");
        }

        _current = this;
        Context.Provide(this);
        _ui = ModUi.For(Context);
        _ui.Patch(new PatchSpec("mods.quickplay", "Fill the player selection once it accepts input.",
            () => AccessTools.Method(typeof(ViewSelectPlayers), "EnableInput"), ModCore.Capabilities.GameCapability.None)
        {
            Postfix = AccessTools.Method(typeof(QuickplayFeature), nameof(EnableInputPostfix)),
        });
        Track(_ui.OnState(state =>
        {
            if (state != "StateScreenSelectPlayers") _handled = false;
        }));
    }

    protected override void OnStop() => _current = null;

    /// <summary>Line-up problems that make quickplay fall back to the normal selection (empty = fine).</summary>
    public string Validate()
    {
        var a = _seats.Take(2).Count(x => x.Value != Seat.Empty);
        var b = _seats.Skip(2).Count(x => x.Value != Seat.Empty);
        if (a == 0 || b == 0) return "Jedes Team braucht mindestens einen Platz.";
        var devices = _seats.Select(x => x.Value).Where(IsHuman).ToList();
        if (devices.Count != devices.Distinct().Count()) return "Ein Gerät ist doppelt vergeben.";
        if (_seats.Count(x => x.Value == Seat.Twitch) > 1) return "Nur ein Platz kann Twitch sein.";
        return string.Empty;
    }

    public static bool IsHuman(Seat seat) => seat is Seat.Keyboard or Seat.Gamepad1 or Seat.Gamepad2 or Seat.Gamepad3 or Seat.Gamepad4;

    /// <summary>The InControl device for a human seat, or null if not connected.</summary>
    public static InputDevice? DeviceFor(Seat seat)
    {
        if (seat == Seat.Keyboard)
        {
            return InputManager.Devices.OfType<DeviceKeyboard>().OrderBy(k => k.DeviceId).FirstOrDefault();
        }

        var pads = InputManager.Devices.Where(d => !(d is DeviceKeyboard) && d.IsAttached).ToList();
        var index = seat - Seat.Gamepad1;
        return index >= 0 && index < pads.Count ? pads[index] : null;
    }

    /// <summary>Connected devices as seat names, for the menu.</summary>
    public static IList<string> ConnectedDevices()
    {
        var list = new List<string> { "Keyboard" };
        var pads = InputManager.Devices.Where(d => !(d is DeviceKeyboard) && d.IsAttached).ToList();
        for (var i = 0; i < pads.Count; i++) list.Add("Gamepad" + (i + 1) + " (" + pads[i].Name + ")");
        return list;
    }

    /// <summary>
    /// Presses the Main door from the door screen (as a door confirm does: inside flag, welcome line
    /// stopped). A saved unfinished show still asks resume or new, like the game.
    /// </summary>
    public string StartFromMainMenu()
    {
        var menu = MainMenuController.Instance;
        if (menu == null || _ui.State != "StateScreenMainMenu") return "Nur im Hauptmenü möglich.";
        var t = typeof(MainMenuController);
        if ((bool)AccessTools.Field(t, "isInside").GetValue(menu) || menu.IsInputLocked()) return "Hauptmenü ist beschäftigt.";
        AccessTools.Field(t, "isInside").SetValue(menu, true);
        if (AccessTools.Field(t, "delayMessageCoroutine").GetValue(menu) is UnityEngine.Coroutine delay) menu.StopCoroutine(delay);
        AccessTools.Method(t, "Submit", new[] { typeof(int) }).Invoke(menu, new object[] { (int)MainMenuState.Main });
        return "Show wird gestartet.";
    }

    private static void EnableInputPostfix(ViewSelectPlayers __instance)
    {
        PatchGuard.Run("mods.quickplay", () =>
        {
            var self = _current;
            if (self == null || !self._enabled.Value || self._handled) return;
            self._handled = true;
            self.Context.MainThread.Post(() => PatchGuard.Run("mods.quickplay.run", () => self.Run(__instance)));
        });
    }

    private void Run(ViewSelectPlayers view)
    {
        if (view == null || _ui.State != "StateScreenSelectPlayers") return;
        var t = typeof(ViewSelectPlayers);
        var recreate = (bool)AccessTools.Field(t, "recreateMatch").GetValue(view);
        if (recreate)
        {
            if (_resume.Value) Resume(view);
            return;
        }

        if ((bool)AccessTools.Field(t, "isTutorialFromArchive").GetValue(view))
        {
            Report("Tutorial aus dem Archiv: normale Auswahl.");
            return;
        }

        var problem = Validate();
        if (problem.Length > 0)
        {
            Report("Schnellstart übersprungen: " + problem);
            return;
        }

        var seats = _seats.Select(x => x.Value).ToArray();
        var devices = new InputDevice?[4];
        for (var i = 0; i < 4; i++)
        {
            if (!IsHuman(seats[i])) continue;
            devices[i] = DeviceFor(seats[i]);
            if (devices[i] == null)
            {
                Report("Schnellstart übersprungen: " + seats[i] + " ist nicht verbunden.");
                return;
            }
        }

        // No tutorial question, short countdown, chosen show.
        AccessTools.Field(t, "isTutorial").SetValue(view, false);
        AccessTools.Field(t, "startUpTime").SetValue(view, Math.Max(1, _countdown.Value));
        var maxShow = Context.Get<CatalogueFeature>()?.Active == true ? 2 * CatalogueFeature.HostShows : CatalogueFeature.HostShows;
        if (_showId.Value >= 1 && _showId.Value <= maxShow) AccessTools.Field(t, "selectedShowID").SetValue(view, _showId.Value);
        var store = SDataStore.Instance;
        if (!store.SettingsData.PlayedTutorial1P)
        {
            store.SettingsData.PlayedTutorial1P = true;
            store.SaveSettings();
        }

        var registerA = AccessTools.Method(t, "RegisterPlayerTeamA");
        var registerB = AccessTools.Method(t, "RegisterPlayerTeamB");
        var createBot = AccessTools.Method(t, "CreateAiPlayer");
        var registered = (Dictionary<InputDevice, PlayerAgentLocal>)AccessTools.Field(t, "registeredDevices").GetValue(view);
        var used = new HashSet<int>();
        TeamSlot? twitch = null;
        var lineup = new List<string>();
        // Bots first, then humans; every registration is already complete, so the countdown runs once.
        foreach (var i in new[] { 0, 1, 2, 3 }.OrderBy(i => IsHuman(seats[i]) ? 1 : 0))
        {
            if (seats[i] == Seat.Empty) continue;
            var teamA = i < 2;
            APlayerAgent agent;
            if (IsHuman(seats[i]))
            {
                var local = new PlayerAgentLocal();
                local.UpdateData(PickCharacter(_characters[i].Value, used), completeRefresh: true);
                if (local.Input is InputPlayer input) input.Device = devices[i];
                registered[devices[i]!] = local;
                agent = local;
            }
            else
            {
                agent = (PlayerAgentBot)createBot.Invoke(view, null);
                if (seats[i] == Seat.Twitch) twitch = teamA ? TeamSlot.A : TeamSlot.B;
            }

            (teamA ? registerA : registerB).Invoke(view, new object[] { agent, PlayerLoginStates.CompleteSelection });
            lineup.Add(SeatKeys[i] + "=" + seats[i] + " (" + agent.Name + ")");
        }

        Context.Get<OpponentFeature>()?.PlanNextShow(twitch);
        AccessTools.Method(t, "ContinueSessionState").Invoke(view, null);
        Report("Schnellstart: " + string.Join(", ", lineup.ToArray()));
    }

    private static DataCharacterProfile PickCharacter(int wanted, HashSet<int> used)
    {
        var all = SDataStore.Instance.AvailableCharacters;
        foreach (var p in all)
        {
            if (p.Id == wanted && used.Add(p.Id)) return p;
        }

        foreach (var p in all)
        {
            if (used.Add(p.Id)) return p;
        }

        return all[0];
    }

    private void Resume(ViewSelectPlayers view)
    {
        var t = typeof(ViewSelectPlayers);
        var teamA = (Team)AccessTools.Field(t, "teamA").GetValue(view);
        var teamB = (Team)AccessTools.Field(t, "teamB").GetValue(view);
        var registered = (Dictionary<InputDevice, PlayerAgentLocal>)AccessTools.Field(t, "registeredDevices").GetValue(view);
        var slotOf = AccessTools.Method(t, "GetOccupiedViewSlot");
        var confirm = AccessTools.Method(typeof(ViewPlayerSlot), "ProcessConfirmInput");
        var humansA = teamA.Count(a => a is PlayerAgentLocal);
        var humansB = teamB.Count(a => a is PlayerAgentLocal);
        var devices = new Queue<InputDevice>(_seats.Select(x => x.Value).Where(IsHuman).Select(DeviceFor).Where(d => d != null).Select(d => d!));
        if (devices.Count < humansA + humansB)
        {
            Report("Fortsetzen: zu wenige Geräte für " + (humansA + humansB) + " Spieler, bitte manuell verbinden.");
            return;
        }

        void Connect(int count, bool a)
        {
            for (var n = 0; n < count; n++)
            {
                var device = devices.Dequeue();
                if (a) view.ReconnectToPlayerTeamA(device);
                else view.ReconnectToPlayerTeamB(device);
                if (!registered.TryGetValue(device, out var agent)) continue;
                if (slotOf.Invoke(view, new object[] { agent }) is ViewPlayerSlot slot && !slot.SelectionCompleted) confirm.Invoke(slot, null);
            }
        }

        AccessTools.Field(t, "startUpTime").SetValue(view, Math.Max(1, _countdown.Value));
        Connect(humansA, true);
        Connect(humansB, false);
        Report("Fortsetzen: " + (humansA + humansB) + " Spieler automatisch verbunden.");
    }

    private void Report(string message)
    {
        LastMessage = message;
        Log.Info(message);
        _ui.Toast(message, 5, "quickplay");
    }
}
