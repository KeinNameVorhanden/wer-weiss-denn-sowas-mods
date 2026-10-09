using System;
using System.Collections.Generic;
using System.Text;
using WWDS.ModCore.Time;

namespace WWDS.ModCore.Commands;

/// <summary>Trust level of whoever issued a command. Higher includes lower.</summary>
public enum Authority
{
    Viewer = 0,
    Subscriber = 10,
    Vip = 20,
    Moderator = 30,
    Broadcaster = 40,

    /// <summary>The person at the keyboard (in-game panel, local console).</summary>
    LocalOperator = 50,
}

/// <summary>Who issued a command or message. <see cref="UserId"/> is the stable Twitch user id.</summary>
public sealed class Invoker
{
    public Invoker(string userId, string displayName, Authority authority, string source, string? login = null)
    {
        UserId = userId ?? string.Empty;
        Login = (login ?? displayName ?? string.Empty).ToLowerInvariant();
        DisplayName = displayName ?? string.Empty;
        Authority = authority;
        Source = source ?? string.Empty;
    }

    public string UserId { get; }

    /// <summary>Lower-case account name (falls back to the display name).</summary>
    public string Login { get; }

    public string DisplayName { get; }

    public Authority Authority { get; }

    /// <summary>"twitch", "irc", "mock", "local".</summary>
    public string Source { get; }

    public static Invoker Local(string name = "local") => new("local", name, Authority.LocalOperator, "local");

    public override string ToString() => DisplayName + " (" + Authority + ", " + Source + ")";
}

public sealed class ParsedCommand
{
    public ParsedCommand(string name, IList<string> args, string rawArgs)
    {
        Name = name;
        Args = args;
        RawArgs = rawArgs;
    }

    /// <summary>Lower-case command name without prefix.</summary>
    public string Name { get; }

    public IList<string> Args { get; }

    /// <summary>Everything after the command name, trimmed (for free text like names).</summary>
    public string RawArgs { get; }

    public string Arg(int index) => index < Args.Count ? Args[index] : string.Empty;
}

public static class CommandParser
{
    /// <summary>
    /// Parses "!vote A", "!name \"Quiz Master\"", "  !Guess 25000 ". Returns null if the text does
    /// not start with <paramref name="prefix"/> or has no command name.
    /// </summary>
    public static ParsedCommand? Parse(string? text, string prefix)
    {
        if (text == null || string.IsNullOrEmpty(prefix)) return null;
        var t = text.Trim();
        if (!t.StartsWith(prefix, StringComparison.Ordinal) || t.Length == prefix.Length) return null;
        t = t.Substring(prefix.Length);

        var nameEnd = 0;
        while (nameEnd < t.Length && !char.IsWhiteSpace(t[nameEnd])) nameEnd++;
        var name = t.Substring(0, nameEnd).ToLowerInvariant();
        if (name.Length == 0) return null;
        var raw = t.Substring(nameEnd).Trim();
        return new ParsedCommand(name, Tokenize(raw), raw);
    }

    public static List<string> Tokenize(string text)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;
        foreach (var c in text)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (hasToken) result.Add(current.ToString());
                current.Length = 0;
                hasToken = false;
                continue;
            }

            current.Append(c);
            hasToken = true;
        }

        if (hasToken) result.Add(current.ToString());
        return result;
    }
}

public enum CommandOutcome
{
    NotACommand,
    Unknown,
    Denied,
    OnCooldown,
    Handled,
    Failed,
}

public sealed class CommandResult
{
    public CommandResult(CommandOutcome outcome, string? reply = null)
    {
        Outcome = outcome;
        Reply = reply;
    }

    public CommandOutcome Outcome { get; }

    /// <summary>Optional text for chat / console.</summary>
    public string? Reply { get; }
}

public sealed class CommandDefinition
{
    public CommandDefinition(string name, Authority minAuthority, string description, Func<ParsedCommand, Invoker, CommandResult> handler)
    {
        Name = name.ToLowerInvariant();
        MinAuthority = minAuthority;
        Description = description;
        Handler = handler;
    }

