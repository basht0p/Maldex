using Maldex.Core.Health;
using Maldex.Infrastructure.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Maldex.Core.Tests.Health;

public class MediaMountSweepServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task WaitForTickAsync(WorkerHealth health, DateTimeOffset after)
    {
        for (var i = 0; i < 500; i++)
        {
            if (health.Snapshot().Single().LastTickAt is { } t && t > after) return;
            await Task.Delay(10, Ct);
        }
        Assert.Fail("the sweep loop did not tick");
    }

    [Fact]
    public async Task Loop_ticks_at_start_and_again_after_its_interval_and_is_a_no_op_without_an_inspector()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));
        var health = new WorkerHealth(time);
        using var provider = new ServiceCollection().BuildServiceProvider(); // no LoopMountMediaInspector, as outside Real mode
        using var service = new MediaMountSweepService(provider, time, NullLogger<MediaMountSweepService>.Instance, health);

        var start = time.GetUtcNow();
        await service.StartAsync(Ct);
        try
        {
            await WaitForTickAsync(health, start.AddTicks(-1));
            Assert.Equal(start, health.Snapshot().Single().LastTickAt);

            time.Advance(MediaMountSweepService.SweepInterval);
            await WaitForTickAsync(health, start);
            Assert.Equal(start + MediaMountSweepService.SweepInterval, health.Snapshot().Single().LastTickAt);
            Assert.False(service.ExecuteTask!.IsFaulted);
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }
}
