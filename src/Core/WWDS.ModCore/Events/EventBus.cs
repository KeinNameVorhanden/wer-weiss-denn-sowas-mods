using System;
using System.Collections.Generic;

namespace WWDS.ModCore.Events;

/// <summary>
/// Synchronous, type-keyed publish/subscribe. Publish from the main thread (network threads post
/// through <see cref="Threading.MainThreadQueue"/> first). Handlers are snapshotted per publish, so
/// subscribing or unsubscribing inside a handler is safe. A failing handler is reported and never
/// prevents the other handlers from running.
/// </summary>
public sealed class EventBus
{
    private readonly object _gate = new();
    private readonly Dictionary<Type, List<Subscription>> _byType = new();
    private readonly List<Subscription> _all = new();
    private readonly Action<object, Exception> _onHandlerError;

    public EventBus(Action<object, Exception> onHandlerError)
    {
        _onHandlerError = onHandlerError ?? throw new ArgumentNullException(nameof(onHandlerError));
    }

    /// <summary>Receives every event of exactly type <typeparamref name="T"/> (or a subclass of it).</summary>
    public IDisposable Subscribe<T>(Action<T> handler) where T : class
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        var sub = new Subscription(this, typeof(T), e => handler((T)e));
        lock (_gate)
        {
            if (!_byType.TryGetValue(typeof(T), out var list)) _byType[typeof(T)] = list = new List<Subscription>();
            list.Add(sub);
        }

        return sub;
    }

    /// <summary>Receives every published event (event log, overlay, bridge mirroring).</summary>
    public IDisposable SubscribeAll(Action<object> handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        var sub = new Subscription(this, null, handler);
        lock (_gate) _all.Add(sub);
        return sub;
    }

    public void Publish(object evt)
    {
        if (evt == null) throw new ArgumentNullException(nameof(evt));
        var targets = new List<Subscription>();
        lock (_gate)
        {
            for (var type = evt.GetType(); type != null; type = type.BaseType)
            {
                if (_byType.TryGetValue(type, out var list)) targets.AddRange(list);
            }

            targets.AddRange(_all);
        }

        foreach (var sub in targets)
        {
            if (sub.Disposed) continue;
            try
            {
                sub.Invoke(evt);
            }
            catch (Exception ex)
            {
                _onHandlerError(evt, ex);
            }
        }
    }

    private void Remove(Subscription sub)
    {
        lock (_gate)
        {
            if (sub.EventType == null) _all.Remove(sub);
            else if (_byType.TryGetValue(sub.EventType, out var list)) list.Remove(sub);
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly EventBus _owner;
        private readonly Action<object> _handler;

        public Subscription(EventBus owner, Type? eventType, Action<object> handler)
        {
            _owner = owner;
            EventType = eventType;
            _handler = handler;
        }

        public Type? EventType { get; }

        public bool Disposed { get; private set; }

        public void Invoke(object evt) => _handler(evt);

        public void Dispose()
        {
            if (Disposed) return;
            Disposed = true;
            _owner.Remove(this);
        }
    }
}
