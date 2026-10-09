using WWDS.ModCore.Commands;
using WWDS.ModCore.Time;
using Xunit;

namespace WWDS.Tests.Core;

public class CommandTests
{
    [Theory]
    [InlineData("!vote A", "vote", new[] { "A" })]
    [InlineData("  !Name \"Quiz Master\"  extra ", "name", new[] { "Quiz Master", "extra" })]
    [InlineData("!guess", "guess", new string[0])]
    [InlineData("!name \"\"", "name", new[] { "" })]
    public void Parse_HandlesCaseQuotesAndWhitespace(string text, string name, string[] args)
    {
        var cmd = CommandParser.Parse(text, "!");
        Assert.NotNull(cmd);
        Assert.Equal(name, cmd!.Name);
        Assert.Equal(args, cmd.Args);
    }

    [Theory]
    [InlineData("vote A")]
    [InlineData("!")]
    [InlineData("! vote")]
    [InlineData(null)]
    public void Parse_RejectsNonCommands(string? text) => Assert.Null(CommandParser.Parse(text, "!"));

    [Fact]
    public void Execute_EnforcesAuthority_AndPerUserCooldown_ModeratorsExempt()
    {
        var clock = new ManualClock();
        var reg = new CommandRegistry(clock);
        var handled = 0;
        reg.Register(new CommandDefinition("vote", Authority.Viewer, "vote", (_, _) =>
        {
            handled++;
            return new CommandResult(CommandOutcome.Handled);
        }) { UserCooldown = 5 });
        reg.Register(new CommandDefinition("cancel", Authority.Moderator, "cancel", (_, _) => new CommandResult(CommandOutcome.Handled)));

        var viewer = new Invoker("1", "viewer", Authority.Viewer, "twitch");
        var other = new Invoker("2", "other", Authority.Viewer, "twitch");
        var mod = new Invoker("3", "mod", Authority.Moderator, "twitch");

        Assert.Equal(CommandOutcome.Denied, reg.Execute("!cancel", "!", viewer).Outcome);
        Assert.Equal(CommandOutcome.Handled, reg.Execute("!cancel", "!", mod).Outcome);

        Assert.Equal(CommandOutcome.Handled, reg.Execute("!vote A", "!", viewer).Outcome);
        Assert.Equal(CommandOutcome.OnCooldown, reg.Execute("!vote B", "!", viewer).Outcome);
        Assert.Equal(CommandOutcome.Handled, reg.Execute("!vote B", "!", other).Outcome);
        Assert.Equal(CommandOutcome.Handled, reg.Execute("!vote A", "!", mod).Outcome);
        Assert.Equal(CommandOutcome.Handled, reg.Execute("!vote A", "!", mod).Outcome);
        clock.Advance(5.01);
        Assert.Equal(CommandOutcome.Handled, reg.Execute("!vote C", "!", viewer).Outcome);
        Assert.Equal(5, handled);
        Assert.Equal(CommandOutcome.Unknown, reg.Execute("!nope", "!", viewer).Outcome);
    }

    [Fact]
    public void FailedHandler_DoesNotStartCooldown_AndUnregisterRemovesAliases()
    {
        var clock = new ManualClock();
        var errors = new List<Exception>();
        var reg = new CommandRegistry(clock, errors.Add);
        var fail = true;
        var def = new CommandDefinition("name", Authority.Viewer, "submit name", (_, _) =>
        {
            if (fail) throw new InvalidOperationException();
            return new CommandResult(CommandOutcome.Handled);
        }) { UserCooldown = 30 };
        def.Aliases.Add("n");
        var handle = reg.Register(def);
        var viewer = new Invoker("1", "v", Authority.Viewer, "twitch");

        Assert.Equal(CommandOutcome.Failed, reg.Execute("!n Bob", "!", viewer).Outcome);
        fail = false;
        Assert.Equal(CommandOutcome.Handled, reg.Execute("!n Bob", "!", viewer).Outcome);
        Assert.Single(errors);

        handle.Dispose();
        Assert.Equal(CommandOutcome.Unknown, reg.Execute("!n Bob", "!", viewer).Outcome);
        Assert.Equal(CommandOutcome.Unknown, reg.Execute("!name Bob", "!", viewer).Outcome);
    }
}
