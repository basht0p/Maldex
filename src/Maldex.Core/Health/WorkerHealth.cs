using System.Collections.Concurrent;

namespace Maldex.Core.Health;

public sealed record WorkerHealthEntry(string Name, DateTimeOffset RegisteredAt, DateTimeOffset? LastTickAt, TimeSpan ExpectedInterval, bool Stalled);

/// <summary>
/// Liveness of the background workers: each registers with the interval it normally ticks at and ticks once per loop iteration.
/// A worker is stalled once <c>now - (lastTick ?? registeredAt)</c> exceeds <see cref="StallFactor"/> intervals, but never under <see cref="MinStallThreshold"/>.
/// </summary>
public sealed class WorkerHealth(TimeProvider time)
{
    public const int StallFactor = 3;

    /// <summary>Floor of the stall threshold: a fast poller whose single pass is long (a Suricata run) is not stalled for it.</summary>
    public static readonly TimeSpan MinStallThreshold = TimeSpan.FromSeconds(60);

    private sealed class State(DateTimeOffset registeredAt, TimeSpan interval)
    {
        public readonly DateTimeOffset RegisteredAt = registeredAt;
        public readonly TimeSpan Interval = interval;
        public long LastTickTicks; // UtcTicks; 0 = never
    }

    private readonly ConcurrentDictionary<string, State> _workers = new(StringComparer.Ordinal);

    /// <summary>Null-safe registration for field initializers: returns <paramref name="health"/> so a worker can keep it.</summary>
    public static WorkerHealth? RegisterOn(WorkerHealth? health, string name, TimeSpan expectedInterval)
    {
        health?.Register(name, expectedInterval);
        return health;
    }

    /// <summary>Idempotent: a second registration of the same name keeps the first.</summary>
    public void Register(string name, TimeSpan expectedInterval) =>
        _workers.TryAdd(name, new State(time.GetUtcNow(), expectedInterval));

    /// <summary>Records a loop iteration. A name that was never registered is ignored.</summary>
    public void Tick(string name)
    {
        if (_workers.TryGetValue(name, out var s)) Interlocked.Exchange(ref s.LastTickTicks, time.GetUtcNow().UtcTicks);
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    public IReadOnlyList<WorkerHealthEntry> Snapshot()
    {
        var now = time.GetUtcNow();
        return [.. _workers.OrderBy(w => w.Key, StringComparer.Ordinal).Select(w =>
        {
            var last = Interlocked.Read(ref w.Value.LastTickTicks);
            DateTimeOffset? lastTick = last == 0 ? null : new DateTimeOffset(last, TimeSpan.Zero);
            var stalled = now - (lastTick ?? w.Value.RegisteredAt) > Max(w.Value.Interval * StallFactor, MinStallThreshold);
            return new WorkerHealthEntry(w.Key, w.Value.RegisteredAt, lastTick, w.Value.Interval, stalled);
        })];
    }
}
