namespace Koko.Core.Events;

public interface IKokoEvent
{
    DateTimeOffset Timestamp { get; }
}

public interface IKokoEventBus
{
    IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : IKokoEvent;

    void Publish<TEvent>(TEvent @event) where TEvent : IKokoEvent;
}

public sealed class KokoEventBus : IKokoEventBus
{
    private readonly object gate = new();
    private readonly Dictionary<Type, List<Delegate>> handlers = [];

    public IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : IKokoEvent
    {
        ArgumentNullException.ThrowIfNull(handler);

        lock (gate)
        {
            var eventType = typeof(TEvent);
            if (!handlers.TryGetValue(eventType, out var list))
            {
                list = [];
                handlers.Add(eventType, list);
            }

            list.Add(handler);
        }

        return new Subscription<TEvent>(this, handler);
    }

    public void Publish<TEvent>(TEvent @event) where TEvent : IKokoEvent
    {
        ArgumentNullException.ThrowIfNull(@event);

        Delegate[] snapshot;
        lock (gate)
        {
            snapshot = handlers.TryGetValue(typeof(TEvent), out var list)
                ? list.ToArray()
                : [];
        }

        foreach (var handler in snapshot)
            ((Action<TEvent>)handler)(@event);
    }

    private void Unsubscribe<TEvent>(Action<TEvent> handler) where TEvent : IKokoEvent
    {
        lock (gate)
        {
            if (!handlers.TryGetValue(typeof(TEvent), out var list))
                return;

            list.Remove(handler);
            if (list.Count == 0)
                handlers.Remove(typeof(TEvent));
        }
    }

    private sealed class Subscription<TEvent> : IDisposable where TEvent : IKokoEvent
    {
        private readonly KokoEventBus bus;
        private Action<TEvent>? handler;

        public Subscription(KokoEventBus bus, Action<TEvent> handler)
        {
            this.bus = bus;
            this.handler = handler;
        }

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref handler, null);
            if (current is not null)
                bus.Unsubscribe(current);
        }
    }
}

public sealed class NullKokoEventBus : IKokoEventBus
{
    public static NullKokoEventBus Instance { get; } = new();

    private NullKokoEventBus()
    {
    }

    public IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : IKokoEvent
    {
        ArgumentNullException.ThrowIfNull(handler);
        return NoopDisposable.Instance;
    }

    public void Publish<TEvent>(TEvent @event) where TEvent : IKokoEvent
    {
        ArgumentNullException.ThrowIfNull(@event);
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}

public enum KokoOperationSeverity
{
    Trace,
    Info,
    Warning,
    Error
}

public sealed record KokoOperationEvent(
    string OperationId,
    string Stage,
    string Message,
    KokoOperationSeverity Severity = KokoOperationSeverity.Info,
    double? Progress = null,
    DateTimeOffset? TimestampOverride = null) : IKokoEvent
{
    public DateTimeOffset Timestamp { get; } = TimestampOverride ?? DateTimeOffset.UtcNow;
}
