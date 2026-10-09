using System;
using System.Collections.Generic;
using UnityEngine;

namespace WWDS.Game.Wwds.Mods.Ui;

public enum MenuCommand
{
    None,
    Up,
    Down,
    Left,
    Right,
    Submit,
    Back,
    PrevTab,
    NextTab,
    Close,
}

/// <summary>
/// Menu navigation from raw <see cref="Input"/> (keyboard and XInput pads), independent of InControl, which
/// is disabled while a mod menu is open (see <see cref="GameInputBlocker"/>). Directions auto-repeat on
/// unscaled time so menus also work while the game is paused (timeScale 0).
/// </summary>
public sealed class MenuInput
{
    private const float RepeatDelay = 0.4f;
    private const float RepeatRate = 0.09f;
    private const float StickThreshold = 0.6f;
    private static readonly string[] PadAxes = Axes();

    private readonly Dictionary<MenuCommand, float> _held = new();
    private readonly HashSet<string> _brokenAxes = new();

    private static string[] Axes()
    {
        // Unity's XInput mapping on Windows: left stick = analog 0/1 (Y inverted), D-pad = analog 5/6.
        var list = new List<string>();
        for (var pad = 1; pad <= 4; pad++)
        {
            list.Add("joystick " + pad + " analog 0");
            list.Add("joystick " + pad + " analog 1");
            list.Add("joystick " + pad + " analog 5");
            list.Add("joystick " + pad + " analog 6");
        }

        return list.ToArray();
    }

    /// <summary>The command of this frame (one per frame, directions repeat while held).</summary>
    public MenuCommand Poll()
    {
        if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || Input.GetKeyDown(KeyCode.Space)
            || Input.GetKeyDown(KeyCode.J) || Input.GetKeyDown(KeyCode.JoystickButton0)) return MenuCommand.Submit;
        if (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.Backspace) || Input.GetKeyDown(KeyCode.K)
            || Input.GetKeyDown(KeyCode.JoystickButton1)) return MenuCommand.Back;
        if (Input.GetKeyDown(KeyCode.JoystickButton7)) return MenuCommand.Close;
        if (Input.GetKeyDown(KeyCode.Q) || Input.GetKeyDown(KeyCode.PageUp) || Input.GetKeyDown(KeyCode.JoystickButton4)
            || (Input.GetKeyDown(KeyCode.Tab) && Shift())) return MenuCommand.PrevTab;
        if (Input.GetKeyDown(KeyCode.E) || Input.GetKeyDown(KeyCode.PageDown) || Input.GetKeyDown(KeyCode.JoystickButton5)
            || Input.GetKeyDown(KeyCode.Tab)) return MenuCommand.NextTab;

        var pad = PadDirection();
        foreach (var dir in new[] { MenuCommand.Up, MenuCommand.Down, MenuCommand.Left, MenuCommand.Right })
        {
            if (Repeat(dir, DirectionHeld(dir, pad))) return dir;
        }

        return MenuCommand.None;
    }

    /// <summary>True while any key, mouse or pad button is held (used to release the game's input safely).</summary>
    public static bool AnythingHeld()
    {
        if (Input.anyKey) return true;
        for (var b = KeyCode.JoystickButton0; b <= KeyCode.JoystickButton19; b++)
        {
            if (Input.GetKey(b)) return true;
        }

        return false;
    }

    private static bool Shift() => Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

    private static bool DirectionHeld(MenuCommand dir, Vector2 pad) => dir switch
    {
        MenuCommand.Up => Input.GetKey(KeyCode.UpArrow) || Input.GetKey(KeyCode.W) || pad.y > StickThreshold,
        MenuCommand.Down => Input.GetKey(KeyCode.DownArrow) || Input.GetKey(KeyCode.S) || pad.y < -StickThreshold,
        MenuCommand.Left => Input.GetKey(KeyCode.LeftArrow) || Input.GetKey(KeyCode.A) || pad.x < -StickThreshold,
        MenuCommand.Right => Input.GetKey(KeyCode.RightArrow) || Input.GetKey(KeyCode.D) || pad.x > StickThreshold,
        _ => false,
    };

    private bool Repeat(MenuCommand dir, bool held)
    {
        var now = Time.unscaledTime;
        if (!held)
        {
            _held.Remove(dir);
            return false;
        }

        if (!_held.TryGetValue(dir, out var next))
        {
            _held[dir] = now + RepeatDelay;
            return true;
        }

        if (now < next) return false;
        _held[dir] = now + RepeatRate;
        return true;
    }

    private Vector2 PadDirection()
    {
        var result = Vector2.zero;
        for (var i = 0; i < PadAxes.Length; i += 2)
        {
            var x = Axis(PadAxes[i]);
            var y = Axis(PadAxes[i + 1]);
            // Stick Y is inverted (analog 1), D-pad Y (analog 6) is not.
            if (i % 4 == 0) y = -y;
            if (Mathf.Abs(x) > Mathf.Abs(result.x)) result.x = x;
            if (Mathf.Abs(y) > Mathf.Abs(result.y)) result.y = y;
        }

        return result;
    }

    private float Axis(string name)
    {
        if (_brokenAxes.Contains(name)) return 0;
        try
        {
            return Input.GetAxisRaw(name);
        }
        catch (ArgumentException)
        {
            _brokenAxes.Add(name);
            return 0;
        }
    }
}
