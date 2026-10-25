using System.Data.Common;
using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Maldex.Data;
using Maldex.Data.Templates;
using Maldex.Integration.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Maldex.Integration.Tests.Orchestrator;

[Collection("sql")]
public class TemplateBindingTests(SqlServerFixture sql)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Wraps the fake hypervisor and records every spec it is asked to start.</summary>
    private sealed class SpyHypervisor(IHypervisor inner) : IHypervisor
    {
        public Task SendKeyAsync(VmHandle vm, string key, CancellationToken ct) => throw new NotSupportedException();
        public List<VmSpec> Specs { get; } = [];
        public Task<VmHandle> StartAsync(VmSpec spec, CancellationToken ct)
        {
            lock (Specs) Specs.Add(spec);
            return inner.StartAsync(spec, ct);
        }
        public Task DestroyAsync(VmHandle vm, CancellationToken ct) => inner.DestroyAsync(vm, ct);
        public Task<IReadOnlyList<VmHandle>> ListAsync(CancellationToken ct) => inner.ListAsync(ct);
        public Task<bool> IsRunningAsync(VmHandle vm, CancellationToken ct) => inner.IsRunningAsync(vm, ct);
    }

    private static Task<OrchestratorHarness> HarnessAsync(SqlServerFixture sql, Action<SpyHypervisor> spy) =>
        OrchestratorHarness.CreateAsync(sql, null, s => s.Replace(ServiceDescriptor.Singleton<IHypervisor>(sp =>
        {
            var h = new SpyHypervisor(ActivatorUtilities.CreateInstance<Maldex.Infrastructure.Fakes.FakeHypervisor>(sp));
            spy(h);
            return h;
        })));

    private static TemplateCatalog Catalog(OrchestratorHarness h, MaldexDbContext db, int keepPrevious = 1) =>
        new(db, h.Get<ITemplateStore>(), Options.Create(new TemplateOptions { KeepPrevious = keepPrevious }), h.Time);

    private static async Task PublishAsync(OrchestratorHarness h, TemplateVersion version, int keepPrevious = 1)
    {
        await using var db = h.NewContext();
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync(Ct);
        await Catalog(h, db, keepPrevious).PublishAsync(version.TemplateId, version.Id, Ct);
    }

    private static async Task<TemplateVersion> V1Async(OrchestratorHarness h)
    {
        await using var db = h.NewContext();
        return await db.TemplateVersions.SingleAsync(Ct);
    }

    [Fact]
    public async Task Run_BindsCurrentVersion_AndStartsItsDisk()
    {
        SpyHypervisor? spy = null;
        await using var h = await HarnessAsync(sql, s => spy = s);
        var v1 = await V1Async(h);
        var id = await h.SeedPreparingTaskAsync();

        await h.RunAsync(id);

        await using var db = h.NewContext();
        Assert.Equal(v1.Id, (await db.Tasks.SingleAsync(t => t.Id == id, Ct)).TemplateVersionId);
        Assert.NotEmpty(spy!.Specs);
        Assert.All(spy.Specs, s => Assert.Equal(v1.FileStem, s.DiskStem));
    }

    [Fact]
    public async Task Run_PublishWhileQueued_UsesNewVersion()
    {
        SpyHypervisor? spy = null;
        await using var h = await HarnessAsync(sql, s => spy = s);
        var id = await h.SeedPreparingTaskAsync();
        var v2 = h.NewVersion("win10", 2);
        await PublishAsync(h, v2);

        await h.RunAsync(id);

        await using var db = h.NewContext();
        Assert.Equal(v2.Id, (await db.Tasks.SingleAsync(t => t.Id == id, Ct)).TemplateVersionId);
        Assert.NotEmpty(spy!.Specs);
        Assert.All(spy.Specs, s => Assert.Equal(v2.FileStem, s.DiskStem));
    }

    [Fact]
    public async Task Run_AlreadyBound_KeepsBinding()
    {
        SpyHypervisor? spy = null;
        await using var h = await HarnessAsync(sql, s => spy = s);
        var v1 = await V1Async(h);
        var id = await h.SeedPreparingTaskAsync();
        await using (var db0 = h.NewContext())
        {
            (await db0.Tasks.SingleAsync(t => t.Id == id, Ct)).TemplateVersionId = v1.Id;
            await db0.SaveChangesAsync(Ct);
        }
        await PublishAsync(h, h.NewVersion("win10", 2));

        await h.RunAsync(id);

        await using var db = h.NewContext();
        Assert.Equal(v1.Id, (await db.Tasks.SingleAsync(t => t.Id == id, Ct)).TemplateVersionId);
        Assert.NotEmpty(spy!.Specs);
        Assert.All(spy.Specs, s => Assert.Equal(v1.FileStem, s.DiskStem));
    }

    [Fact]
    public async Task Run_DisabledTemplate_FailsWithoutVm()
    {
        SpyHypervisor? spy = null;
        await using var h = await HarnessAsync(sql, s => spy = s);
        var id = await h.SeedPreparingTaskAsync();
        await using (var db0 = h.NewContext())
        {
            (await db0.Templates.SingleAsync(Ct)).Enabled = false;
            await db0.SaveChangesAsync(Ct);
        }

        await h.RunAsync(id);

        await using var db = h.NewContext();
        var task = await db.Tasks.SingleAsync(t => t.Id == id, Ct);
        Assert.Equal(RunStatus.Failed, task.Status);
        Assert.Equal("template 'win10' is not available", task.FailureReason);
        Assert.Null(task.TemplateVersionId);
        Assert.Equal(0, task.Attempt);
        Assert.Empty(spy!.Specs);
        Assert.Empty(await h.Get<IEgressRouter>().ListAsync(Ct));
    }

    /// <summary>Real mode on a database a fake-mode run imported into: the version's row is there, its disk is not.</summary>
    private sealed class MissingStemsStore : ITemplateStore
    {
        public Task CreateBlankAsync(string stem, long diskBytes, string varsTemplatePath, CancellationToken ct) => throw new NotSupportedException();
        public HashSet<string> Missing { get; } = [];
        public bool Exists(string fileStem) => !Missing.Contains(fileStem);
        public long? SizeOf(string fileStem) => null;
        public Task DeleteAsync(string fileStem, CancellationToken ct) => Task.CompletedTask;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_DiskMissing_FailsAtOnceWithoutVm(bool alreadyBound)
    {
        SpyHypervisor? spy = null;
        var store = new MissingStemsStore();
        await using var h = await OrchestratorHarness.CreateAsync(sql, null, s =>
        {
            s.Replace(ServiceDescriptor.Singleton<ITemplateStore>(store));
            s.Replace(ServiceDescriptor.Singleton<IHypervisor>(sp =>
                spy = new SpyHypervisor(ActivatorUtilities.CreateInstance<Maldex.Infrastructure.Fakes.FakeHypervisor>(sp))));
        });
        var v1 = await V1Async(h);
        store.Missing.Add(v1.FileStem);
        var id = await h.SeedPreparingTaskAsync();
        if (alreadyBound)
        {
            await using var db0 = h.NewContext();
            (await db0.Tasks.SingleAsync(t => t.Id == id, Ct)).TemplateVersionId = v1.Id;
            await db0.SaveChangesAsync(Ct);
        }

        await h.RunAsync(id);

        await using var db = h.NewContext();
        var task = await db.Tasks.SingleAsync(t => t.Id == id, Ct);
        Assert.Equal(RunStatus.Failed, task.Status);
        Assert.Equal("template 'win10' is not available: its disk is missing", task.FailureReason);
        Assert.Equal(alreadyBound ? v1.Id : null, task.TemplateVersionId);
        Assert.Equal(0, task.Attempt);
        Assert.True(spy is null || spy.Specs.Count == 0, "no VM may be started without the template's disk");
        Assert.Empty(await h.Get<IEgressRouter>().ListAsync(Ct));
    }

    /// <summary>Like <c>LibvirtTemplateStore</c>: a stem that is not a valid slug throws instead of answering.</summary>
    private sealed class InvalidStemStore : ITemplateStore
    {
        public Task CreateBlankAsync(string stem, long diskBytes, string varsTemplatePath, CancellationToken ct) => throw new NotSupportedException();
        public HashSet<string> Invalid { get; } = [];
        public bool Exists(string fileStem) => Invalid.Contains(fileStem) ? throw new ArgumentException("Invalid template file stem.", nameof(fileStem)) : true;
        public long? SizeOf(string fileStem) => null;
        public Task DeleteAsync(string fileStem, CancellationToken ct) => Task.CompletedTask;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_InvalidStem_FailsAsDiskMissing_WithoutVm(bool alreadyBound)
    {
        SpyHypervisor? spy = null;
        var store = new InvalidStemStore();
        await using var h = await OrchestratorHarness.CreateAsync(sql, null, s =>
        {
            s.Replace(ServiceDescriptor.Singleton<ITemplateStore>(store));
            s.Replace(ServiceDescriptor.Singleton<IHypervisor>(sp =>
                spy = new SpyHypervisor(ActivatorUtilities.CreateInstance<Maldex.Infrastructure.Fakes.FakeHypervisor>(sp))));
        });
        var v1 = await V1Async(h);
        store.Invalid.Add(v1.FileStem);
        var id = await h.SeedPreparingTaskAsync();
        if (alreadyBound)
        {
            await using var db0 = h.NewContext();
            (await db0.Tasks.SingleAsync(t => t.Id == id, Ct)).TemplateVersionId = v1.Id;
            await db0.SaveChangesAsync(Ct);
        }

        await h.RunAsync(id);

        await using var db = h.NewContext();
        var task = await db.Tasks.SingleAsync(t => t.Id == id, Ct);
        Assert.Equal(RunStatus.Failed, task.Status);
        Assert.Equal("template 'win10' is not available: its disk is missing", task.FailureReason);
        Assert.True(spy is null || spy.Specs.Count == 0);
    }

    /// <summary>Runs <paramref name="onLock"/> once, just before the bind's UPDLOCK re-read executes (after it has resolved the version).</summary>
    private sealed class BeforeLockInterceptor(Func<Task> onLock) : DbCommandInterceptor
    {
        private bool _done;
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!_done && command.CommandText.Contains("UPDLOCK", StringComparison.Ordinal))
            {
                _done = true;
                await onLock();
            }
            return result;
        }
    }

    private static MaldexDbContext ContextWith(OrchestratorHarness h, BeforeLockInterceptor interceptor)
    {
        using var plain = h.NewContext();
        return new MaldexDbContext(new DbContextOptionsBuilder<MaldexDbContext>()
            .UseSqlServer(plain.Database.GetConnectionString()).AddInterceptors(interceptor).Options);
    }

    [Fact]
    public async Task Bind_VersionRetiredBetweenResolveAndLock_BindsTheNewCurrentVersion()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql);
        var v1 = await V1Async(h);
        var taskId = await h.SeedPreparingTaskAsync();
        var v2 = h.NewVersion("win10", 2);
        // Publishing v2 with KeepPrevious = 0 retires v1 after bind has resolved it but before it takes the lock.
        var interceptor = new BeforeLockInterceptor(() => PublishAsync(h, v2, keepPrevious: 0));
        await using var db = ContextWith(h, interceptor);
        var task = await db.Tasks.SingleAsync(t => t.Id == taskId, Ct);

        var bound = await Catalog(h, db).BindTaskVersionAsync(task, Ct);

        Assert.Equal(v2.Id, bound.Id);
        await using var check = h.NewContext();
        Assert.Equal(TemplateVersionStatus.Retired, (await check.TemplateVersions.SingleAsync(v => v.Id == v1.Id, Ct)).Status);
        Assert.Equal(v2.Id, (await check.Tasks.SingleAsync(t => t.Id == taskId, Ct)).TemplateVersionId);
    }

    /// <summary>The API's stop request: it writes the task row (and so its row version) while the run is binding.</summary>
    private static async Task RequestStopAsync(OrchestratorHarness h, Guid taskId)
    {
        await using var api = h.NewContext();
        (await api.Tasks.SingleAsync(t => t.Id == taskId, Ct)).StopRequestedAt = h.Time.GetUtcNow();
        await api.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Bind_ConcurrentStop_BindsWithoutConflict_AndKeepsTheStop()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql);
        var v1 = await V1Async(h);
        var taskId = await h.SeedPreparingTaskAsync();
        var interceptor = new BeforeLockInterceptor(() => RequestStopAsync(h, taskId));
        await using var db = ContextWith(h, interceptor);
        var task = await db.Tasks.SingleAsync(t => t.Id == taskId, Ct);

        var bound = await Catalog(h, db).BindTaskVersionAsync(task, Ct);

        Assert.Equal(v1.Id, bound.Id);
        Assert.Equal(v1.Id, task.TemplateVersionId);
        Assert.False(db.Entry(task).Property(t => t.TemplateVersionId).IsModified); // already saved; a later save must not resend it
        await using var check = h.NewContext();
        var row = await check.Tasks.SingleAsync(t => t.Id == taskId, Ct);
        Assert.Equal(v1.Id, row.TemplateVersionId);
        Assert.NotNull(row.StopRequestedAt);
    }

    [Fact]
    public async Task Run_StopDuringBind_EndsCancelled_NotFailed()
    {
        var dir = Directory.CreateTempSubdirectory("maldex-replay-").FullName;
        await File.WriteAllLinesAsync(Path.Combine(dir, "slow.jsonl"),
            ["""{"delayMs":600000,"message":{"type":"ready","agentVersion":"1","hostname":"h"}}"""], Ct);
        try
        {
            OrchestratorHarness? harness = null;
            Guid taskId = default;
            var interceptor = new BeforeLockInterceptor(() => RequestStopAsync(harness!, taskId));
            await using var h = await OrchestratorHarness.CreateAsync(sql, new Dictionary<string, string?>
            {
                ["Maldex:Fake:Replay:Directory"] = dir,
                ["Maldex:Fake:Replay:DefaultFile"] = "slow.jsonl",
                ["Maldex:Fake:Replay:Speed"] = "1",
                ["Maldex:Fake:Replay:HoldOpen"] = "true",
                ["Maldex:Orchestrator:AgentReadyTimeout"] = "01:00:00",
            }, s => s.ConfigureDbContext<MaldexDbContext>(o => o.AddInterceptors(interceptor)));
            harness = h;
            var v1 = await V1Async(h);
            taskId = await h.SeedPreparingTaskAsync();

            await h.RunAsync(taskId);

            await using var db = h.NewContext();
            var task = await db.Tasks.SingleAsync(t => t.Id == taskId, Ct);
            Assert.Equal(RunStatus.Cancelled, task.Status);
            Assert.Null(task.FailureReason);
            Assert.Equal(v1.Id, task.TemplateVersionId);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Bind_VersionRetiredAndNothingReplacesIt_RefusesAndLeavesTaskUnbound()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql);
        var v1 = await V1Async(h);
        var taskId = await h.SeedPreparingTaskAsync();
        var interceptor = new BeforeLockInterceptor(async () =>
        {
            await using var other = h.NewContext();
            (await other.TemplateVersions.SingleAsync(v => v.Id == v1.Id, Ct)).Status = TemplateVersionStatus.Retired;
            await other.SaveChangesAsync(Ct);
        });
        await using var db = ContextWith(h, interceptor);
        var task = await db.Tasks.SingleAsync(t => t.Id == taskId, Ct);

        var ex = await Assert.ThrowsAsync<TemplateUnavailableException>(() => Catalog(h, db).BindTaskVersionAsync(task, Ct));

        Assert.Equal("template 'win10' is not available", ex.Message);
        await using var check = h.NewContext();
        Assert.Null((await check.Tasks.SingleAsync(t => t.Id == taskId, Ct)).TemplateVersionId);
    }
}
