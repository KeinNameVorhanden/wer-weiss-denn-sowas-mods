using System;
using System.Collections.Generic;
using System.Linq;
using WWDS.ModCore.Capabilities;
using WWDS.ModCore.Config;
using WWDS.ModCore.Logging;

namespace WWDS.ModCore.Features;

public enum FeatureState
{
    Registered,
    Disabled,
    MissingCapabilities,
    MissingDependency,
    Running,
    Faulted,
    Stopped,
}

/// <summary>
/// A self-contained, independently switchable module ("NewFeature.cs"). Subscribe to events in
/// <see cref="Start"/>, dispose subscriptions in <see cref="Stop"/>.
/// </summary>
public interface IFeature
{
    /// <summary>Stable id, also the config key under [Features].</summary>
    string Id { get; }

    string DisplayName { get; }

    string Description { get; }

    bool EnabledByDefault { get; }

    /// <summary>Ids of features that must be running first.</summary>
    IEnumerable<string> Dependencies { get; }

    /// <summary>Adapter capabilities without which the feature cannot work.</summary>
    GameCapability RequiredCapabilities { get; }

    void Start(ModContext context);

    /// <summary>Per-frame work on the main thread. Keep it cheap.</summary>
    void Tick();

    void Stop();
}

/// <summary>Convenience base: tracks subscriptions and disposes them on stop.</summary>
public abstract class FeatureBase : IFeature
{
    private readonly List<IDisposable> _subscriptions = new();

    public abstract string Id { get; }

    public abstract string DisplayName { get; }

    public virtual string Description => string.Empty;

    public virtual bool EnabledByDefault => true;

    public virtual IEnumerable<string> Dependencies => NoDependencies;

    private static readonly string[] NoDependencies = new string[0];

    public virtual GameCapability RequiredCapabilities => GameCapability.None;

    protected ModContext Context { get; private set; } = null!;

    protected ModLog Log { get; private set; } = null!;

    public void Start(ModContext context)
    {
        Context = context;
        Log = context.Log.For(LogCategoryName);
        OnStart();
    }

    protected virtual string LogCategoryName => Id;

    protected abstract void OnStart();

    public virtual void Tick()
    {
    }

    public void Stop()
    {
        try
        {
            OnStop();
        }
        finally
        {
            foreach (var s in _subscriptions) s.Dispose();
            _subscriptions.Clear();
        }
    }

    protected virtual void OnStop()
    {
    }

    protected void Track(IDisposable subscription) => _subscriptions.Add(subscription);
}

public sealed class FeatureInfo
{
    internal FeatureInfo(IFeature feature, IConfigValue<bool> enabled)
    {
        Feature = feature;
        Enabled = enabled;
    }

    public IFeature Feature { get; }

    public IConfigValue<bool> Enabled { get; }

    public FeatureState State { get; internal set; } = FeatureState.Registered;

    public string? Detail { get; internal set; }

    internal int ConsecutiveTickFailures { get; set; }
}

/// <summary>
/// Starts features in dependency order, skips those that are disabled or lack capabilities, and
/// isolates failures: an exception in Start or repeated exceptions in Tick fault only that feature
/// (and anything depending on it), never the harness or the game.
/// </summary>
public sealed class FeatureRegistry
{
    public const int MaxConsecutiveTickFailures = 5;

    private readonly List<FeatureInfo> _features = new();
    private readonly IConfigStore _config;
    private readonly ModLog _log;

    public FeatureRegistry(IConfigStore config, ModLog log)
    {
        _config = config;
        _log = log;
    }

    public IList<FeatureInfo> Features => _features.AsReadOnly();

    public FeatureInfo Register(IFeature feature)
    {
        if (_features.Any(f => f.Feature.Id == feature.Id)) throw new InvalidOperationException("Duplicate feature id " + feature.Id);
        var enabled = _config.Bind("Features", feature.Id, feature.EnabledByDefault, feature.DisplayName + ": " + feature.Description);
        var info = new FeatureInfo(feature, enabled);
        _features.Add(info);
        return info;
    }

    public FeatureInfo? Find(string id) => _features.FirstOrDefault(f => f.Feature.Id == id);

