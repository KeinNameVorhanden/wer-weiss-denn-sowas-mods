using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using InControl;
using WWDS.Runtime.Patching;

namespace WWDS.Game.Wwds.Mods;

/// <summary>
/// One-tick virtual button presses on real InControl devices:
/// injected after every device read its hardware and before InControl commits, so the game's own loops see
/// an ordinary press and release. Used by mouse clicks and keyboard shortcuts.
/// </summary>
public sealed class VirtualPad
{
    private static VirtualPad? _current;
    private static FieldInfo? _tickField;
    private readonly List<Pending> _pending = new();

    public VirtualPad(ModUi ui)
    {
        _current = this;
        _tickField = AccessTools.Field(typeof(InputManager), "currentTick");
        ui.Patch(new PatchSpec("mods.virtual-pad", "Virtual button presses (mouse clicks, keyboard shortcuts) after the devices read their hardware.",
            () => AccessTools.Method(typeof(InputManager), "UpdateDevices", new[] { typeof(float) }), ModCore.Capabilities.GameCapability.None)
        {
            Postfix = AccessTools.Method(typeof(VirtualPad), nameof(UpdateDevicesPostfix)),
        });
    }

    /// <summary>The keyboard "controller" (DeviceKeyboard id 0), which the mouse belongs to.</summary>
    public static InputDevice? Keyboard => InputManager.Devices.OfType<DeviceKeyboard>().OrderBy(k => k.DeviceId).FirstOrDefault();

    /// <summary>Holds <paramref name="control"/> for two input ticks: one press and one release, like a tap.</summary>
    public void Press(InputDevice device, InputControlType control) => _pending.Add(new Pending(device, control, 2));

    public void Clear() => _pending.Clear();

    private static void UpdateDevicesPostfix(float deltaTime)
    {
        var self = _current;
        if (self == null || self._pending.Count == 0 || _tickField == null) return;
        PatchGuard.Run("mods.virtual-pad", () =>
        {
            var tick = (ulong)_tickField.GetValue(null);
            for (var i = self._pending.Count - 1; i >= 0; i--)
            {
                var p = self._pending[i];
                p.Device.GetControl(p.Control).UpdateWithState(true, tick, deltaTime);
                if (--p.Ticks <= 0) self._pending.RemoveAt(i);
            }
        });
    }

    private sealed class Pending
    {
        public Pending(InputDevice device, InputControlType control, int ticks)
        {
            Device = device;
            Control = control;
            Ticks = ticks;
        }

        public InputDevice Device { get; }

        public InputControlType Control { get; }

        public int Ticks { get; set; }
    }
}
