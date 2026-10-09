using System;
using DoozyUI;
using InControl;
using UnityEngine;
using WWDS.ModCore.Logging;

namespace WWDS.Game.Wwds.Mods.Ui;

/// <summary>
/// Silences the game's input while a mod menu is open: InControl off
/// (stops every device poll, SActiveDeviceManager events and per-player input), DoozyUI back button off,
/// main-menu raw Return/Escape checks locked. Optionally pauses a running show like the game's pause menu.
/// Release waits until every key and button is up, so the closing key never reaches the game.
/// </summary>
public sealed class GameInputBlocker
{
    private readonly ModLog _log;
    private bool _blocked;
    private bool _releasing;
    private bool _lockedMainMenu;
    private bool _paused;
    private bool _backDisabled;
    private bool _inControlWasEnabled;

    public GameInputBlocker(ModLog log) => _log = log;

    public bool Active => _blocked || _releasing;

    /// <summary>True while this blocker holds the show paused.</summary>
    public bool Paused => _paused;

    public void Block(bool pauseShow)
    {
        if (_blocked || _releasing)
        {
            // Still blocked (or a release is pending): keep the saved game state, never capture our own block as it.
            _blocked = true;
            _releasing = false;
            if (pauseShow && !_paused) Pause();
            return;
        }

        _blocked = true;
        Safe("InControl", () =>
        {
            _inControlWasEnabled = InputManager.Enabled;
            InputManager.Enabled = false;
        });
        Safe("back button", () =>
        {
            UIManager.DisableBackButton();
            _backDisabled = true;
        });
        Safe("main menu lock", () =>
        {
            var menu = MainMenuController.Instance;
            if (menu != null && !menu.IsInputLocked())
            {
                menu.LockInput();
                _lockedMainMenu = true;
            }
        });
        if (pauseShow) Pause();
    }

    /// <summary>Starts releasing; the game gets its input back once nothing is held any more.</summary>
    public void Release()
    {
        if (!_blocked) return;
        _blocked = false;
        _releasing = true;
        if (_paused) Unpause();
    }

    /// <summary>Call every frame.</summary>
    public void Tick()
    {
        if (!_releasing || MenuInput.AnythingHeld()) return;
        _releasing = false;
        Safe("main menu unlock", () =>
        {
            if (_lockedMainMenu && MainMenuController.Instance != null) MainMenuController.Instance.UnlockInput();
            _lockedMainMenu = false;
        });
        Safe("back button", () =>
        {
            if (_backDisabled) UIManager.EnableBackButton();
            _backDisabled = false;
        });
        Safe("InControl", () =>
        {
            if (_inControlWasEnabled) InputManager.Enabled = true;
        });
    }

    private void Pause()
    {
        Safe("pause", () =>
        {
            if (UIManager.Instance.gamePaused) return;
            UIManager.TogglePause();
            if (SSoundManager.Instance != null) SSoundManager.Instance.PauseAllSounds();
            if (SVideoController.Instance != null) SVideoController.Instance.Pause();
            _paused = true;
        });
    }

    private void Unpause()
    {
        Safe("unpause", () =>
        {
            if (UIManager.Instance.gamePaused) UIManager.TogglePause();
            if (SSoundManager.Instance != null) SSoundManager.Instance.ResumeAllSounds();
            if (SVideoController.Instance != null) SVideoController.Instance.Resume();
        });
        _paused = false;
    }

    /// <summary>Forgets the pause without touching timeScale (the game reset it, e.g. a scene unload).</summary>
    public void DropPause() => _paused = false;

    private void Safe(string what, Action body)
    {
        try
        {
            body();
        }
        catch (Exception ex)
        {
            _log.Warn("Input blocker (" + what + "): " + ex.Message);
        }
    }
}