    public void StartAll(ModContext context, CapabilitySet capabilities)
    {
        foreach (var info in Order())
        {
            TryStart(info, context, capabilities);
        }
    }

    /// <summary>Starts one feature if its switch, capabilities and dependencies allow it.</summary>
    public bool TryStart(FeatureInfo info, ModContext context, CapabilitySet capabilities)
    {
        if (info.State == FeatureState.Running) return true;
        if (!info.Enabled.Value)
        {
            Set(info, FeatureState.Disabled, "disabled in config");
            return false;
        }

        var missing = capabilities.Missing(info.Feature.RequiredCapabilities);
        if (missing != GameCapability.None)
        {
            Set(info, FeatureState.MissingCapabilities, "game adapter lacks " + missing);
            return false;
        }

        foreach (var dep in info.Feature.Dependencies)
        {
            var d = Find(dep);
            if (d == null || d.State != FeatureState.Running)
            {
                Set(info, FeatureState.MissingDependency, "requires feature '" + dep + "'");
                return false;
            }
        }

        try
        {
            info.Feature.Start(context);
            info.ConsecutiveTickFailures = 0;
            Set(info, FeatureState.Running, null);
            return true;
        }
        catch (Exception ex)
        {
            SafeStop(info);
            Set(info, FeatureState.Faulted, "start failed: " + ex.Message);
            _log.Error("Feature " + info.Feature.Id + " failed to start", ex);
            return false;
        }
    }

    public void Stop(FeatureInfo info, string reason = "stopped")
    {
        foreach (var dependent in _features.Where(f => f.State == FeatureState.Running && f.Feature.Dependencies.Contains(info.Feature.Id)).ToList())
        {
            Stop(dependent, "dependency '" + info.Feature.Id + "' stopped");
        }

        if (info.State != FeatureState.Running) return;
        SafeStop(info);
        Set(info, FeatureState.Stopped, reason);
    }

    public void StopAll()
    {
        foreach (var info in Order().Reverse())
        {
            if (info.State == FeatureState.Running) Stop(info, "shutdown");
        }
    }

    public void TickAll()
    {
        foreach (var info in _features)
        {
            if (info.State != FeatureState.Running) continue;
            try
            {
                info.Feature.Tick();
                info.ConsecutiveTickFailures = 0;
            }
            catch (Exception ex)
            {
                info.ConsecutiveTickFailures++;
                _log.Error("Feature " + info.Feature.Id + " tick failed (" + info.ConsecutiveTickFailures + "/" + MaxConsecutiveTickFailures + ")", ex);
                if (info.ConsecutiveTickFailures >= MaxConsecutiveTickFailures)
                {
                    Stop(info, "too many tick failures");
                    info.State = FeatureState.Faulted;
                }
            }
        }
    }

    /// <summary>Topological order by dependencies; registration order breaks ties; cycles are reported.</summary>
    private IEnumerable<FeatureInfo> Order()
    {
        var result = new List<FeatureInfo>();
        var visiting = new HashSet<string>();
        var done = new HashSet<string>();

        void Visit(FeatureInfo f)
        {
            if (done.Contains(f.Feature.Id)) return;
            if (!visiting.Add(f.Feature.Id))
            {
                _log.Warn("Feature dependency cycle at " + f.Feature.Id);
                return;
            }

            foreach (var dep in f.Feature.Dependencies)
            {
                var d = Find(dep);
                if (d != null) Visit(d);
            }

            visiting.Remove(f.Feature.Id);
            done.Add(f.Feature.Id);
            result.Add(f);
        }

        foreach (var f in _features) Visit(f);
        return result;
    }

    private void SafeStop(FeatureInfo info)
    {
        try
        {
            info.Feature.Stop();
        }
        catch (Exception ex)
        {
            _log.Error("Feature " + info.Feature.Id + " failed to stop", ex);
        }
    }

    private void Set(FeatureInfo info, FeatureState state, string? detail)
    {
        info.State = state;
        info.Detail = detail;
        _log.Info("Feature " + info.Feature.Id + ": " + state + (detail == null ? string.Empty : " (" + detail + ")"));
    }
}
