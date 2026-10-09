using System.Text.RegularExpressions;

namespace WWDS.Bridge;

/// <summary>Masks anything token-like before it reaches the console (mirrors the in-game redactor).</summary>
public static partial class LogText
{
    public static string Redact(string text) =>
        Secret().Replace(Bearer().Replace(text, "$1***"), "$1***");

    [GeneratedRegex(@"(Bearer\s+|OAuth\s+)[A-Za-z0-9\-_\.]{10,}", RegexOptions.IgnoreCase)]
    private static partial Regex Bearer();

    [GeneratedRegex(@"((?:access_token|refresh_token|client_secret|device_code)[""']?\s*[:=]\s*[""']?)[^\s""'&,}]+", RegexOptions.IgnoreCase)]
    private static partial Regex Secret();
}
