using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using WWDS.ModCore.Logging;
using WWDS.Runtime.Patching;

namespace WWDS.Game.Wwds.Mods;

/// <summary>
/// Show progress outside the game's own shows lives in separate save files (shows 31-60 of the other
/// game, own question packs 101+), so the game's "Sessions" file only ever holds shows it can load
/// without the mods. Features register a show id range with its file; saving splits the history by range,
/// loading merges the files back in.
/// </summary>
public static class SessionFiles
{
    private static readonly List<Range> Ranges = new();
    private static ModLog? _log;
    private static bool _patched;

    /// <summary>Stores shows <paramref name="min"/>..<paramref name="max"/> in <paramref name="file"/> while <paramref name="active"/> is true.</summary>
    public static void Register(ModUi ui, int min, int max, Func<string> file, Func<bool> active)
    {
        Ranges.Add(new Range(min, max, file, active));
        if (_patched) return;
        _patched = true;
        _log = ui.Log;
        var none = ModCore.Capabilities.GameCapability.None;
        ui.Patch(new PatchSpec("mods.sessions.save", "Progress of shows outside the game's own goes to separate files.",
            () => AccessTools.Method(typeof(SDataAccess), "SaveSessionHistory"), none)
        {
            Prefix = AccessTools.Method(typeof(SessionFiles), nameof(SavePrefix)),
        });
        ui.Patch(new PatchSpec("mods.sessions.load", "Merge the separate progress files when loading.",
            () => AccessTools.Method(typeof(SDataAccess), "LoadSessionHistory"), none)
        {
            Postfix = AccessTools.Method(typeof(SessionFiles), nameof(LoadPostfix)),
        });
    }

    private static List<Range> Active => Ranges.Where(r => r.Active()).ToList();

    private static bool SavePrefix()
    {
        var active = Active;
        if (active.Count == 0) return true;
        try
        {
            var last = SDataAccess.LastSession;
            var lastId = last != null && !last.IsFinished ? last.ShowID : 0;
            var all = SDataAccess.SessionHistory != null ? SDataAccess.SessionHistory.Values.ToArray() : new DataSession[0];
            SSaveData.Instance.Save(Split(all, lastId, id => !active.Any(r => r.Contains(id))), "Sessions");
            foreach (var range in active) SSaveData.Instance.Save(Split(all, lastId, range.Contains), range.File());
            return false;
        }
        catch (Exception ex)
        {
            _log?.Warn("Save split failed, saving normally: " + ex.Message);
            return true;
        }
    }

    internal static DataSessionHistory Split(DataSession[] all, int lastId, Func<int, bool> here)
    {
        var part = all.Where(s => here(s.ShowID)).ToArray();
        return new DataSessionHistory { LastSessionID = here(lastId) ? lastId : 0, SessionHisory = part.Length > 0 ? part : null };
    }

    private static void LoadPostfix(ref IEnumerator __result)
    {
        if (Ranges.Count > 0) __result = Merge(__result);
    }

    private static IEnumerator Merge(IEnumerator original)
    {
        while (original.MoveNext()) yield return original.Current;
        foreach (var range in Active)
        {
            var data = new DataSessionHistory();
            yield return SSaveData.Instance.Load(range.File());
            SSaveData.Instance.GetSaveData(ref data);
            if (data.SessionHisory == null) continue;
            var count = 0;
            foreach (var session in data.SessionHisory.Where(s => range.Contains(s.ShowID)))
            {
                if (session.ShowID == data.LastSessionID) SDataAccess.LastSession = session;
                SDataAccess.SessionHistory[session.ShowID] = session;
                count++;
            }

            _log?.Info("Loaded " + count + " sessions of shows " + range.Min + "-" + (range.Max == int.MaxValue ? "..." : range.Max.ToString()) + " from " + range.File());
        }
    }

    private sealed class Range
    {
        public Range(int min, int max, Func<string> file, Func<bool> active)
        {
            Min = min;
            Max = max;
            File = file;
            Active = active;
        }

        public int Min { get; }

        public int Max { get; }

        public Func<string> File { get; }

        public Func<bool> Active { get; }

        public bool Contains(int id) => id >= Min && id <= Max;
    }
}
