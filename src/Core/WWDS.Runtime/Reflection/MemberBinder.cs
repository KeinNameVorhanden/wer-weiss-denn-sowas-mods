using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace WWDS.Runtime.Reflection;

/// <summary>
/// Resolves private game members once at startup and records which are missing, so capabilities that
/// need them can be withheld instead of failing in the middle of a show.
/// </summary>
public sealed class MemberBinder
{
    private readonly List<string> _missing = new();

    public IList<string> Missing => _missing.AsReadOnly();

    public bool AllResolved => _missing.Count == 0;

    public FieldInfo? Field(Type type, string name)
    {
        var f = AccessTools.Field(type, name);
        if (f == null) _missing.Add(type.Name + "." + name);
        return f;
    }

    public MethodInfo? Method(Type type, string name, Type[]? args = null)
    {
        var m = AccessTools.Method(type, name, args);
        if (m == null) _missing.Add(type.Name + "." + name + "(" + (args == null ? "*" : string.Join(",", Array.ConvertAll(args, a => a.Name))) + ")");
        return m;
    }

    public MethodInfo? PropertySetter(Type type, string name)
    {
        var m = AccessTools.PropertySetter(type, name);
        if (m == null) _missing.Add(type.Name + "." + name + " setter");
        return m;
    }

    /// <summary>Checks a group of members and reports which of them are missing.</summary>
    public static bool AllPresent(out string missing, params KeyValuePair<string, object?>[] members)
    {
        var names = new List<string>();
        foreach (var kv in members)
        {
            if (kv.Value == null) names.Add(kv.Key);
        }

        missing = string.Join(", ", names.ToArray());
        return names.Count == 0;
    }
}
