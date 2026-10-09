using System;
using System.Collections.Generic;

namespace WWDS.ModCore.Threading;

/// <summary>
/// Hands work from background threads (network transports) to the Unity main thread.
/// The host calls <see cref="Drain"/> once per frame. Game state may only be touched there.
/// </summary>
public sealed class MainThreadQueue
{
    private readonly object _gate = new();
    private readonly Queue<Action> _pending = new();
    private readonly Action<Exception> _onError;
    private readonly int _maxPerDrain;

    public MainThreadQueue(Action<Exception> onError, int maxPerDrain = 256)
    {
        _onError = onError ?? throw new ArgumentNullException(nameof(onError));
        _maxPerDrain = maxPerDrain > 0 ? maxPerDrain : throw new ArgumentOutOfRangeException(nameof(maxPerDrain));
    }

    public int Count
    {
        get { lock (_gate) return _pending.Count; }
    }

    public void Post(Action work)
    {
        if (work == null) throw new ArgumentNullException(nameof(work));
        lock (_gate) _pending.Enqueue(work);
    }

    /// <summary>
    /// Runs queued work on the calling (main) thread. A failing item is reported and does not stop
    /// the remaining items. At most <c>maxPerDrain</c> items run per call so a flood of chat
    /// messages cannot stall a frame; the rest run next frame.
    /// </summary>
    public int Drain()
    {
        var ran = 0;
        while (ran < _maxPerDrain)
        {
            Action next;
            lock (_gate)
            {
                if (_pending.Count == 0) break;
                next = _pending.Dequeue();
            }

            ran++;
            try
            {
                next();
            }
            catch (Exception ex)
            {
                _onError(ex);
            }
        }

        return ran;
    }
}
