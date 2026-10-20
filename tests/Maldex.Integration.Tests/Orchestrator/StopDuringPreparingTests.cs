using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Maldex.Integration.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Maldex.Integration.Tests.Orchestrator;

[Collection("sql")]
public class StopDuringPreparingTests(SqlServerFixture sql)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Stop_DuringPreparing_EndsPromptly()
    {
        // The agent would only become ready after ten minutes; the ready timeout is longer still.
        var dir = Directory.CreateTempSubdirectory("maldex-replay-").FullName;
        await File.WriteAllLinesAsync(Path.Combine(dir, "slow.jsonl"),
            ["""{"delayMs":600000,"message":{"type":"ready","agentVersion":"1","hostname":"h"}}"""], Ct);
        try
        {
            await using var h = await OrchestratorHarness.CreateAsync(sql, new Dictionary<string, string?>
            {
                ["Maldex:Fake:Replay:Directory"] = dir,
                ["Maldex:Fake:Replay:DefaultFile"] = "slow.jsonl",
                ["Maldex:Fake:Replay:Speed"] = "1",
                ["Maldex:Fake:Replay:HoldOpen"] = "true",
                ["Maldex:Orchestrator:AgentReadyTimeout"] = "01:00:00",
            });
            var id = await h.SeedPreparingTaskAsync();
            var requestedAt = h.Time.GetUtcNow().AddSeconds(1);
            var sinceStop = new System.Diagnostics.Stopwatch();

            await h.RunAsync(id, async () =>
            {
                while (h.Time.GetUtcNow() < requestedAt) await Task.Delay(1, Ct);
                await using var api = h.NewContext();
                var t = await api.Tasks.SingleAsync(x => x.Id == id, Ct);
                t.StopRequestedAt = h.Time.GetUtcNow();
                await api.SaveChangesAsync(Ct);
                sinceStop.Start();
            });
            sinceStop.Stop();

            await using var db = h.NewContext();
            var task = await db.Tasks.SingleAsync(t => t.Id == id, Ct);
            Assert.Equal(RunStatus.Cancelled, task.Status);
            // Ended within a few poll intervals of the request (real time), not at the ready timeout.
            Assert.True(sinceStop.Elapsed < TimeSpan.FromSeconds(5), $"run took {sinceStop.Elapsed} to end after the stop request");
            var last = (await db.TaskStatusHistory.Where(x => x.TaskId == id).ToListAsync(Ct)).OrderBy(x => x.Id).Last();
            Assert.Equal(RunStatus.Preparing, last.From);
            Assert.Equal("stopped by analyst", last.Reason);
            Assert.Empty(await h.Get<IHypervisor>().ListAsync(Ct));
            Assert.Empty(await h.Get<IEgressRouter>().ListAsync(Ct));
        }
        finally { Directory.Delete(dir, true); }
    }
}
