using WWDS.ModCore;
using WWDS.ModCore.Config;
using WWDS.ModCore.Detection;
using WWDS.ModCore.Logging;
using WWDS.ModCore.Time;

namespace WWDS.Tests;

internal sealed class ListSink : ILogSink
{
    public List<string> Lines { get; } = new();

    public void Write(LogLevel level, string category, string message) => Lines.Add(level + " [" + category + "] " + message);
}

internal static class TestContext
{
    public static ModContext Create(ManualClock? clock = null, ListSink? sink = null)
    {
        sink ??= new ListSink();
        var detection = GameDetector.Detect("WWDS2", "WWDS2_Data", WwdsProfiles.Game2.KnownBuilds[0].AssemblySha256, "2018.2.20f1");
        return new ModContext(new ModLog(sink, LogCategory.Core), new MemoryConfigStore(), clock ?? new ManualClock(), "test", detection);
    }
}
