using System.Collections.Generic;

namespace WWDS.ModCore.Adapters;

public sealed class PatchInfo
{
    public PatchInfo(string id, string target, string status, string reason, string? error)
    {
        Id = id;
        Target = target;
        Status = status;
        Reason = reason;
        Error = error;
    }

    public string Id { get; }

    public string Target { get; }

    public string Status { get; }

    public string Reason { get; }

    public string? Error { get; }
}

/// <summary>Optional adapter interface: lists its patches for diagnostics UIs.</summary>
public interface IPatchStatusProvider
{
    IList<PatchInfo> GetPatchStatus();
}
