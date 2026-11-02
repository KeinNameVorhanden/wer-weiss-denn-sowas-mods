using WWDS.ModCore.Model;
using Xunit;

namespace WWDS.Tests.Core;

public class FrameCapTests
{
    [Fact]
    public void MonitorLimit_FollowsTheRefreshRate_AndFallsBackTo60WhenUnknown()
    {
        Assert.Equal(144, FrameCap.Target(FrameCap.Monitor, 0, focused: true, refreshRate: 144));
        Assert.Equal(60, FrameCap.Target(FrameCap.Monitor, 0, focused: true, refreshRate: 0));
    }

    [Fact]
    public void BackgroundLimit_OnlyLowers_AndAlsoCapsAnUnlimitedGame()
    {
        Assert.Equal(30, FrameCap.Target(144, 30, focused: false, refreshRate: 144));
        Assert.Equal(20, FrameCap.Target(20, 30, focused: false, refreshRate: 144));
        Assert.Equal(30, FrameCap.Target(FrameCap.Unlimited, 30, focused: false, refreshRate: 144));
        Assert.Equal(FrameCap.Unlimited, FrameCap.Target(FrameCap.Unlimited, 30, focused: true, refreshRate: 144));
        Assert.Equal(144, FrameCap.Target(144, 0, focused: false, refreshRate: 144));
    }

    [Fact]
    public void VSync_IsDroppedInTheBackground_SoTheBackgroundLimitWorks()
    {
        Assert.Equal(1, FrameCap.VSyncCount(vsync: true, focused: true));
        Assert.Equal(0, FrameCap.VSyncCount(vsync: true, focused: false));
    }
}
