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
        public List<VmSpec> Specs { get; } = [];
        public Task<VmHandle> StartAsync(VmSpec spec, CancellationToken ct)
        {
            lock (Specs) Specs.Add(spec);
            return inner.StartAsync(spec, ct);
        }
        public Task DestroyAsync(VmHandle vm, CancellationToken ct) => inner.DestroyAsync(vm, ct);
        public Task<IReadOnlyList<VmHandle>> ListAsync(CancellationToken ct) => inner.ListAsync(ct);
    }

    private static Task<OrchestratorHarness> HarnessAsync(SqlServerFixture sql, Action<SpyHypervisor> spy) =>
        OrchestratorHarness.CreateAsync(sql, null, s => s.Replace(ServiceDescriptor.Singleton<IHypervisor>(sp =>
        {
            var h = new SpyHypervisor(ActivatorUtilities.CreateInstance<Maldex.Infrastructure.Fakes.FakeHypervisor>(sp));
            spy(h);
            return h;
        })));

    private static TemplateCatalog Catalog(MaldexDbContext db, TimeProvider time, int keepPrevious = 1) =>
        new(db, Options.Create(new TemplateOptions { KeepPrevious = keepPrevious }), time);

    private static async Task PublishAsync(OrchestratorHarness h, TemplateVersion version, int keepPrevious = 1)
    {
        await using var db = h.NewContext();
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync(Ct);
        await Catalog(db, h.Time, keepPrevious).PublishAsync(version.TemplateId, version.Id, Ct);
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

        var bound = await Catalog(db, h.Time).BindAsync(task, Ct);

        Assert.Equal(v2.Id, bound.Id);
        await using var check = h.NewContext();
        Assert.Equal(TemplateVersionStatus.Retired, (await check.TemplateVersions.SingleAsync(v => v.Id == v1.Id, Ct)).Status);
        Assert.Equal(v2.Id, (await check.Tasks.SingleAsync(t => t.Id == taskId, Ct)).TemplateVersionId);
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

        var ex = await Assert.ThrowsAsync<TemplateUnavailableException>(() => Catalog(db, h.Time).BindAsync(task, Ct));

        Assert.Equal("template 'win10' is not available", ex.Message);
        await using var check = h.NewContext();
        Assert.Null((await check.Tasks.SingleAsync(t => t.Id == taskId, Ct)).TemplateVersionId);
    }
}
