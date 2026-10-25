using Maldex.Core.Health;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Maldex.Core.Tests.Health;

public class WorkerHealthTests
{
    private static (WorkerHealth Health, FakeTimeProvider Time) Create()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));
        return (new WorkerHealth(time), time);
    }

    [Fact]
    public void Registered_worker_is_not_stalled_until_three_intervals_pass()
    {
        var (health, time) = Create();
        health.Register("w", TimeSpan.FromSeconds(1));

        time.Advance(TimeSpan.FromSeconds(2.9));
        Assert.False(health.Snapshot().Single().Stalled);

        time.Advance(TimeSpan.FromSeconds(0.2));
        Assert.True(health.Snapshot().Single().Stalled);
    }

    [Fact]
    public void Tick_resets_the_stall_clock()
    {
        var (health, time) = Create();
        health.Register("w", TimeSpan.FromSeconds(1));
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.True(health.Snapshot().Single().Stalled);

        health.Tick("w");
        var e = health.Snapshot().Single();
        Assert.False(e.Stalled);
        Assert.Equal(time.GetUtcNow(), e.LastTickAt);

        time.Advance(TimeSpan.FromSeconds(2.9));
        Assert.False(health.Snapshot().Single().Stalled);
    }

    [Fact]
    public void Worker_that_stops_ticking_becomes_stalled()
    {
        var (health, time) = Create();
        health.Register("w", TimeSpan.FromSeconds(1));
        for (var i = 0; i < 5; i++) { time.Advance(TimeSpan.FromSeconds(1)); health.Tick("w"); }
        Assert.False(health.Snapshot().Single().Stalled);

        time.Advance(TimeSpan.FromSeconds(3.5));
        Assert.True(health.Snapshot().Single().Stalled);
    }

    [Fact]
    public void Snapshot_is_ordered_by_name_and_unknown_tick_registers_nothing()
    {
        var (health, _) = Create();
        health.Register("b", TimeSpan.FromSeconds(1));
        health.Register("a", TimeSpan.FromSeconds(1));
        health.Register("b", TimeSpan.FromHours(1)); // idempotent: keeps the first
        health.Tick("nope");

        var snapshot = health.Snapshot();
        Assert.Equal(["a", "b"], snapshot.Select(e => e.Name));
        Assert.Equal(TimeSpan.FromSeconds(1), snapshot[1].ExpectedInterval);
        Assert.All(snapshot, e => Assert.Null(e.LastTickAt));
    }
}
