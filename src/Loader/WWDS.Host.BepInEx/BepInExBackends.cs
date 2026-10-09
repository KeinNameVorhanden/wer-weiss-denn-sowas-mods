using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;
using WWDS.ModCore.Config;
using WWDS.ModCore.Logging;
using LogLevel = WWDS.ModCore.Logging.LogLevel;

namespace WWDS.Host.BepInEx;

/// <summary>Routes harness logs into BepInEx's log (console + LogOutput.log) as "[Category] message".</summary>
internal sealed class BepInExLogSink : ILogSink
{
    private readonly ManualLogSource _source;

    public BepInExLogSink(ManualLogSource source) => _source = source;

    public void Write(LogLevel level, string category, string message)
    {
        var line = "[" + category + "] " + message;
        switch (level)
        {
            case LogLevel.Debug:
                _source.LogDebug(line);
                break;
            case LogLevel.Warning:
                _source.LogWarning(line);
                break;
            case LogLevel.Error:
                _source.LogError(line);
                break;
            default:
                _source.LogInfo(line);
                break;
        }
    }
}

/// <summary>Backs harness settings with BepInEx's typed .cfg file (BepInEx/config/wwds.harness.cfg).</summary>
internal sealed class BepInExConfigStore : IConfigStore, IConfigCatalog
{
    private readonly ConfigFile _file;

    public BepInExConfigStore(ConfigFile file) => _file = file;

    public IConfigValue<T> Bind<T>(string section, string key, T defaultValue, string description) =>
        new Entry<T>(_file.Bind(section, key, defaultValue, description));

    public void Save() => _file.Save();

    public IList<ConfigEntryInfo> Entries()
    {
        var list = new List<ConfigEntryInfo>();
        foreach (var pair in _file)
        {
            var entry = pair.Value;
            list.Add(new ConfigEntryInfo(entry.Definition.Section, entry.Definition.Key, entry.Description?.Description ?? string.Empty,
                entry.SettingType, entry.DefaultValue, () => entry.BoxedValue, v => entry.BoxedValue = v));
        }

        return list;
    }

    private sealed class Entry<T> : IConfigValue<T>
    {
        private readonly ConfigEntry<T> _entry;

        public Entry(ConfigEntry<T> entry)
        {
            _entry = entry;
            _entry.SettingChanged += (_, _) => Changed?.Invoke(_entry.Value);
        }

        public string Section => _entry.Definition.Section;

        public string Key => _entry.Definition.Key;

        public T Value
        {
            get => _entry.Value;
            set => _entry.Value = value;
        }

        public T DefaultValue => (T)_entry.DefaultValue;

        public event Action<T>? Changed;
    }
}
