using System.Collections.Generic;
using WWDS.ModCore.Input;
using Xunit;

namespace WWDS.Tests.Core;

public class KeyBindingsTests
{
    [Fact]
    public void Conflicts_NameEveryBindingThatSharesAKey_AcrossBothPlayers()
    {
        var bindings = new Dictionary<string, string>
        {
            ["P1.A"] = "Alpha1",
            ["P2.A"] = "Alpha1",
            ["P1.B"] = "K",
            ["P2.B"] = "None",
            ["P2.AnswerB"] = "None",
        };
        Assert.Equal(new[] { "P1.A", "P2.A" }, KeyBindings.Conflicts(bindings));
    }

    [Fact]
    public void Labels_FitOnAKeyCap()
    {
        Assert.Equal(("1", "N3", "^", "SPC", "F11", "ENTF"), (KeyBindings.Label("Alpha1"), KeyBindings.Label("Keypad3"), KeyBindings.Label("UpArrow"),
            KeyBindings.Label("Space"), KeyBindings.Label("F11"), KeyBindings.Label("Delete")));
    }
}
