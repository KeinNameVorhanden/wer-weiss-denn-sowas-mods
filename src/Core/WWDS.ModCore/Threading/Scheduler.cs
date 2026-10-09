using System;
using System.Collections.Generic;
using WWDS.ModCore.Time;

namespace WWDS.ModCore.Threading;

/// <summary>
/// Main-thread delayed actions driven by the host's per-frame tick. Used instead of timers/threads so
/// that everything touching game state stays on the main thread and is testable with a manual clock.
/// </summary>
public sealed class Scheduler
{
    private readonly IClock _clock;
    private readonly Action<Exception> _onError;
    private readonly List<Item> _items = new();
    private long _sequence;

    public Scheduler(IClock clock, Action<Exception> onError)
    {
        _clock = clock;
        _onError = onError;
    }

    public int Count => _items.Count;

    /// <summary>Runs <paramref name="action"/> once after <paramref name="seconds"/>. Dispose the handle to cancel.</summary>
    public IDisposable After(double seconds, Action action)
    {
        var item = new Item(_clock.Now + Math.Max(0, seconds), ++_sequence, action);
        _items.Add(item);
        return item;
    }

    public void Tick()
    {
        if (_items.Count == 0) return;
        var now = _clock.Now;
        var due = new List<Item>();
        for (var i = _items.Count - 1; i >= 0; i--)
        {
            var item = _items[i];
            if (item.Cancelled)
            {
                _items.RemoveAt(i);
            }
            else if (item.Due <= now)
            {
                due.Add(item);
                _items.RemoveAt(i);
            }
        }

        due.Sort((x, y) => x.Due != y.Due ? x.Due.CompareTo(y.Due) : x.Sequence.CompareTo(y.Sequence));
        foreach (var item in due)
        {
            try
            {
                item.Action();
            }
            catch (Exception ex)
            {
                _onError(ex);
            }
        }
    }

    private sealed class Item : IDisposable
    {
        public Item(double due, long sequence, Action action)
        {
            Due = due;
            Sequence = sequence;
            Action = action;
        }

        public double Due { get; }

        public long Sequence { get; }

        public Action Action { get; }

        public bool Cancelled { get; private set; }

        public void Dispose() => Cancelled = true;
    }
}

/// <summary>Something drawn by the host every GUI pass (IMGUI). Implemented by UI features.</summary>
public interface IGuiLayer
{
    /// <summary>Lower draws first.</summary>
    int Order { get; }

    void Draw();
}

/// <summary>Host services features may need without depending on Unity or the loader.</summary>
public interface IHostControl
{
    void QuitGame();

    bool RunInBackground { get; set; }

    /// <summary>Folder for harness data (statistics, logs, question packs): BepInEx/wwds by default.</summary>
    string DataDirectory { get; }
}