    public string Name { get; }

    public IList<string> Aliases { get; } = new List<string>();

    public Authority MinAuthority { get; }

    public string Description { get; }

    /// <summary>Seconds between two uses by the same user (0 = none). Moderators and up are exempt.</summary>
    public double UserCooldown { get; set; }

    /// <summary>Seconds between two uses by anyone (0 = none). Moderators and up are exempt.</summary>
    public double GlobalCooldown { get; set; }

    public Func<ParsedCommand, Invoker, CommandResult> Handler { get; }
}

/// <summary>
/// Shared command table for chat (via the audience layer) and the local console. Enforces authority
/// and cooldowns, isolates handler exceptions.
/// </summary>
public sealed class CommandRegistry
{
    private readonly Dictionary<string, CommandDefinition> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _lastUseByUser = new();
    private readonly Dictionary<string, double> _lastUseGlobal = new();
    private readonly IClock _clock;
    private readonly Action<Exception>? _onError;

    public CommandRegistry(IClock clock, Action<Exception>? onError = null)
    {
        _clock = clock;
        _onError = onError;
    }

    public IEnumerable<CommandDefinition> All => _byName.Values;

    /// <summary>Registers a command; returns a handle that unregisters it (features call this on stop).</summary>
    public IDisposable Register(CommandDefinition definition)
    {
        foreach (var key in Keys(definition))
        {
            if (_byName.ContainsKey(key)) throw new InvalidOperationException("Command '" + key + "' already registered");
        }

        foreach (var key in Keys(definition)) _byName[key] = definition;
        return new Registration(this, definition);
    }

    public CommandResult Execute(string? text, string prefix, Invoker invoker)
    {
        var parsed = CommandParser.Parse(text, prefix);
        return parsed == null ? new CommandResult(CommandOutcome.NotACommand) : Execute(parsed, invoker);
    }

    public CommandResult Execute(ParsedCommand command, Invoker invoker)
    {
        if (!_byName.TryGetValue(command.Name, out var def)) return new CommandResult(CommandOutcome.Unknown);
        if (invoker.Authority < def.MinAuthority) return new CommandResult(CommandOutcome.Denied);

        var now = _clock.Now;
        var exempt = invoker.Authority >= Authority.Moderator;
        var userKey = def.Name + "|" + invoker.UserId;
        if (!exempt)
        {
            if (def.UserCooldown > 0 && _lastUseByUser.TryGetValue(userKey, out var lastUser) && now - lastUser < def.UserCooldown) return new CommandResult(CommandOutcome.OnCooldown);
            if (def.GlobalCooldown > 0 && _lastUseGlobal.TryGetValue(def.Name, out var lastAny) && now - lastAny < def.GlobalCooldown) return new CommandResult(CommandOutcome.OnCooldown);
        }

        CommandResult result;
        try
        {
            result = def.Handler(command, invoker);
        }
        catch (Exception ex)
        {
            _onError?.Invoke(ex);
            return new CommandResult(CommandOutcome.Failed);
        }

        if (result.Outcome == CommandOutcome.Handled)
        {
            _lastUseByUser[userKey] = now;
            _lastUseGlobal[def.Name] = now;
        }

        return result;
    }

    private static IEnumerable<string> Keys(CommandDefinition d)
    {
        yield return d.Name;
        foreach (var a in d.Aliases) yield return a.ToLowerInvariant();
    }

    private sealed class Registration : IDisposable
    {
        private readonly CommandRegistry _owner;
        private readonly CommandDefinition _def;

        public Registration(CommandRegistry owner, CommandDefinition def)
        {
            _owner = owner;
            _def = def;
        }

        public void Dispose()
        {
            foreach (var key in Keys(_def))
            {
                if (_owner._byName.TryGetValue(key, out var d) && ReferenceEquals(d, _def)) _owner._byName.Remove(key);
            }
        }
    }
}
