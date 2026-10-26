using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using InControl;
using UnityEngine;
using WWDS.Game.Wwds.Mods.Ui;
using WWDS.ModCore.Config;
using WWDS.ModCore.Features;
using WWDS.Runtime.Patching;

namespace WWDS.Game.Wwds.Mods;

/// <summary>
/// General ESC menu: resume, save (in a running show), settings, mod menu, back to the main menu and
/// quit. Opens with Escape (also when no keyboard player joined, where the game ignores Escape) and
/// replaces the game's pause menu on the controller Start button. In the main menu it opens on the door
/// screen only, where Escape has no meaning for the game.
/// </summary>
public sealed class EscMenuFeature : FeatureBase
{
    public const string FeatureId = "EscMenu";

    private static EscMenuFeature? _current;
    private IConfigValue<bool> _replacePause = null!;
    private IConfigValue<string> _hotkey = null!;
    private IConfigValue<bool> _confirmQuitDoor = null!;
    private MenuWindow? _quitWindow;
    private bool _quitting;
    private ModUi _ui = null!;
    private MenuWindow? _window;
    private string _status = string.Empty;
    private int _openedByPatchFrame = -1;

    public override string Id => FeatureId;

    public override string DisplayName => "ESC menu";

    public override string Description => "Escape opens a menu with resume, save, settings, mod menu, main menu and quit.";

    protected override string LogCategoryName => "Mods";

    public IConfigValue<bool> ReplacePauseMenu => _replacePause;

    protected override void OnStart()
    {
        var c = Context.Config;
        _replacePause = c.Bind("EscMenu", "ReplacePauseMenu", true, "Use this menu instead of the game's pause menu (Start button) during a show.");
        _hotkey = c.Bind("EscMenu", "Hotkey", "Escape", "Key that opens the menu (UnityEngine.KeyCode name).");
        _confirmQuitDoor = c.Bind("EscMenu", "ConfirmQuitDoor", true, "Ask before the main menu's Quit door closes the game (the game quits at once).");
        _ui = ModUi.For(Context);
        _current = this;
        Context.Provide(this);
        _ui.Patch(new PatchSpec("mods.esc-menu", "Open the ESC menu instead of the game's pause menu.",
            () => AccessTools.Method(typeof(ShowController), "ShowMainMenu", new[] { typeof(InputDevice) }), ModCore.Capabilities.GameCapability.None)
        {
            Prefix = AccessTools.Method(typeof(EscMenuFeature), nameof(ShowMainMenuPrefix)),
        });
        _ui.Patch(new PatchSpec("mods.quit-confirm", "Ask before the Quit door closes the game.",
            () => AccessTools.Method(typeof(MainMenuController), "Submit", new[] { typeof(int) }), ModCore.Capabilities.GameCapability.None)
        {
            Prefix = AccessTools.Method(typeof(EscMenuFeature), nameof(SubmitPrefix)),
        });
    }

    protected override void OnStop()
    {
        _window?.Close();
        _current = null;
    }

    public override void Tick()
    {
        _ui.Tick();
        if (_ui.AnyOpen || _ui.Blocker.Active || !Input.GetKeyDown(ModUi.Key(_hotkey.Value))) return;
        if (Time.frameCount == _openedByPatchFrame) return;
        if (CanOpenHere()) Open();
    }

    /// <summary>Escape opens the menu only where the game has no own use for it.</summary>
    private bool CanOpenHere()
    {
        if (_ui.InShow) return !GamePauseMenuOpen();
        if (_ui.State != "StateScreenMainMenu") return false;
        var menu = MainMenuController.Instance;
        if (menu == null || menu.IsInputLocked()) return false;
        var t = typeof(MainMenuController);
        return !(bool)AccessTools.Field(t, "isInside").GetValue(menu) && !(bool)AccessTools.Field(t, "loadSavegame").GetValue(menu);
    }

    private static bool GamePauseMenuOpen() => PauseMenu() is { } p && p.IsPauseMenuActive();

    private static ViewPauseMenu? PauseMenu() => Resources.FindObjectsOfTypeAll<ViewPauseMenu>().FirstOrDefault(p => p != null && p.gameObject.scene.IsValid());

    public void Open()
    {
        _status = string.Empty;
        _window ??= Build();
        _ui.Show(_window);
    }

    private MenuWindow Build()
    {
        var w = _ui.CreateWindow("Pause", compact: true);
        w.Pages.Add(new MenuPage("Pause", Rows));
        return w;
    }

