using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Maldex.Integration.Tests.Fixtures;
using Maldex.Integration.Tests.Orchestrator;
using Maldex.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Maldex.Integration.Tests.Processing;

[Collection("sql")]
public class ProcessingRetryCapTests(SqlServerFixture sql)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class AlwaysThrowingPipeline(OrchestratorHarness h) : ProcessingPipeline(
        h.Get<IServiceScopeFactory>(), h.Get<IYaraScanner>(), h.Get<ISuricataRunner>(), h.Get<IArtifactStore>(),
        h.Get<ITaskEventPublisher>(), h.Time, NullLogger<ProcessingPipeline>.Instance)
    {
        public int Calls;

        public override Task ProcessAsync(Guid taskId, CancellationToken ct)
        {
            Calls++;
            throw new InvalidOperationException("boom");
        }
    }

    [Fact]
    public async Task ProcessingWorker_PersistentFailure_EventuallyFailsTask()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql);
        var id = await h.SeedPreparingTaskAsync();
        await h.RunAsync(id);
        Assert.Equal(RunStatus.Processing, await h.StatusOfAsync(id));

        var pipeline = new AlwaysThrowingPipeline(h);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Maldex:Processing:MaxAttempts"] = "3" }).Build();
        var worker = new ProcessingWorker(h.Get<IServiceScopeFactory>(), pipeline, h.Time, config, NullLogger<ProcessingWorker>.Instance);

        await worker.ProcessPendingAsync(Ct);
        Assert.Equal(1, pipeline.Calls);
        await worker.ProcessPendingAsync(Ct); // still backing off (2 s)
        Assert.Equal(1, pipeline.Calls);
        Assert.Equal(RunStatus.Processing, await h.StatusOfAsync(id));

        h.Time.Advance(TimeSpan.FromSeconds(2));
        await worker.ProcessPendingAsync(Ct);
        Assert.Equal(2, pipeline.Calls);

        h.Time.Advance(TimeSpan.FromSeconds(3)); // backing off 4 s
        await worker.ProcessPendingAsync(Ct);
        Assert.Equal(2, pipeline.Calls);

        h.Time.Advance(TimeSpan.FromSeconds(1));
        await worker.ProcessPendingAsync(Ct);
        Assert.Equal(3, pipeline.Calls);

        await using var db = h.NewContext();
        var task = await db.Tasks.SingleAsync(t => t.Id == id, Ct);
        Assert.Equal(RunStatus.Failed, task.Status);
        Assert.Equal("processing failed: boom", task.FailureReason);

        await worker.ProcessPendingAsync(Ct);
        Assert.Equal(3, pipeline.Calls); // terminal: never picked up again
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(5, 32)]
    [InlineData(6, 60)]
    [InlineData(20, 60)]
    public void Backoff_DoublesAndCapsAtSixtySeconds(int failures, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), ProcessingWorker.BackoffFor(failures));
}
