using System.Text.Json;
using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Maldex.Data;
using Maldex.Integration.Tests.Fixtures;
using Maldex.Orchestrator;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Maldex.Integration.Tests.Orchestrator;

[Collection("sql")]
public class FailureHandlingTests(SqlServerFixture sql)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<List<TaskStatusHistory>> HistoryAsync(MaldexDbContext db, Guid taskId) =>
        (await db.TaskStatusHistory.Where(h => h.TaskId == taskId).ToListAsync(Ct)).OrderBy(h => h.Id).ToList();

    private static async Task<string> WriteReplayAsync(params string[] lines)
    {
        var dir = Directory.CreateTempSubdirectory("maldex-replay-").FullName;
        await File.WriteAllLinesAsync(Path.Combine(dir, "custom.jsonl"), lines, Ct);
        return dir;
    }

    private static Dictionary<string, string?> Replay(string dir, bool holdOpen) => new()
    {
        ["Maldex:Fake:Replay:Directory"] = dir,
        ["Maldex:Fake:Replay:DefaultFile"] = "custom.jsonl",
        ["Maldex:Fake:Replay:HoldOpen"] = holdOpen ? "true" : "false",
    };

    private const string ReadyLine = """{"delayMs":1,"message":{"type":"ready","agentVersion":"1","hostname":"h"}}""";

    [Fact]
    public async Task EgressUnavailable_FailsInPreparing_NoVmStarted()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql);
        var id = await h.SeedPreparingTaskAsync(NetworkMode.LiveVpn);

        await h.RunAsync(id);

        await using var db = h.NewContext();
        var task = await db.Tasks.SingleAsync(t => t.Id == id, Ct);
        Assert.Equal(RunStatus.Failed, task.Status);
        Assert.Equal("egress unavailable: live egress is not available in fake mode", task.FailureReason);
        var failed = (await HistoryAsync(db, id)).Last();
        Assert.Equal(RunStatus.Preparing, failed.From);
        Assert.Equal(RunStatus.Failed, failed.To);
        Assert.Equal(0, task.Attempt); // a VM start was never attempted
        Assert.Empty(h.Channels.Channels);
        Assert.Empty(await h.Get<IHypervisor>().ListAsync(Ct));
        Assert.Empty(await h.Get<IEgressRouter>().ListAsync(Ct));
    }

    [Fact]
    public async Task VmFailsOnce_RetriesAndSucceeds()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql, new Dictionary<string, string?> { ["Maldex:Fake:Hypervisor:FailStarts"] = "1" });
        var id = await h.SeedPreparingTaskAsync();

        await h.RunAsync(id);

        await using var db = h.NewContext();
        var task = await db.Tasks.SingleAsync(t => t.Id == id, Ct);
        Assert.Equal(RunStatus.Processing, task.Status);
        Assert.Equal(2, task.Attempt);
        Assert.Null(task.FailureReason);
        Assert.Single(h.Channels.Channels); // only the second, fresh VM got an agent channel
        Assert.Empty(await h.Get<IHypervisor>().ListAsync(Ct));
        Assert.Empty(await h.Get<IEgressRouter>().ListAsync(Ct));
    }

    [Fact]
    public async Task VmFailsTwice_Failed()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql, new Dictionary<string, string?> { ["Maldex:Fake:Hypervisor:FailStarts"] = "2" });
        var id = await h.SeedPreparingTaskAsync();

        await h.RunAsync(id);

        await using var db = h.NewContext();
        var task = await db.Tasks.SingleAsync(t => t.Id == id, Ct);
        Assert.Equal(RunStatus.Failed, task.Status);
        Assert.Equal("vm failed to start after 2 attempts", task.FailureReason);
        Assert.Equal(2, task.Attempt);
        Assert.Empty(h.Channels.Channels);
        Assert.Empty(await h.Get<IHypervisor>().ListAsync(Ct));
        Assert.Empty(await h.Get<IEgressRouter>().ListAsync(Ct));
    }

    [Fact]
    public async Task AgentNeverReady_Failed_After90s()
    {
        // The agent chatters but never says "ready", and the channel stays open: only the timeout can end it.
        var dir = await WriteReplayAsync(
            """{"delayMs":1,"message":{"type":"file","at":"2000-01-01T00:00:00Z","pid":1,"operation":"write","path":"C:\\x.txt"}}""");
        try
        {
            await using var h = await OrchestratorHarness.CreateAsync(sql, Replay(dir, holdOpen: true));
            var id = await h.SeedPreparingTaskAsync();
            var started = h.Time.GetUtcNow();

            await h.RunAsync(id); // the harness advances fake time until the run finishes

            Assert.True(h.Time.GetUtcNow() - started >= TimeSpan.FromSeconds(180), "two full 90 s ready timeouts must elapse");
            await using var db = h.NewContext();
            var task = await db.Tasks.SingleAsync(t => t.Id == id, Ct);
            Assert.Equal(RunStatus.Failed, task.Status);
            Assert.Equal("agent not ready after 2 attempts", task.FailureReason);
            Assert.Equal(2, task.Attempt);
            Assert.DoesNotContain(await HistoryAsync(db, id), x => x.To == RunStatus.Running);
            Assert.Equal(2, h.Channels.Channels.Count);
            Assert.Empty(await h.Get<IHypervisor>().ListAsync(Ct));
            Assert.Empty(await h.Get<IEgressRouter>().ListAsync(Ct));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task AgentCrash_GoesToCollecting_KeepsEvents()
    {
        // HoldOpen = false: the replay ends without a clean shutdown, exactly like a guest that died.
        await using var h = await OrchestratorHarness.CreateAsync(sql, new Dictionary<string, string?> { ["Maldex:Fake:Replay:HoldOpen"] = "false" });
        var id = await h.SeedPreparingTaskAsync();

        await h.RunAsync(id);

        await using var db = h.NewContext();
        var task = await db.Tasks.SingleAsync(t => t.Id == id, Ct);
        Assert.NotEqual(RunStatus.Failed, task.Status);
        var collecting = Assert.Single(await HistoryAsync(db, id), x => x.To == RunStatus.Collecting);
        Assert.Equal("agent disconnected", collecting.Reason);
        Assert.Equal(RunStatus.Processing, task.Status); // collected data is handed on to processing
        Assert.NotEmpty(await db.BehaviorEvents.Where(e => e.TaskId == id).ToListAsync(Ct));
        Assert.NotEmpty(await db.ProcessNodes.Where(n => n.TaskId == id).ToListAsync(Ct));
    }

    [Fact]
    public async Task TenViolations_EndsRun()
    {
        var garbage = Enumerable.Repeat("this is not a frame", 12).ToArray();
        var dir = await WriteReplayAsync([
            ReadyLine,
            JsonSerializer.Serialize(new { delayMs = 1, message = new { type = "process_started", at = "2000-01-01T00:00:00Z", pid = 10, parentPid = 1, imagePath = "C:\\a.exe", commandLine = "a.exe", user = "u" } }),
            .. garbage,
        ]);
        try
        {
            // HoldOpen = true, so only the violation limit can end the run.
            await using var h = await OrchestratorHarness.CreateAsync(sql, Replay(dir, holdOpen: true));
            var id = await h.SeedPreparingTaskAsync();

            await h.RunAsync(id);

            await using var db = h.NewContext();
            var collecting = Assert.Single(await HistoryAsync(db, id), x => x.To == RunStatus.Collecting);
            Assert.Equal("agent protocol violations", collecting.Reason);
            Assert.Equal(10, Assert.Single(h.Channels.Channels).Violations);
            Assert.Single(await db.ProcessNodes.Where(n => n.TaskId == id).ToListAsync(Ct)); // what came before is kept
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Reconcile_FailsInFlightTasks_DestroysLeftovers_LeavesProcessing()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql);
        var preparing = await h.SeedTaskAsync(RunStatus.Preparing);
        var running = await h.SeedTaskAsync(RunStatus.Running);
        var collecting = await h.SeedTaskAsync(RunStatus.Collecting);
        var processing = await h.SeedTaskAsync(RunStatus.Processing);
        var queued = await h.SeedTaskAsync(RunStatus.Queued);

        // Leftovers of the previous process: a VM and a network lease per in-flight task.
        var hypervisor = h.Get<IHypervisor>();
        var egress = h.Get<IEgressRouter>();
        foreach (var id in new[] { preparing, running, collecting })
        {
            await egress.CreateAsync(id, NetworkMode.Simulated, null, Ct);
            await hypervisor.StartAsync(new VmSpec(id, "win10", 4096, 2, $"fake-net-{id:N}"), Ct);
        }
        Assert.Equal(3, (await hypervisor.ListAsync(Ct)).Count);
        Assert.Equal(3, (await egress.ListAsync(Ct)).Count);

        await h.Get<Reconciler>().ReconcileAsync(Ct);

        Assert.Empty(await hypervisor.ListAsync(Ct));
        Assert.Empty(await egress.ListAsync(Ct));

        await using var db = h.NewContext();
        foreach (var id in new[] { preparing, running, collecting })
        {
            var task = await db.Tasks.SingleAsync(t => t.Id == id, Ct);
            Assert.Equal(RunStatus.Failed, task.Status);
            Assert.Equal("orchestrator restart", task.FailureReason);
            Assert.NotNull(task.EndedAt);
            var last = (await HistoryAsync(db, id)).Last();
            Assert.Equal(RunStatus.Failed, last.To);
            Assert.Equal("orchestrator restart", last.Reason);
        }

        Assert.Equal(RunStatus.Processing, (await db.Tasks.SingleAsync(t => t.Id == processing, Ct)).Status); // the processing worker resumes it
        Assert.Equal(RunStatus.Queued, (await db.Tasks.SingleAsync(t => t.Id == queued, Ct)).Status);
    }
}
