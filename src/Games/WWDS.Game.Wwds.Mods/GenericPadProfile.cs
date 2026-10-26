using InControl;

namespace WWDS.Game.Wwds.Mods;

/// <summary>
/// Catch-all controller profile, checked after every built-in one: any gamepad the game does not know
/// (Switch Pro, GuliKit or 8BitDo in Switch/DInput mode, cheap USB pads) gets the usual positional layout,
/// bottom face button = A (confirm), right = B, left = X, top = Y, shoulders, triggers, Select/Start,
/// left stick and the hat as D-pad. Without it such a pad has no A/B/shoulder buttons and can never join.
/// </summary>
public sealed class GenericPadProfile : UnityInputDeviceProfile
{
    public GenericPadProfile(bool swapAB)
    {
        Name = "Generic Gamepad (WWDS mod)";
        Meta = "Unknown gamepad, positional layout";
        DeviceClass = InputDeviceClass.Controller;
        DeviceStyle = InputDeviceStyle.Unknown;
        IncludePlatforms = new[] { "Windows" };
        LastResortRegex = ".+";
        ButtonMappings = new[]
        {
            Map("A", InputControlType.Action1, swapAB ? Button1 : Button0),
            Map("B", InputControlType.Action2, swapAB ? Button0 : Button1),
            Map("X", InputControlType.Action3, Button2),
            Map("Y", InputControlType.Action4, Button3),
            Map("Left Bumper", InputControlType.LeftBumper, Button4),
            Map("Right Bumper", InputControlType.RightBumper, Button5),
            Map("Left Trigger", InputControlType.LeftTrigger, Button6),
            Map("Right Trigger", InputControlType.RightTrigger, Button7),
            Map("Select", InputControlType.Back, Button8),
            Map("Start", InputControlType.Start, Button9),
            Map("Left Stick Button", InputControlType.LeftStickButton, Button10),
            Map("Right Stick Button", InputControlType.RightStickButton, Button11),
        };
        AnalogMappings = new[]
        {
            LeftStickLeftMapping(Analog0),
            LeftStickRightMapping(Analog0),
            LeftStickUpMapping(Analog1),
            LeftStickDownMapping(Analog1),
            DPadLeftMapping(Analog5),
            DPadRightMapping(Analog5),
            DPadUpMapping2(Analog6),
            DPadDownMapping2(Analog6),
        };
    }

    private static InputControlMapping Map(string handle, InputControlType target, InputControlSource source) =>
        new() { Handle = handle, Target = target, Source = source };
}
