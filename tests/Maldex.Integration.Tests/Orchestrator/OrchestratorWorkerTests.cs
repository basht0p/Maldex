using Maldex.Core.Domain;
using Maldex.Data;
using Maldex.Integration.Tests.Fixtures;
using Maldex.Orchestrator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using OrgEntity = Maldex.Core.Domain.Org;

namespace Maldex.Integration.Tests.Orchestrator;

internal static class OrchestratorHarnessSeeding
{
    /// <summary>
    /// Seeds a URL task and walks it to <paramref name="status"/> through the real state machine
    /// (<c>Queued → Preparing → Running → Collecting → Processing</c>).
    /// </summary>
    public static async Task<Guid> SeedTaskAsync(this OrchestratorHarness h, RunStatus status, bool interactive = false, DateTimeOffset? createdAt = null)
    {
        await using var db = h.NewContext();
        var org = new OrgEntity { Id = Guid.NewGuid(), Name = "Org-" + Guid.NewGuid().ToString("N"), RetentionDays = 30 };
        var user = new AppUser { Id = Guid.NewGuid(), EntraObjectId = Guid.NewGuid().ToString("N"), DisplayName = "A", Role = UserRole.Analyst };
        var at = createdAt ?? h.Time.GetUtcNow();
        var task = new AnalysisTask
        {
            Id = Guid.NewGuid(), OrgId = org.Id, SubmittedByUserId = user.Id, Url = "http://example.test/", Image = "win10",
            NetworkMode = NetworkMode.Simulated, DurationSec = 300, Interactive = interactive, CreatedAt = at,
        };
        RunStatus[] path = [RunStatus.Preparing, RunStatus.Running, RunStatus.Collecting, RunStatus.Processing];
        foreach (var step in path)
        {
            if (status == RunStatus.Queued) break;
            task.TransitionTo(step, "seed", at);
            if (step == status) break;
        }
        db.AddRange(org, user, task);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return task.Id;
    }

    public static async Task<RunStatus> StatusOfAsync(this OrchestratorHarness h, Guid id)
    {
        await using var db = h.NewContext();
        return await db.Tasks.AsNoTracking().Where(t => t.Id == id).Select(t => t.Status).SingleAsync(TestContext.Current.CancellationToken);
    }
}

