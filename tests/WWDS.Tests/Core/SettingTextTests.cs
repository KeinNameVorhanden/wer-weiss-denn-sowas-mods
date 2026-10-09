using WWDS.ModCore.Config;
using WWDS.Audience.Opponents;
using Xunit;

namespace WWDS.Tests.Core;

public class SettingTextTests
{
    [Theory]
    [InlineData(typeof(float), "0,5", 0.5f)]
    [InlineData(typeof(float), " 12.25 ", 12.25f)]
    [InlineData(typeof(int), "-3", -3)]
    [InlineData(typeof(bool), "An", true)]
    [InlineData(typeof(bool), "nein", false)]
    public void TryParse_AcceptsWhatPlayersType(Type type, string text, object expected)
    {
        Assert.True(SettingText.TryParse(type, text, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData(typeof(int), "1.5")]
    [InlineData(typeof(int), "")]
    [InlineData(typeof(float), "NaN")]
    [InlineData(typeof(bool), "vielleicht")]
    [InlineData(typeof(OpponentMode), "Nobody")]
    public void TryParse_RejectsGarbage(Type type, string text)
    {
        Assert.False(SettingText.TryParse(type, text, out _));
    }

    [Fact]
    public void TryParse_EnumIgnoresCase()
    {
        Assert.True(SettingText.TryParse(typeof(OpponentMode), "selectedviewer", out var value));
        Assert.Equal(OpponentMode.SelectedViewer, value);
    }

    [Theory]
    [InlineData(25, 1, 26)]
    [InlineData(19, 1, 20)]
    [InlineData(100, -1, 99)]
    [InlineData(1000, -1, 990)]
    [InlineData(1500, 1, 1600)]
    [InlineData(0, -1, -1)]
    public void Step_Int_ScalesWithMagnitude_AndStepsDownAcrossDecades(int value, int direction, int expected)
    {
        Assert.Equal(expected, SettingText.Step(typeof(int), value, direction));
    }

    [Theory]
    [InlineData(0.6f, 1, 0.65f)]
    [InlineData(10f, -1, 9.5f)]
    [InlineData(25f, 1, 26f)]
    [InlineData(0.62f, 1, 0.65f)]
    public void Step_Float_SnapsToTheStepGrid(float value, int direction, float expected)
    {
        Assert.Equal(expected, (float)SettingText.Step(typeof(float), value, direction)!, 4);
    }

    [Fact]
    public void Step_EnumWrapsAround()
    {
        Assert.Equal(OpponentMode.Crowd, SettingText.Step(typeof(OpponentMode), OpponentMode.Hybrid, 1));
        Assert.Equal(OpponentMode.Hybrid, SettingText.Step(typeof(OpponentMode), OpponentMode.Crowd, -1));
    }
}
