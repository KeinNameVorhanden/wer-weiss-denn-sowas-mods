using System;
using System.Collections.Generic;

namespace WWDS.ModCore.Config;

/// <summary>A typed, persisted setting. The host backs it with BepInEx <c>ConfigEntry&lt;T&gt;</c>.</summary>
public interface IConfigValue<T>
{
    string Section { get; }

    string Key { get; }

    T Value { get; set; }

    T DefaultValue { get; }

    event Action<T>? Changed;
}

/// <summary>Creates settings. Supported types: bool, int, float, double, string and enums.</summary>
public interface IConfigStore
{
    IConfigValue<T> Bind<T>(string section, string key, T defaultValue, string description);

    void Save();
}

/// <summary>One bound setting as seen by generic editors (the mod menu's settings pages).</summary>
public sealed class ConfigEntryInfo
{
    private readonly Func<object?> _get;
    private readonly Action<object?> _set;

    public ConfigEntryInfo(string section, string key, string description, Type valueType, object? defaultValue, Func<object?> get, Action<object?> set)
    {
        Section = section;
        Key = key;
        Description = description;
        ValueType = valueType;
        DefaultValue = defaultValue;
        _get = get;
        _set = set;
    }

    public string Section { get; }

    public string Key { get; }

    public string Description { get; }

    public Type ValueType { get; }

    public object? DefaultValue { get; }

    public object? Value
    {
        get => _get();
        set => _set(value);
    }
}

/// <summary>Optional store capability: enumerate every bound setting.</summary>
public interface IConfigCatalog
{
    IList<ConfigEntryInfo> Entries();
}

/// <summary>In-memory store for tests, the bridge and fallback when the host has no config.</summary>
public sealed class MemoryConfigStore : IConfigStore, IConfigCatalog
{
    private readonly Dictionary<string, object> _values = new();
    private readonly List<ConfigEntryInfo> _entries = new();

    public IConfigValue<T> Bind<T>(string section, string key, T defaultValue, string description)
    {
        var id = section + "." + key;
        if (_values.TryGetValue(id, out var existing)) return (IConfigValue<T>)existing;
        var value = new MemoryValue<T>(section, key, defaultValue);
        _values[id] = value;
        _entries.Add(new ConfigEntryInfo(section, key, description, typeof(T), defaultValue, () => value.Value, v => value.Value = (T)v!));
        return value;
    }

    public IList<ConfigEntryInfo> Entries() => _entries.AsReadOnly();

    public void Save()
    {
    }

    private sealed class MemoryValue<T> : IConfigValue<T>
    {
        private T _value;

        public MemoryValue(string section, string key, T defaultValue)
        {
            Section = section;
            Key = key;
            DefaultValue = defaultValue;
            _value = defaultValue;
        }

        public string Section { get; }

        public string Key { get; }

        public T DefaultValue { get; }

        public T Value
        {
            get => _value;
            set
            {
                if (EqualityComparer<T>.Default.Equals(_value, value)) return;
                _value = value;
                Changed?.Invoke(value);
            }
        }

        public event Action<T>? Changed;
    }
}

/// <summary>Parses comma separated config lists ("a, b ,c") into trimmed, non-empty items.</summary>
public static class ConfigList
{
    public static List<string> Parse(string? text)
    {
        var result = new List<string>();
        if (text == null) return result;
        foreach (var part in text.Split(',', ';', '\n'))
        {
            var t = part.Trim();
            if (t.Length > 0) result.Add(t);
        }

        return result;
    }
}