[Collection("sql")]
public class OrchestratorWorkerTests(SqlServerFixture sql)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Replays hold the channel open and fake time never advances, so a claimed run waits for its agent in <c>Preparing</c>.</summary>
    private static Dictionary<string, string?> Settings(string? minFreeDiskMiB = "1") => new()
    {
        ["Maldex:Fake:Replay:HoldOpen"] = "true",
        ["Maldex:Orchestrator:MinFreeDiskMiB"] = minFreeDiskMiB,
    };

    [Fact]
    public async Task Worker_RespectsMaxConcurrentVms()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql, Settings());
        var worker = h.Get<OrchestratorWorker>();
        var t0 = h.Time.GetUtcNow();
        var ids = new[]
        {
            await h.SeedTaskAsync(RunStatus.Queued, createdAt: t0),
            await h.SeedTaskAsync(RunStatus.Queued, createdAt: t0.AddSeconds(1)),
            await h.SeedTaskAsync(RunStatus.Queued, createdAt: t0.AddSeconds(2)),
        };
        try
        {
            var first = await worker.ClaimAndStartAsync(Ct);
            Assert.Equal([ids[0]], first);

            // Further ticks must not claim anything while the one slot is busy.
            Assert.Empty(await worker.ClaimAndStartAsync(Ct));
            Assert.Empty(await worker.ClaimAndStartAsync(Ct));

            var statuses = new List<RunStatus>();
            foreach (var id in ids) statuses.Add(await h.StatusOfAsync(id));
            Assert.Equal([RunStatus.Preparing, RunStatus.Queued, RunStatus.Queued], statuses);
        }
        finally
        {
            await worker.StopRunsAsync();
        }
    }

    [Theory]
    [InlineData(TemplateJobState.Preparing, false, false)] // still copying the disk, no slot taken yet
    [InlineData(TemplateJobState.Preparing, true, true)]   // slot taken, about to boot
    [InlineData(TemplateJobState.Configuring, true, true)]
    [InlineData(TemplateJobState.Sealing, true, true)]
    [InlineData(TemplateJobState.Verifying, true, true)]
    [InlineData(TemplateJobState.Failed, true, false)]     // terminal: the slot is free again
    public async Task Worker_CountsJobVmsAgainstMaxConcurrentVms(TemplateJobState jobState, bool slotHeld, bool blocks)
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql, Settings());
        var worker = h.Get<OrchestratorWorker>();
        var id = await h.SeedTaskAsync(RunStatus.Queued);
        await using (var db = h.NewContext())
        {
            var template = await db.Templates.SingleAsync(t => t.Id == "win10", Ct);
            var user = new AppUser { Id = Guid.NewGuid(), EntraObjectId = Guid.NewGuid().ToString("N"), DisplayName = "J", Role = UserRole.Admin };
            db.Users.Add(user);
            db.TemplateJobs.Add(new TemplateJob
            {
                Id = Guid.NewGuid(), Kind = TemplateJobKind.Edit, TemplateId = template.Id, BaseVersionId = template.CurrentVersionId!.Value,
                Network = NetworkMode.Simulated, State = jobState, RequestedByUserId = user.Id, CreatedAt = h.Time.GetUtcNow(),
                SlotHeldAt = slotHeld ? h.Time.GetUtcNow() : null,
            });
            await db.SaveChangesAsync(Ct);
        }
        try
        {
            var claimed = await worker.ClaimAndStartAsync(Ct);
            if (blocks)
            {
                Assert.Empty(claimed);
                Assert.Equal(RunStatus.Queued, await h.StatusOfAsync(id));
            }
            else
            {
                Assert.Equal([id], claimed);
            }
        }
        finally
        {
            await worker.StopRunsAsync();
        }
    }

    [Fact]
    public async Task Worker_LowDisk_ClaimsNothing()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql, Settings(minFreeDiskMiB: long.MaxValue.ToString()));
        var worker = h.Get<OrchestratorWorker>();
        var id = await h.SeedTaskAsync(RunStatus.Queued);
        try
        {
            Assert.Empty(await worker.ClaimAndStartAsync(Ct));
            Assert.Equal(RunStatus.Queued, await h.StatusOfAsync(id));
        }
        finally
        {
            await worker.StopRunsAsync();
        }
    }

    [Fact]
    public async Task Worker_InteractiveClaimedFirst()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql, Settings());
        var worker = h.Get<OrchestratorWorker>();
        var t0 = h.Time.GetUtcNow();
        var automatedOlder = await h.SeedTaskAsync(RunStatus.Queued, interactive: false, createdAt: t0);
        var interactiveNewer = await h.SeedTaskAsync(RunStatus.Queued, interactive: true, createdAt: t0.AddMinutes(5));
        try
        {
            Assert.Equal([interactiveNewer], await worker.ClaimAndStartAsync(Ct));
            Assert.Equal(RunStatus.Preparing, await h.StatusOfAsync(interactiveNewer));
            Assert.Equal(RunStatus.Queued, await h.StatusOfAsync(automatedOlder));
        }
        finally
        {
            await worker.StopRunsAsync();
        }
    }

    [Fact]
    public async Task Worker_ConcurrentClaimers_ClaimEachTaskOnce()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql, Settings());
        var worker = h.Get<OrchestratorWorker>();
        var id = await h.SeedTaskAsync(RunStatus.Queued);
        try
        {
            var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => worker.ClaimAndStartAsync(Ct), Ct)));

            Assert.Equal([id], results.SelectMany(r => r).ToList()); // a lost race is skipped, never an error
            await using var db = h.NewContext();
            var history = await db.TaskStatusHistory.Where(x => x.TaskId == id).ToListAsync(Ct);
            Assert.Single(history, x => x.To == RunStatus.Preparing);
        }
        finally
        {
            await worker.StopRunsAsync();
        }
    }

    [Fact]
    public async Task Worker_Start_ReconcilesThenClaims()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql, Settings());
        var worker = h.Get<OrchestratorWorker>();
        var orphan = await h.SeedTaskAsync(RunStatus.Running); // would hold the only slot if it were not reconciled
        var queued = await h.SeedTaskAsync(RunStatus.Queued);

        await worker.StartAsync(Ct);
        try
        {
            await h.WaitForStatusAsync(orphan, RunStatus.Failed);
            await h.WaitForStatusAsync(queued, RunStatus.Preparing);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>Lists fail a few times (libvirt/DB not ready at boot), then work.</summary>
    private sealed class FlakyHypervisor(Maldex.Core.Abstractions.IHypervisor inner, int failures) : Maldex.Core.Abstractions.IHypervisor
    {
        public Task SendKeyAsync(Maldex.Core.Abstractions.VmHandle vm, string key, CancellationToken ct) => throw new NotSupportedException();
        public int ListCalls;
        public Task<Maldex.Core.Abstractions.VmHandle> StartAsync(Maldex.Core.Abstractions.VmSpec spec, CancellationToken ct) => inner.StartAsync(spec, ct);
        public Task DestroyAsync(Maldex.Core.Abstractions.VmHandle vm, CancellationToken ct) => inner.DestroyAsync(vm, ct);
        public Task<bool> IsRunningAsync(Maldex.Core.Abstractions.VmHandle vm, CancellationToken ct) => inner.IsRunningAsync(vm, ct);
        public Task<IReadOnlyList<Maldex.Core.Abstractions.VmHandle>> ListAsync(CancellationToken ct) =>
            Interlocked.Increment(ref ListCalls) <= failures ? throw new IOException("virsh: failed to connect to the hypervisor") : inner.ListAsync(ct);
    }

    [Fact]
    public async Task Worker_Start_RetriesReconcile_ThenClaims()
    {
        FlakyHypervisor? flaky = null;
        await using var h = await OrchestratorHarness.CreateAsync(sql, Settings(), s => s.Replace(ServiceDescriptor.Singleton<Maldex.Core.Abstractions.IHypervisor>(sp =>
            flaky = new FlakyHypervisor(ActivatorUtilities.CreateInstance<Maldex.Infrastructure.Fakes.FakeHypervisor>(sp), failures: 2))));
        var worker = h.Get<OrchestratorWorker>();
        var orphan = await h.SeedTaskAsync(RunStatus.Running);
        var queued = await h.SeedTaskAsync(RunStatus.Queued);

        await worker.StartAsync(Ct);
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (await h.StatusOfAsync(queued) != RunStatus.Preparing)
            {
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), "reconcile was not retried");
                Assert.False(worker.ExecuteTask!.IsFaulted, worker.ExecuteTask.Exception?.ToString());
                h.Time.Advance(TimeSpan.FromSeconds(1));
                await Task.Delay(5, Ct);
            }
            Assert.Equal(RunStatus.Failed, await h.StatusOfAsync(orphan));
            Assert.True(flaky!.ListCalls >= 3);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Worker_Start_ReconcileKeepsFailing_FaultsAfterBoundedAttempts()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql, Settings(), s => s.Replace(ServiceDescriptor.Singleton<Maldex.Core.Abstractions.IHypervisor>(sp =>
            new FlakyHypervisor(ActivatorUtilities.CreateInstance<Maldex.Infrastructure.Fakes.FakeHypervisor>(sp), failures: int.MaxValue))));
        var worker = h.Get<OrchestratorWorker>();
        var queued = await h.SeedTaskAsync(RunStatus.Queued);

        await worker.StartAsync(Ct);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!worker.ExecuteTask!.IsCompleted)
        {
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), "reconcile retries are not bounded");
            h.Time.Advance(TimeSpan.FromSeconds(5));
            await Task.Delay(5, Ct);
        }
        Assert.True(worker.ExecuteTask.IsFaulted); // surfaced to the host (GatedHostedService stops the app)
        Assert.Equal(RunStatus.Queued, await h.StatusOfAsync(queued)); // never claimed without a reconcile
        await worker.StopAsync(CancellationToken.None);
    }
}
