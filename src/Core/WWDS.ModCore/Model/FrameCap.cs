namespace WWDS.ModCore.Model;

/// <summary>
/// Frame rate limit rules. The limit is a number of frames per second, 0 = the monitor's refresh rate,
/// -1 = unlimited. In the background a second, usually lower limit applies (0 = none); it never raises
/// the foreground limit. VSync only applies in the foreground (an unfocused window would still wait for
/// the monitor at full rate).
/// </summary>
public static class FrameCap
{
    public const int Monitor = 0;
    public const int Unlimited = -1;

    /// <summary>Application.targetFrameRate for the settings (-1 = unlimited).</summary>
    public static int Target(int limit, int backgroundLimit, bool focused, int refreshRate)
    {
        var foreground = Resolve(limit, refreshRate);
        if (focused || backgroundLimit == 0) return foreground;
        var background = Resolve(backgroundLimit, refreshRate);
        if (foreground == Unlimited) return background;
        if (background == Unlimited) return foreground;
        return background < foreground ? background : foreground;
    }

    /// <summary>QualitySettings.vSyncCount for the settings.</summary>
    public static int VSyncCount(bool vsync, bool focused) => vsync && focused ? 1 : 0;

    private static int Resolve(int limit, int refreshRate)
    {
        if (limit == Monitor) return refreshRate > 0 ? refreshRate : 60;
        return limit < 0 ? Unlimited : limit < 10 ? 10 : limit;
    }
}
