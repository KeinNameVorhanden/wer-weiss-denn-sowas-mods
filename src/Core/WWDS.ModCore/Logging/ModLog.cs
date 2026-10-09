using System;
using System.Text.RegularExpressions;

namespace WWDS.ModCore.Logging;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>Where formatted log lines end up (BepInEx log, test buffer, ...). Implemented by the host.</summary>
public interface ILogSink
{
    void Write(LogLevel level, string category, string message);
}

/// <summary>Well-known log categories, rendered as "[Category]".</summary>
public static class LogCategory
{
    public const string Core = "Core";
    public const string GameAdapter = "GameAdapter";
    public const string Game1 = "Game1";
    public const string Game2 = "Game2";
    public const string Twitch = "Twitch";
    public const string Audience = "Audience";
    public const string Vote = "Vote";
    public const string Naming = "Naming";
    public const string Guess = "Guess";
    public const string Opponent = "Opponent";
    public const string Bindings = "Bindings";
    public const string UI = "UI";
    public const string Bridge = "Bridge";
}

/// <summary>
/// Category-scoped logger. Every message passes the <see cref="Redactor"/> so OAuth tokens or
/// passwords that slip into an exception text are never written to disk.
/// </summary>
public sealed class ModLog
{
    private readonly ILogSink _sink;

    public ModLog(ILogSink sink, string category, Func<bool>? debugEnabled = null)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        Category = category;
        DebugEnabled = debugEnabled ?? (() => false);
    }

    public string Category { get; }

    public Func<bool> DebugEnabled { get; }

    public ModLog For(string category) => new(_sink, category, DebugEnabled);

    public void Debug(string message)
    {
        if (DebugEnabled()) Write(LogLevel.Debug, message);
    }

    public void Info(string message) => Write(LogLevel.Info, message);

    public void Warn(string message) => Write(LogLevel.Warning, message);

    public void Error(string message) => Write(LogLevel.Error, message);

    public void Error(string message, Exception ex) => Write(LogLevel.Error, message + ": " + ex);

    private void Write(LogLevel level, string message)
    {
        try
        {
            _sink.Write(level, Category, Redactor.Redact(message));
        }
        catch
        {
            // Logging must never take the caller down.
        }
    }
}

/// <summary>Masks secrets that look like OAuth tokens, bearer headers or password assignments.</summary>
public static class Redactor
{
    private static readonly Regex[] Patterns =
    {
        new(@"(oauth:)[A-Za-z0-9]{10,}", RegexOptions.IgnoreCase),
        new(@"(Bearer\s+)[A-Za-z0-9\-_\.]{10,}", RegexOptions.IgnoreCase),
        new(@"((?:access_token|refresh_token|client_secret|password|token)[""']?\s*[:=]\s*[""']?)[^\s""'&,}]+", RegexOptions.IgnoreCase),
    };

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        var result = text!;
        foreach (var pattern in Patterns) result = pattern.Replace(result, "$1***");
        return result;
    }
}
