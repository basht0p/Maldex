using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Maldex.Integration.Tests.Fixtures;
using Maldex.Orchestrator;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Maldex.Integration.Tests.Orchestrator;

[Collection("sql")]
public class StopDuringPreparingTests(SqlServerFixture sql)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string ReadyAfterTenMinutes = """{"delayMs":600000,"message":{"type":"ready","agentVersion":"1","hostname":"h"}}""";
    private const string ChattyFile = """{"delayMs":100,"message":{"type":"file","at":"2000-01-01T00:00:00Z","pid":10,"operation":"write","path":"C:\\f.txt"}}""";

    /// <summary>Runs a task whose agent never becomes ready in time, requests a stop, and checks it ends promptly.</summary>
    private async Task AssertStopEndsPromptlyAsync(params string[] replayLines)
    {
        var dir = Directory.CreateTempSubdirectory("maldex-replay-").FullName;
        await File.WriteAllLinesAsync(Path.Combine(dir, "slow.jsonl"), replayLines, Ct);
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

            // Drive the fake clock by hand: one second at a time, and only after the run has had real time to react,
            // so database latency cannot make the fake clock race ahead of the coordinator.
            var run = Task.Run(() => h.Get<RunCoordinator>().RunAsync(id, Ct), Ct);
            await Task.Delay(300, Ct); // let it reach the ready wait
            await using (var api = h.NewContext())
            {
                var t = await api.Tasks.SingleAsync(x => x.Id == id, Ct);
                t.StopRequestedAt = h.Time.GetUtcNow();
                await api.SaveChangesAsync(Ct);
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!run.IsCompleted)
            {
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), "Run did not end after the stop request.");
                h.Time.Advance(TimeSpan.FromSeconds(1));
                await Task.WhenAny(run, Task.Delay(200, Ct));
            }
            await run;

            await using var db = h.NewContext();
            var task = await db.Tasks.SingleAsync(t => t.Id == id, Ct);
            Assert.Equal(RunStatus.Cancelled, task.Status);
            // Ended within a few poll intervals of the request (fake time), not at the ready timeout or when the chatter stops.
            var took = task.EndedAt!.Value - task.StopRequestedAt!.Value;
            Assert.True(took <= TimeSpan.FromSeconds(5), $"run took {took} (fake time) to end after the stop request");
            var last = (await db.TaskStatusHistory.Where(x => x.TaskId == id).ToListAsync(Ct)).OrderBy(x => x.Id).Last();
            Assert.Equal(RunStatus.Preparing, last.From);
            Assert.Equal("stopped by analyst", last.Reason);
            Assert.Empty(await h.Get<IHypervisor>().ListAsync(Ct));
            Assert.Empty(await h.Get<IEgressRouter>().ListAsync(Ct));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public Task Stop_DuringPreparing_EndsPromptly() => AssertStopEndsPromptlyAsync(ReadyAfterTenMinutes);

    [Fact]
    public Task Stop_DuringPreparing_WithChattyGuest_EndsPromptly() =>
        // A message every 100 ms for 60 s, faster than the 1 s poll interval, then ready.
        AssertStopEndsPromptlyAsync([.. Enumerable.Repeat(ChattyFile, 600), ReadyAfterTenMinutes]);
}
