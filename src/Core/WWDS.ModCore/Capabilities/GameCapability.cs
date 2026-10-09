using System;
using System.Collections.Generic;
using System.Text;

namespace WWDS.ModCore.Capabilities;

/// <summary>
/// What a game adapter can reliably do. Granted only after the patches backing a capability were
/// applied successfully (and, where marked in docs/compatibility.md, verified at runtime).
/// Features declare the capabilities they need and are not started without them.
/// </summary>
[Flags]
public enum GameCapability : long
{
    None = 0,
    ReadPhase = 1 << 0,
    ReadQuestion = 1 << 1,
    ReadAnswers = 1 << 2,
    ReadCorrectAnswer = 1 << 3,
    ReadScore = 1 << 4,
    ReadContestants = 1 << 5,
    ObserveAnswers = 1 << 6,
    RenameContestant = 1 << 7,

    /// <summary>A bot contestant's normal-question answer can be decided from outside.</summary>
    ControlBotAnswer = 1 << 8,

    /// <summary>A bot contestant's master-question answer can be decided from outside (20 s clock).</summary>
    ControlBotMasterAnswer = 1 << 9,

    /// <summary>A bot contestant's master bet can be decided from outside.</summary>
    ControlBotBet = 1 << 10,

    /// <summary>A bot contestant's bonus estimate can be decided from outside.</summary>
    ControlBotEstimate = 1 << 11,

    /// <summary>A bot contestant's category pick can be decided from outside.</summary>
    ControlBotCategory = 1 << 12,

    /// <summary>An answer can be submitted on behalf of a human contestant (audience assist).</summary>
    SubmitAnswerForHuman = 1 << 13,

    /// <summary>A bot-vs-bot or bot-vs-human show can be started programmatically.</summary>
    StartShow = 1 << 14,

    /// <summary>Two bots in one team (multiple community contestants). Not granted until proven.</summary>
    SecondBotInTeam = 1 << 15,

    /// <summary>Questions of a show slot can be replaced at load time.</summary>
    ReplaceQuestions = 1 << 16,

    /// <summary>The team label shown in the player frames can be changed.</summary>
    RenameTeam = 1 << 17,

    /// <summary>The category can be picked on behalf of a human contestant (audience picks the category).</summary>
    SelectCategoryForHuman = 1 << 18,
}

/// <summary>Granted capabilities plus the reason for every capability that was withheld.</summary>
public sealed class CapabilitySet
{
    private readonly Dictionary<GameCapability, string> _denied = new();

    public GameCapability Granted { get; private set; }

    public bool Has(GameCapability required) => required == GameCapability.None || (Granted & required) == required;

    public GameCapability Missing(GameCapability required) => required & ~Granted;

    public void Grant(GameCapability capability)
    {
        Granted |= capability;
        foreach (var single in Split(capability)) _denied.Remove(single);
    }

    public void Deny(GameCapability capability, string reason)
    {
        Granted &= ~capability;
        foreach (var single in Split(capability)) _denied[single] = reason;
    }

    public string? ReasonDenied(GameCapability capability) => _denied.TryGetValue(capability, out var r) ? r : null;

    public static IEnumerable<GameCapability> Split(GameCapability caps)
    {
        foreach (GameCapability value in Enum.GetValues(typeof(GameCapability)))
        {
            if (value != GameCapability.None && (caps & value) == value && IsSingleBit((long)value)) yield return value;
        }
    }

    public string Describe()
    {
        var sb = new StringBuilder();
        foreach (GameCapability value in Enum.GetValues(typeof(GameCapability)))
        {
            if (value == GameCapability.None) continue;
            sb.Append(Has(value) ? "[x] " : "[ ] ").Append(value);
            var reason = ReasonDenied(value);
            if (!Has(value) && reason != null) sb.Append("  (").Append(reason).Append(')');
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static bool IsSingleBit(long v) => v != 0 && (v & (v - 1)) == 0;
}
