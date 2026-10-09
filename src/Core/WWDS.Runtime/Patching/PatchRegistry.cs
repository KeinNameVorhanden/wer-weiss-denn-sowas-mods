using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using WWDS.ModCore.Capabilities;
using WWDS.ModCore.Config;
using WWDS.ModCore.Logging;

namespace WWDS.Runtime.Patching;

public enum PatchStatus
{
    Pending,
    Applied,
    TargetMissing,
    Failed,
    DisabledByConfig,
}

/// <summary>
/// One Harmony patch with a documented reason and the capabilities it backs. Patch method bodies
/// must catch their own exceptions (see <see cref="PatchGuard"/>).
/// </summary>
public sealed class PatchSpec
{
    public PatchSpec(string id, string reason, Func<MethodBase?> target, GameCapability backs)
    {
        Id = id;
        Reason = reason;
        Target = target;
        Backs = backs;
    }

    public string Id { get; }

    /// <summary>Why the patch exists (shown in the overlay and docs).</summary>
    public string Reason { get; }

    public Func<MethodBase?> Target { get; }

    /// <summary>Capabilities that are withheld if this patch is not applied.</summary>
    public GameCapability Backs { get; }

    public MethodInfo? Prefix { get; set; }

    public MethodInfo? Postfix { get; set; }

    /// <summary>Only applied when the feature that needs it asks (e.g. persistence suppression).</summary>
    public bool Optional { get; set; }

    public PatchStatus Status { get; internal set; } = PatchStatus.Pending;

    public string? Error { get; internal set; }

    public string? TargetName { get; internal set; }
}

/// <summary>
/// Applies patches one by one so a missing method only disables what depends on it. Each patch can be
/// switched off in config ([Patches] &lt;id&gt; = false) for troubleshooting after a game update.
/// </summary>
public sealed class PatchRegistry
{
    private readonly Harmony _harmony;
    private readonly IConfigStore _config;
    private readonly ModLog _log;
    private readonly List<PatchSpec> _patches = new();

    public PatchRegistry(string harmonyId, IConfigStore config, ModLog log)
    {
        _harmony = new Harmony(harmonyId);
        _config = config;
        _log = log;
    }

    public IList<PatchSpec> Patches => _patches.AsReadOnly();

    public PatchSpec Add(PatchSpec spec)
    {
        _patches.Add(spec);
        return spec;
    }

    public void ApplyAll()
    {
        foreach (var p in _patches.Where(p => !p.Optional)) Apply(p);
    }

    public bool Apply(PatchSpec p)
    {
        if (p.Status == PatchStatus.Applied) return true;
        var enabled = _config.Bind("Patches", p.Id, true, p.Reason);
        if (!enabled.Value)
        {
            p.Status = PatchStatus.DisabledByConfig;
            _log.Warn("Patch " + p.Id + " disabled by config");
            return false;
        }

        MethodBase? target;
        try
        {
            target = p.Target();
        }
        catch (Exception ex)
        {
            target = null;
            p.Error = ex.Message;
        }

        if (target == null)
        {
            p.Status = PatchStatus.TargetMissing;
            p.Error ??= "target method not found";
            _log.Warn("Patch " + p.Id + ": " + p.Error);
            return false;
        }

        p.TargetName = target.DeclaringType?.Name + "." + target.Name;
        try
        {
            _harmony.Patch(target, p.Prefix == null ? null : new HarmonyMethod(p.Prefix), p.Postfix == null ? null : new HarmonyMethod(p.Postfix));
            p.Status = PatchStatus.Applied;
            _log.Info("Patch " + p.Id + " applied to " + p.TargetName);
            return true;
        }
        catch (Exception ex)
        {
            p.Status = PatchStatus.Failed;
            p.Error = ex.GetType().Name + ": " + ex.Message;
            _log.Error("Patch " + p.Id + " failed on " + p.TargetName, ex);
            return false;
        }
    }

    /// <summary>
    /// Grants <paramref name="candidates"/> minus every capability backed by a non-applied,
    /// non-optional patch.
    /// </summary>
    public void ComputeCapabilities(CapabilitySet set, GameCapability candidates)
    {
        set.Grant(candidates);
        foreach (var p in _patches.Where(p => !p.Optional && p.Status != PatchStatus.Applied && p.Backs != GameCapability.None))
        {
            set.Deny(p.Backs & candidates, "patch " + p.Id + ": " + (p.Error ?? p.Status.ToString()));
        }
    }

    public void UnpatchAll()
    {
        try
        {
            _harmony.UnpatchSelf();
        }
        catch (Exception ex)
        {
            _log.Error("Unpatch failed", ex);
        }
    }
}

/// <summary>Runs patch bodies so that a bug in the harness can never break the game method.</summary>
public static class PatchGuard
{
    public static ModLog? Log { get; set; }

    public static void Run(string where, Action body)
    {
        try
        {
            body();
        }
        catch (Exception ex)
        {
            Log?.Error("Patch body " + where + " failed", ex);
        }
    }
}