    private IEnumerable<Widget> Rows()
    {
        var inLoop = _ui.State is "StateShowLoop" or "StateShowTutorial";
        var mods = Context.Get<ModMenuFeature>();
        var quick = Context.Get<QuickplayFeature>();
        yield return new ButtonWidget("Fortsetzen", () => _window!.Close()) { Hint = "Zurück ins Spiel." };
        if (_ui.State == "StateScreenMainMenu" && quick != null && quick.Enabled.Value)
        {
            yield return new ButtonWidget("Schnellstart", () =>
            {
                _window!.Close();
                _status = quick.StartFromMainMenu();
            }) { Hint = "Startet eine Show mit der Aufstellung aus dem Mod-Menü (Schnellstart)." };
        }

        if (_ui.State == "StateShowLoop")
        {
            yield return new ButtonWidget("Speichern", Save, () => _status) { Hint = "Speichert den Stand der letzten abgeschlossenen Runde (das Spiel speichert sonst nach jeder Runde)." };
        }

        if (mods != null)
        {
            yield return new ButtonWidget("Einstellungen", () => mods.Open(ModMenuFeature.GamePage, _window)) { Hint = "Lautstärke, Auflösung, Fenstermodus, Bildrate. Zurück führt wieder hierher." };
            yield return new ButtonWidget("Mod-Menü", () => mods.Open(null, _window)) { Hint = "Twitch, Publikum, Komfort, alle Mod-Einstellungen. Zurück führt wieder hierher." };
        }

        if (inLoop)
        {
            yield return new ButtonWidget("Zurück zum Hauptmenü", () => _window!.Confirm("Show beenden und zum Hauptmenü? Der Stand der letzten Runde bleibt gespeichert.", "Zum Hauptmenü", EndShow))
            {
                Hint = "Beendet die Show wie im Pausenmenü des Spiels.",
            };
        }

        yield return new ButtonWidget("Spiel beenden", () => _window!.Confirm("Spiel wirklich beenden?", "Beenden", Quit)) { Hint = "Schließt das Spiel." };
    }

    private void Save()
    {
        var session = SDataAccess.ActiveSession;
        if (session == null || _ui.State != "StateShowLoop")
        {
            _status = "Nicht möglich";
            return;
        }

        session.Save();
        _status = "Gespeichert " + DateTime.Now.ToString("HH:mm:ss");
        _ui.Toast("Show gespeichert", 3, "save");
        Log.Info("Show saved from the ESC menu");
    }

    internal bool IsOpen => _window != null && _window.IsOpen;

    internal MenuWindow? WindowForTest => _window;

    internal void Close() => _window?.Close();

    internal void EndShow()
    {
        _window?.Close();
        var pause = PauseMenu();
        if (pause != null) pause.EndGame();
        else SGameFSM.Instance.SetInputAndContinue(2);
        Log.Info("Show ended from the ESC menu");
    }

    private void Quit()
    {
        _window?.Close();
        _quitWindow?.Close();
        Log.Info("Quit from the ESC menu");
        if (Context.Host != null) Context.Host.QuitGame();
        else Application.Quit();
    }

    internal void ShowQuitConfirmForTest() => ConfirmQuitDoor();

    internal void CloseQuitConfirm() => _quitWindow?.Close();

    private void ConfirmQuitDoor()
    {
        _quitting = false;
        if (_quitWindow == null)
        {
            _quitWindow = _ui.CreateWindow("Spiel beenden?", compact: true);
            _quitWindow.Pages.Add(new MenuPage("Beenden", QuitRows));
            _quitWindow.Closed += () =>
            {
                if (_quitting) return;
                // Back out of the Quit door like the game's Back(): leave the door and close it again.
                var menu = MainMenuController.Instance;
                if (menu == null) return;
                AccessTools.Field(typeof(MainMenuController), "isInside").SetValue(menu, false);
                (AccessTools.Property(typeof(MainMenuController), "currentDoor").GetValue(menu, null) as RepresentationMenuDoor)?.ResetDoor();
            };
        }

        _ui.Show(_quitWindow);
    }

    private IEnumerable<Widget> QuitRows()
    {
        yield return new InfoWidget("Das Spiel wird geschlossen. Eine laufende Show ist bis zur letzten Runde gespeichert.", 2);
        yield return new ButtonWidget("Zurück", () => _quitWindow!.Close());
        yield return new ButtonWidget("Spiel beenden", () =>
        {
            _quitting = true;
            Quit();
        });
    }

    private static bool SubmitPrefix(int selectedIndex)
    {
        try
        {
            var self = _current;
            if (self == null || !self._confirmQuitDoor.Value || selectedIndex != (int)MainMenuState.Quit) return true;
            self.ConfirmQuitDoor();
            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static bool ShowMainMenuPrefix()
    {
        try
        {
            var self = _current;
            if (self == null || !self._replacePause.Value) return true;
            if (!self._ui.AnyOpen && !self._ui.Blocker.Active)
            {
                self._openedByPatchFrame = Time.frameCount;
                self.Open();
            }

            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }
}
