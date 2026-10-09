using System;
using UnityEngine;
using WWDS.ModCore.Time;

namespace WWDS.Runtime;

/// <summary>Unity's unscaled realtime clock; safe to read only from the main thread.</summary>
public sealed class UnityClock : IClock
{
    public double Now => Time.realtimeSinceStartup;

    public DateTime UtcNow => DateTime.UtcNow;
}
