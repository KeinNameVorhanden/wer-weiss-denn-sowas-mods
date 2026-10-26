using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using InControl;
using UnityEngine;
using WWDS.ModCore.Config;
using WWDS.ModCore.Features;
using WWDS.Runtime.Patching;

namespace WWDS.Game.Wwds.Mods;

/// <summary>
/// Repairs the game's keyboard "controllers": InControl and the game each attach a keyboard with the
/// arrow layout, so two identical devices exist and one keyboard could join twice or swallow mod presses.
/// Only one keyboard per layout is kept (arrows/J-K-I-L-U-O and WASD/1-4/Q-E). Both keyboard players keep
/// Escape (the game gives it to the last one only), and while the rename dialog is open every keyboard
/// only reports Enter and Escape, so typing a name can no longer press buttons of the other keyboard.
/// Gamepads: the game matches pads by name only, so XInput pads with an unusual name ("Controller (GuliKit X)")
/// and every DInput/Switch-mode pad were ignored; they now get the Xbox layout or a generic positional one.
/// </summary>
public sealed class InputDevicesFeature : FeatureBase
{
    public const string FeatureId = "InputDevices";

    private static InputDevicesFeature? _current;
    private static bool _renameOpen;
    private IConfigValue<bool> _fixKeyboards = null!;
    private IConfigValue<bool> _xboxNames = null!;
    private IConfigValue<bool> _genericPads = null!;
    private IConfigValue<bool> _swapAB = null!;
    private bool _logged;

    public override string Id => FeatureId;

    public override string DisplayName => "Input devices";

    public override string Description => "Two keyboard players without double devices, Escape for both, safe name typing, more gamepads, device log.";

    protected override string LogCategoryName => "Mods";

    protected override void OnStart()
    {
        _fixKeyboards = Context.Config.Bind("Input", "FixKeyboards", true, "One keyboard device per layout, Escape for both keyboard players, name typing only reads Enter/Escape.");
        _xboxNames = Context.Config.Bind("Input", "AnyXInputName", true, "Treat every pad named \"Controller (...)\" (Windows' name for XInput pads, e.g. GuliKit or 8BitDo in X mode) as an Xbox controller.");
        _genericPads = Context.Config.Bind("Input", "GenericControllers", true, "Give unknown gamepads (Switch Pro, GuliKit/8BitDo in Switch or DInput mode, USB pads) a standard layout instead of ignoring them.");
        _swapAB = Context.Config.Bind("Input", "GenericSwapAB", false, "Unknown gamepads: swap the bottom and right face buttons (A/B). Takes effect after a restart.");
        _current = this;
        var ui = ModUi.For(Context);
        var none = ModCore.Capabilities.GameCapability.None;
        ui.Patch(new PatchSpec("mods.input.keyboard-dedupe", "Drop the second keyboard device with the same layout.",
            () => AccessTools.Method(typeof(InputManager), nameof(InputManager.AttachDevice), new[] { typeof(InputDevice) }), none)
        {
            Prefix = AccessTools.Method(typeof(InputDevicesFeature), nameof(AttachDevicePrefix)),
        });
        ui.Patch(new PatchSpec("mods.input.escape-both", "Every keyboard player keeps Escape (pause).",
            () => AccessTools.Method(typeof(DeviceKeyboard), nameof(DeviceKeyboard.ClearFirstPlayer)), none)
        {
            Prefix = AccessTools.Method(typeof(InputDevicesFeature), nameof(ClearFirstPlayerPrefix)),
        });
        ui.Patch(new PatchSpec("mods.input.rename-open", "Know when the rename dialog reads text.",
            () => AccessTools.Method(typeof(ViewDialogChangeName), nameof(ViewDialogChangeName.ProcessInput), new[] { typeof(string) }), none)
        {
            Postfix = AccessTools.Method(typeof(InputDevicesFeature), nameof(RenamePostfix)),
        });
        ui.Patch(new PatchSpec("mods.input.rename-keys", "While typing a name, keyboards only report Enter and Escape.",
            () => AccessTools.Method(typeof(DeviceKeyboard), nameof(DeviceKeyboard.Update), new[] { typeof(ulong), typeof(float) }), none)
        {
            Prefix = AccessTools.Method(typeof(InputDevicesFeature), nameof(KeyboardUpdatePrefix)),
        });
        ui.Patch(new PatchSpec("mods.input.pad-profiles", "Xbox profile for every XInput pad name, generic layout for unknown pads.",
            () => AccessTools.Method(typeof(UnityInputDeviceManager), "AddSystemDeviceProfiles"), none)
        {
            Postfix = AccessTools.Method(typeof(InputDevicesFeature), nameof(AddProfilesPostfix)),
        });
        InputManager.OnDeviceAttached += OnAttached;
        InputManager.OnDeviceDetached += OnDetached;
    }

