using System;
using System.Globalization;

namespace WWDS.ModCore.Config;

/// <summary>
/// Text and step conversions for generic setting editors (mod menu): parses what a user typed into the
/// setting's type and picks a sensible step for left/right on numbers.
/// </summary>
public static class SettingText
{
    public static string Format(object? value) => value switch
    {
        null => string.Empty,
        float f => f.ToString("0.###", CultureInfo.InvariantCulture),
        double d => d.ToString("0.###", CultureInfo.InvariantCulture),
        IFormattable x => x.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>Parses <paramref name="text"/> as <paramref name="type"/> (invariant culture, "0,5" accepted, enums by name).</summary>
    public static bool TryParse(Type type, string text, out object? value)
    {
        value = null;
        var t = (text ?? string.Empty).Trim();
        if (type == typeof(string))
        {
            value = t;
            return true;
        }

        if (type == typeof(bool))
        {
            switch (t.ToLowerInvariant())
            {
                case "true" or "an" or "on" or "ja" or "yes" or "1":
                    value = true;
                    return true;
                case "false" or "aus" or "off" or "nein" or "no" or "0":
                    value = false;
                    return true;
                default:
                    return false;
            }
        }

        if (type.IsEnum)
        {
            foreach (var name in Enum.GetNames(type))
            {
                if (!string.Equals(name, t, StringComparison.OrdinalIgnoreCase)) continue;
                value = Enum.Parse(type, name);
                return true;
            }

            return false;
        }

        var number = t.Replace(',', '.');
        if (type == typeof(int))
        {
            if (!int.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return false;
            value = i;
            return true;
        }

        if (type == typeof(float) || type == typeof(double))
        {
            if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || double.IsNaN(d) || double.IsInfinity(d)) return false;
            value = type == typeof(float) ? (object)(float)d : d;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Next value when stepping a number: small numbers move in fine steps, large ones by about a tenth of
    /// their magnitude (25 -&gt; 26, 0.6 -&gt; 0.65, 1500 -&gt; 1600). Enums and bools cycle.
    /// </summary>
    public static object? Step(Type type, object? current, int direction)
    {
        if (type == typeof(bool)) return !(current is true);
        if (type.IsEnum)
        {
            var values = Enum.GetValues(type);
            var index = Array.IndexOf(values, current);
            return values.GetValue(((index + direction) % values.Length + values.Length) % values.Length);
        }

        if (type == typeof(int))
        {
            var v = current is int i ? i : 0;
            return v + (direction * IntStep(Math.Abs(v + (direction < 0 ? -1 : 0))));
        }

        if (type == typeof(float) || type == typeof(double))
        {
            var v = Convert.ToDouble(current ?? 0, CultureInfo.InvariantCulture);
            var step = FloatStep(Math.Abs(v + (direction < 0 ? -1e-9 : 0)));
            var next = Math.Round((Math.Round(v / step) + direction) * step, 6);
            return type == typeof(float) ? (object)(float)next : next;
        }

        return current;
    }

    private static int IntStep(int magnitude)
    {
        if (magnitude < 20) return 1;
        var step = 1;
        while (magnitude >= step * 100) step *= 10;
        return step;
    }

    private static double FloatStep(double magnitude)
    {
        if (magnitude < 1) return 0.05;
        if (magnitude < 10) return 0.5;
        return Math.Pow(10, Math.Floor(Math.Log10(magnitude)) - 1);
    }
}
