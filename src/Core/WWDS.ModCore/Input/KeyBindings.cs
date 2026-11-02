using System;
using System.Collections.Generic;
using System.Linq;

namespace WWDS.ModCore.Input;

/// <summary>
/// The two keyboard "controllers" of the game and their keys (UnityEngine.KeyCode names). Each player
/// has ten game buttons; player 2 also has three answer shortcuts of the mod. Escape (menu) and Enter
/// (confirm, typing) stay fixed.
/// </summary>
public static class KeyBindings
{
    /// <summary>Game buttons in menu order (also the config key suffix).</summary>
    public static readonly string[] Buttons = { "Up", "Down", "Left", "Right", "A", "B", "X", "Y", "LB", "RB" };

    /// <summary>Mod shortcuts of player 2 (answers A, B, C) when two keyboard players play.</summary>
    public static readonly string[] P2Shortcuts = { "AnswerA", "AnswerB", "AnswerC" };

    private static readonly Dictionary<string, string> P1Defaults = new()
    {
        ["Up"] = "UpArrow", ["Down"] = "DownArrow", ["Left"] = "LeftArrow", ["Right"] = "RightArrow",
        ["A"] = "J", ["B"] = "K", ["X"] = "I", ["Y"] = "L", ["LB"] = "U", ["RB"] = "O",
    };

    private static readonly Dictionary<string, string> P2Defaults = new()
    {
        ["Up"] = "W", ["Down"] = "S", ["Left"] = "A", ["Right"] = "D",
        ["A"] = "Alpha1", ["B"] = "Alpha2", ["X"] = "Alpha3", ["Y"] = "Alpha4", ["LB"] = "Q", ["RB"] = "E",
        ["AnswerA"] = "X", ["AnswerB"] = "C", ["AnswerC"] = "V",
    };

    /// <summary>Keys no binding may use: the mod and the game need them as they are.</summary>
    public static readonly string[] Reserved = { "Escape", "Return", "KeypadEnter", "F8", "F9", "F10", "F12" };

    /// <summary>The game's key for a button of player 1 or 2.</summary>
    public static string Default(int player, string button) =>
        (player == 1 ? P1Defaults : P2Defaults).TryGetValue(button, out var key) ? key : "None";

    /// <summary>Bindings that share a key with another one (config names like "P1.A"), for warnings.</summary>
    public static List<string> Conflicts(IDictionary<string, string> bindings)
    {
        return bindings.Where(b => b.Value != "None")
            .GroupBy(b => b.Value, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .SelectMany(g => g.Select(b => b.Key))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Short text for a key cap: one to three characters ("J", "1", "N3", "SPC", "^" for up).</summary>
    public static string Label(string key)
    {
        if (string.IsNullOrEmpty(key) || key == "None") return "-";
        if (key.Length == 1) return key.ToUpperInvariant();
        if (key.StartsWith("Alpha", StringComparison.Ordinal) && key.Length == 6) return key.Substring(5);
        if (key.StartsWith("Keypad", StringComparison.Ordinal))
        {
            var rest = key.Substring(6);
            return rest.Length == 1 ? "N" + rest : rest switch
            {
                "Plus" => "N+",
                "Minus" => "N-",
                "Multiply" => "N*",
                "Divide" => "N/",
                "Period" => "N.",
                _ => "NUM",
            };
        }

        switch (key)
        {
            case "UpArrow": return "^";
            case "DownArrow": return "v";
            case "LeftArrow": return "<";
            case "RightArrow": return ">";
            case "Space": return "SPC";
            case "Tab": return "TAB";
            case "Backspace": return "BSP";
            case "LeftShift":
            case "RightShift": return "SHF";
            case "LeftControl":
            case "RightControl": return "STR";
            case "LeftAlt":
            case "RightAlt":
            case "AltGr": return "ALT";
            case "Delete": return "ENTF";
            case "Insert": return "EINF";
            case "Home": return "POS1";
            case "End": return "ENDE";
            case "PageUp": return "BILD";
            case "PageDown": return "BILD";
            case "Comma": return ",";
            case "Period": return ".";
            case "Minus": return "-";
            case "Plus": return "+";
        }

        if (key.Length <= 3 && key[0] == 'F') return key;
        return key.Substring(0, Math.Min(3, key.Length)).ToUpperInvariant();
    }
}
