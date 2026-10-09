using System;
using System.Collections.Generic;
using WWDS.ModCore.Adapters;
using WWDS.ModCore.Capabilities;
using WWDS.ModCore.Commands;
using WWDS.ModCore.Config;
using WWDS.ModCore.Detection;
using WWDS.ModCore.Events;
using WWDS.ModCore.Features;
using WWDS.ModCore.Logging;
using WWDS.ModCore.Threading;
using WWDS.ModCore.Time;

namespace WWDS.ModCore;

/// <summary>
/// Everything a feature needs, handed over in <see cref="IFeature.Start"/>. Built once by the host.
/// </summary>
public sealed class ModContext
{
    public const string HarnessVersion = WWDS.WwdsVersion.Value;

    private readonly Dictionary<Type, object> _services = new();

    public ModContext(ModLog log, IConfigStore config, IClock clock, string loaderName, DetectionResult detection)
    {
        Log = log;
        Config = config;
        Clock = clock;
        LoaderName = loaderName;
        Detection = detection;
        MainThread = new MainThreadQueue(ex => log.Error("Main-thread work failed", ex));
        Scheduler = new Scheduler(clock, ex => log.Error("Scheduled action failed", ex));
        Bus = new EventBus((evt, ex) => log.For(LogCategory.Core).Error("Handler for " + evt.GetType().Name + " failed", ex));
        Journal = new EventJournal(200, clock);
        Bus.SubscribeAll(Journal.Record);
        Commands = new CommandRegistry(clock, ex => log.Error("Command failed", ex));
        Features = new FeatureRegistry(config, log.For(LogCategory.Core));
        DebugMode = config.Bind("General", "DebugMode", false, "Verbose logging and extra diagnostics.");
    }

    public ModLog Log { get; }

    public IConfigStore Config { get; }

    public IClock Clock { get; }

    /// <summary>"BepInEx 5.4.23.5" etc.</summary>
    public string LoaderName { get; }

    public DetectionResult Detection { get; }

    public MainThreadQueue MainThread { get; }

    public Scheduler Scheduler { get; }

    /// <summary>Set by the host; null in tests.</summary>
    public IHostControl? Host { get; set; }

    public EventBus Bus { get; }

    public EventJournal Journal { get; }

    public CommandRegistry Commands { get; }

    public FeatureRegistry Features { get; }

    public IConfigValue<bool> DebugMode { get; }

    /// <summary>Null until the host attached an adapter (unsupported game, or adapter failed).</summary>
    public IGameAdapter? Adapter { get; private set; }

    public CapabilitySet Capabilities => Adapter?.Capabilities ?? new CapabilitySet();

    /// <summary>GUI layers drawn by the host each OnGUI pass (sorted by <see cref="IGuiLayer.Order"/>).</summary>
    public List<IGuiLayer> GuiLayers { get; } = new();

    public void AttachAdapter(IGameAdapter adapter) => Adapter = adapter;

    /// <summary>Simple service locator for cross-feature APIs (e.g. the audience engine).</summary>
    public void Provide<T>(T service) where T : class => _services[typeof(T)] = service;

    public T? Get<T>() where T : class => _services.TryGetValue(typeof(T), out var s) ? (T)s : null;
}

/// <summary>Ring buffer of recent events for the overlay and the bridge.</summary>
public sealed class EventJournal
{
    private readonly object _gate = new();
    private readonly Queue<Entry> _entries = new();
    private readonly int _capacity;
    private readonly IClock _clock;

    public EventJournal(int capacity, IClock clock)
    {
        _capacity = capacity;
        _clock = clock;
    }

    public void Record(object evt)
    {
        lock (_gate)
        {
            _entries.Enqueue(new Entry(_clock.UtcNow, evt.GetType().Name, evt.ToString() ?? string.Empty));
            while (_entries.Count > _capacity) _entries.Dequeue();
        }
    }

    public Entry[] Snapshot()
    {
        lock (_gate) return _entries.ToArray();
    }

    public sealed class Entry
    {
        public Entry(DateTime at, string type, string text)
        {
            At = at;
            Type = type;
            Text = text;
        }

        public DateTime At { get; }

        public string Type { get; }

        public string Text { get; }
    }
}
