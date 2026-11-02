using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using InControl;
using UnityEngine;
using WWDS.Game.Wwds.Mods.Ui;
using WWDS.ModCore.Config;
using WWDS.ModCore.Features;
using WWDS.ModCore.Input;
using WWDS.Runtime.Patching;

namespace WWDS.Game.Wwds.Mods;

/// <summary>
/// Free keys for both keyboard players: each of the game's ten buttons (directions, A, B, X, Y, the two
/// shoulders) and player 2's answer shortcuts can be put on any key. The game's key symbols follow:
/// a rebound key is shown as a drawn key cap with its label. Escape and Enter stay fixed.
/// </summary>
public sealed class KeyBindingsFeature : FeatureBase
{
    public const string FeatureId = "KeyBindings";

    private static readonly Dictionary<string, string> Fields = new()
    {
        ["Up"] = "Up", ["Down"] = "Down", ["Left"] = "Left", ["Right"] = "Right",
        ["A"] = "A", ["B"] = "B", ["X"] = "X", ["Y"] = "Y", ["LB"] = "L", ["RB"] = "R",
    };

    private static KeyBindingsFeature? _current;
    private readonly Dictionary<string, IConfigValue<string>> _keys = new();

    public override string Id => FeatureId;

    public override string DisplayName => "Key bindings";

    public override string Description => "Any key for every button of both keyboard players; the on-screen key symbols follow.";

    protected override string LogCategoryName => "Mods";

    protected override void OnStart()
    {
        foreach (var player in new[] { 1, 2 })
        {
            foreach (var button in KeyBindings.Buttons.Concat(player == 2 ? KeyBindings.P2Shortcuts : new string[0]))
            {
                var name = Name(player, button);
                _keys[name] = Context.Config.Bind("Keys", name, KeyBindings.Default(player, button), "Key of player " + player + " for " + button + " (UnityEngine.KeyCode name).");
            }
        }

        _current = this;
        Context.Provide(this);
        var ui = ModUi.For(Context);
        var none = ModCore.Capabilities.GameCapability.None;
        ui.Patch(new PatchSpec("mods.keys.device", "New keyboard devices get the configured keys.",
            () => AccessTools.Constructor(typeof(DeviceKeyboard), new[] { typeof(int) }), none)
        {
            Postfix = AccessTools.Method(typeof(KeyBindingsFeature), nameof(DevicePostfix)),
        });
        ui.Patch(new PatchSpec("mods.keys.glyphs", "Key symbols show the configured keys.",
            () => AccessTools.Method(typeof(SDataStore), nameof(SDataStore.GetSpriteFaceForButton)), none)
        {
            Postfix = AccessTools.Method(typeof(KeyBindingsFeature), nameof(GlyphPostfix)),
        });
        ApplyAll();
    }

    protected override void OnStop()
    {
        _current = null;
        KeyboardLayout.P2AnswerKeys = new[] { "X", "C", "V" };
    }

    public static string Name(int player, string button) => "P" + player + button;

    /// <summary>Configured key (KeyCode name) of a button.</summary>
    public string Get(int player, string button) => _keys.TryGetValue(Name(player, button), out var v) ? v.Value : KeyBindings.Default(player, button);

    /// <summary>Binds a key; refuses reserved keys. Applied to the running game at once.</summary>
    public bool Set(int player, string button, KeyCode key)
    {
        var name = key.ToString();
        if (Array.IndexOf(KeyBindings.Reserved, name) >= 0 || key.ToString().StartsWith("Mouse", StringComparison.Ordinal) || key.ToString().StartsWith("Joystick", StringComparison.Ordinal)) return false;
        _keys[Name(player, button)].Value = name;
        ApplyAll();
        return true;
    }

    public void ResetAll()
    {
        foreach (var player in new[] { 1, 2 })
        {
            foreach (var button in KeyBindings.Buttons.Concat(player == 2 ? KeyBindings.P2Shortcuts : new string[0])) _keys[Name(player, button)].Value = KeyBindings.Default(player, button);
        }

        ApplyAll();
    }

    /// <summary>Bindings sharing a key with another one.</summary>
    public List<string> Conflicts() => KeyBindings.Conflicts(_keys.ToDictionary(p => p.Key, p => p.Value.Value));

    private void ApplyAll()
    {
        // Before InControl starts there are no devices yet; new ones get their keys in their constructor.
        if (InputManager.IsSetup && InputManager.Devices != null)
        {
            foreach (var keyboard in InputManager.Devices.OfType<DeviceKeyboard>()) Apply(keyboard);
        }

        KeyboardLayout.P2AnswerKeys = KeyBindings.P2Shortcuts.Select(s => Get(2, s)).ToArray();
    }

    private void Apply(DeviceKeyboard keyboard)
    {
        var player = keyboard.DeviceId == 1 ? 2 : 1;
        foreach (var pair in Fields)
        {
            if (!TryKey(Get(player, pair.Key), out var key)) continue;
            AccessTools.Field(typeof(DeviceKeyboard), pair.Value).SetValue(keyboard, key);
        }
    }

    private static bool TryKey(string name, out KeyCode key)
    {
        key = KeyCode.None;
        if (string.IsNullOrEmpty(name)) return false;
        try
        {
            key = (KeyCode)Enum.Parse(typeof(KeyCode), name, true);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void DevicePostfix(DeviceKeyboard __instance)
    {
        var self = _current;
        if (self != null) PatchGuard.Run("mods.keys.device", () => self.Apply(__instance));
    }

    private static void GlyphPostfix(ButtonStyles style, ButtonFaces face, bool isMainMenu, ref Sprite __result)
    {
        var self = _current;
        if (self == null || __result == null || (style != ButtonStyles.KeyboardOne && style != ButtonStyles.KeyboardTwo)) return;
        var original = __result;
        var replaced = __result;
        PatchGuard.Run("mods.keys.glyphs", () =>
        {
            var size = original.rect.size;
            var player = style == ButtonStyles.KeyboardTwo ? 2 : 1;
            if (isMainMenu && face is ButtonFaces.LB or ButtonFaces.RB)
            {
                var b = face == ButtonFaces.LB ? "LB" : "RB";
                if (self.Changed(1, b) || self.Changed(2, b)) replaced = KeyCaps.Row(size, Label(self.Get(1, b)), Label(self.Get(2, b)));
                return;
            }

            if (face == ButtonFaces.Axis)
            {
                if (new[] { "Up", "Left", "Down", "Right" }.Any(d => self.Changed(player, d)))
                {
                    replaced = KeyCaps.Cross(size, Label(self.Get(player, "Up")), Label(self.Get(player, "Left")), Label(self.Get(player, "Down")), Label(self.Get(player, "Right")));
                }

                return;
            }

            var button = face switch
            {
                ButtonFaces.Left => "Left",
                ButtonFaces.Right => "Right",
                ButtonFaces.Up => "Up",
                ButtonFaces.Down => "Down",
                ButtonFaces.Start => null,
                _ => face.ToString(),
            };
            if (button != null && self.Changed(player, button)) replaced = KeyCaps.Row(size, Label(self.Get(player, button)));
        });
        __result = replaced;
    }

    private bool Changed(int player, string button) => !string.Equals(Get(player, button), KeyBindings.Default(player, button), StringComparison.OrdinalIgnoreCase);

    private static string Label(string key) => KeyBindings.Label(key);
}