    protected override void OnStop()
    {
        InputManager.OnDeviceAttached -= OnAttached;
        InputManager.OnDeviceDetached -= OnDetached;
        _current = null;
    }

    public override void Tick()
    {
        if (_logged || !InputManager.IsSetup || InputManager.Devices.Count == 0) return;
        _logged = true;
        LogDevices("Input devices at start");
    }

    private static bool Fix => _current != null && _current._fixKeyboards.Value;

    private void OnAttached(InputDevice device)
    {
        if (_logged) Log.Info("Device attached: " + Describe(device));
    }

    private void OnDetached(InputDevice device) => Log.Info("Device detached: " + Describe(device));

    private void LogDevices(string title)
    {
        Log.Info(title + ": XInput " + (InputManager.EnableXInput ? "on" : "off") + ", " + InputManager.Devices.Count + " device(s)");
        foreach (var device in InputManager.Devices) Log.Info("  " + Describe(device));
    }

    internal static string Describe(InputDevice device)
    {
        var kind = device is DeviceKeyboard k ? "keyboard " + k.DeviceId : device.GetType().Name;
        return "\"" + device.Name + "\" (" + kind + (device.IsKnown ? "" : ", unknown profile") + (string.IsNullOrEmpty(device.Meta) ? "" : ", " + device.Meta) + ")";
    }

    private static bool AttachDevicePrefix(InputDevice inputDevice)
    {
        if (!Fix || inputDevice is not DeviceKeyboard keyboard) return true;
        var duplicate = InputManager.Devices.OfType<DeviceKeyboard>().Any(k => k != keyboard && k.DeviceId == keyboard.DeviceId);
        if (duplicate) _current?.Log.Info("Skipped a second keyboard device with layout " + keyboard.DeviceId);
        return !duplicate;
    }

    private static bool ClearFirstPlayerPrefix() => !Fix;

    private static void AddProfilesPostfix(List<UnityInputDeviceProfileBase> ___systemDeviceProfiles)
    {
        var self = _current;
        if (self == null) return;
        PatchGuard.Run("mods.input.pad-profiles", () =>
        {
            if (self._xboxNames.Value)
            {
                foreach (var profile in ___systemDeviceProfiles.OfType<Xbox360WinProfile>())
                {
                    Traverse.Create(profile).Field("LastResortRegex").SetValue(XboxRegex);
                }
            }

            if (self._genericPads.Value) ___systemDeviceProfiles.Add(new GenericPadProfile(self._swapAB.Value));
        });
    }

    /// <summary>The game's Xbox 360 name pattern plus Windows' name for every XInput pad.</summary>
    internal const string XboxRegex = "360|xbox|catz|^Controller \\(";

    private static void RenamePostfix(ref IEnumerator __result)
    {
        if (Fix) __result = TrackRename(__result);
    }

    private static IEnumerator TrackRename(IEnumerator original)
    {
        _renameOpen = true;
        try
        {
            while (original.MoveNext()) yield return original.Current;
        }
        finally
        {
            _renameOpen = false;
            foreach (var keyboard in InputManager.Devices.OfType<DeviceKeyboard>()) keyboard.TextMode = false;
        }
    }

    private static bool KeyboardUpdatePrefix(DeviceKeyboard __instance, ulong updateTick, float deltaTime)
    {
        if (!_renameOpen || !Fix) return true;
        var result = true;
        PatchGuard.Run("mods.input.rename-keys", () =>
        {
            __instance.GetControl(InputControlType.Action1).UpdateWithState(Input.GetKey(KeyCode.Return) || Input.GetKey(KeyCode.KeypadEnter), updateTick, deltaTime);
            __instance.GetControl(InputControlType.Action2).UpdateWithState(Input.GetKey(KeyCode.Escape), updateTick, deltaTime);
            __instance.Commit(updateTick, deltaTime);
            result = false;
        });
        return result;
    }
}
