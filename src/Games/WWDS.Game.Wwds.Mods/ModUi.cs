using System;
using System.Collections.Generic;
using UnityEngine;
using WWDS.Game.Wwds.Mods.Ui;
using WWDS.ModCore;
using WWDS.ModCore.Events;
using WWDS.ModCore.Logging;
using WWDS.Runtime.Patching;

namespace WWDS.Game.Wwds.Mods;

/// <summary>
/// Shared runtime of the player-facing mods: the overlay canvas, the game input blocker, the Harmony
/// patches of the mods and the game state they react to. One window is open at a time; the game gets its
/// input back when the last one closes.
/// </summary>
public sealed class ModUi
{
    public static readonly string[] ShowStates = { "StateShowIntro", "StateShowTutorial", "StateShowLoop", "StateShowOutro" };

    private readonly ModContext _ctx;
    private readonly List<MenuWindow> _open = new();
    private UiRoot? _root;
    private int _lastTick = -1;
    private ToastLayer? _toasts;
    private readonly Dictionary<MenuWindow, MenuWindow> _returnTo = new();
    private bool _switching;

    public ModUi(ModContext ctx)
    {
        _ctx = ctx;
        Log = ctx.Log.For(LogCategory.UI);
        Blocker = new GameInputBlocker(Log);
        Patches = new PatchRegistry("wwds.harness.mods", ctx.Config, ctx.Log.For("Mods"));
        MenuScale = ctx.Config.Bind("UI", "MenuScale", 1f, "Size of the mod menus (0.6 to 1.4; capped so a menu always fits the screen).");
        HudScale = ctx.Config.Bind("UI", "HudScale", 1f, "Size of the vote window (0.5 to 2).");
        ToastsEnabled = ctx.Config.Bind("UI", "Toasts", true, "Short notifications at the top of the screen (saved, Twitch connected, login code, ...).");
        ctx.Bus.Subscribe<GameStateChanged>(e =>
        {
            State = e.State;
            // StateLoadShowContentScene clears gamePaused without restoring timeScale.
            if (e.State == "StateLoadShowContentScene" || e.State == "StateShowUnload") Blocker.DropPause();
            StateChanged?.Invoke(e.State);
        });
        Pad = new VirtualPad(this);
    }

    public VirtualPad Pad { get; }

    public WWDS.ModCore.Config.IConfigValue<bool> ToastsEnabled { get; }

    /// <summary>Game-styled notification (no-op while [UI] Toasts is off).</summary>
    public void Toast(string text, float seconds = 4, string? key = null)
    {
        if (!ToastsEnabled.Value) return;
        _toasts ??= new ToastLayer(Root);
        _toasts.Show(text, seconds, key);
    }

    public void DismissToast(string key) => _toasts?.Dismiss(key);

    public ModLog Log { get; }

    public WWDS.ModCore.Config.IConfigValue<float> MenuScale { get; }

    public WWDS.ModCore.Config.IConfigValue<float> HudScale { get; }

    public GameInputBlocker Blocker { get; }

    public PatchRegistry Patches { get; }

    /// <summary>Current game FSM state name (empty until the first state).</summary>
    public string State { get; private set; } = string.Empty;

    public bool InShow => Array.IndexOf(ShowStates, State) >= 0;

    public event Action<string>? StateChanged;

    /// <summary>Subscribes to state changes; dispose to unsubscribe (features Track it).</summary>
    public IDisposable OnState(Action<string> handler)
    {
        StateChanged += handler;
        return new Subscription(() => StateChanged -= handler);
    }

    /// <summary>Adds and applies a patch once per process (features may be stopped and started again).</summary>
    public void Patch(PatchSpec spec)
    {
        foreach (var existing in Patches.Patches)
        {
            if (existing.Id == spec.Id) return;
        }

        Patches.Apply(Patches.Add(spec));
    }

    /// <summary>The overlay canvas, created on first use (needs a running Unity scene).</summary>
    public UiRoot Root => _root ??= new UiRoot(new GameStyle());

    public bool AnyOpen => _open.Count > 0;

    public MenuWindow CreateWindow(string title, bool compact = false)
    {
        var w = new MenuWindow(Root, title, compact) { Scale = () => Mathf.Clamp(MenuScale.Value, 0.6f, 1.4f) };
        w.Closed += () =>
        {
            _open.Remove(w);
            if (!_switching && _returnTo.TryGetValue(w, out var parent))
            {
                // Back: return to the menu this one was opened from (e.g. ESC menu -> settings -> back).
                _returnTo.Remove(w);
                Show(parent);
                return;
            }

            if (_open.Count == 0 && !_switching) Blocker.Release();
        };
        return w;
    }

    /// <summary>
    /// Opens <paramref name="window"/> (closing any other) and blocks the game; a show is paused. With
    /// <paramref name="returnTo"/>, closing it (back) reopens that menu instead of returning to the game.
    /// </summary>
    public void Show(MenuWindow window, int page = 0, MenuWindow? returnTo = null)
    {
        _switching = true;
        try
        {
            foreach (var other in _open.ToArray())
            {
                if (other != window) other.Close();
            }
        }
        finally
        {
            _switching = false;
        }

        if (returnTo != null && returnTo != window) _returnTo[window] = returnTo;
        else _returnTo.Remove(window);
        window.CloseLabel = returnTo != null ? "< Zurück" : "Schließen";

        Blocker.Block(InShow);
        if (!_open.Contains(window)) _open.Add(window);
        Root.Style.Refresh();
        window.Open(page);
    }

    /// <summary>Closes a window for good (no return to the menu it came from), e.g. before starting a show.</summary>
    public void CloseForGood(MenuWindow window)
    {
        _returnTo.Remove(window);
        window.Close();
    }

    /// <summary>Call once per frame from a feature tick.</summary>
    public void Tick()
    {
        if (_lastTick == Time.frameCount) return;
        _lastTick = Time.frameCount;
        foreach (var w in _open.ToArray()) w.Tick();
        Blocker.Tick();
        if (_toasts != null)
        {
            _toasts.Scale = Mathf.Clamp(MenuScale.Value, 0.6f, 1.4f);
            _toasts.Tick();
        }
    }

    /// <summary>Parses a KeyCode name; unknown names give None.</summary>
    public static KeyCode Key(string name)
    {
        try
        {
            return (KeyCode)Enum.Parse(typeof(KeyCode), name.Trim(), true);
        }
        catch (ArgumentException)
        {
            return KeyCode.None;
        }
    }

    /// <summary>The shared instance (created by the first mod feature that starts).</summary>
    public static ModUi For(ModContext ctx)
    {
        var existing = ctx.Get<ModUi>();
        if (existing != null) return existing;
        var ui = new ModUi(ctx);
        ctx.Provide(ui);
        return ui;
    }

    private sealed class Subscription : IDisposable
    {
        private Action? _dispose;

        public Subscription(Action dispose) => _dispose = dispose;

        public void Dispose()
        {
            _dispose?.Invoke();
            _dispose = null;
        }
    }
}
