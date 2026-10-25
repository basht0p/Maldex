using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Maldex.Data;
using Maldex.Infrastructure.Fakes;
using Maldex.Infrastructure.Guacamole;
using Maldex.Integration.Tests.Fixtures;
using Maldex.Orchestrator.Templates;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Maldex.Integration.Tests.Web;

/// <summary>The admin endpoints for Edit/Refresh jobs, held versions and the live job hub (workers off: nothing here runs a VM).</summary>
[Collection("sql")]
public sealed class TemplateJobEndpointsTests(SqlServerFixture sql) : IDisposable
{
    private const string Seeded = "win10-ltsc-2021"; // imported at startup: default, Ready v1

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly List<IDisposable> _disposables = [];
    private readonly List<HubConnection> _connections = [];

    public void Dispose()
    {
        foreach (var c in _connections) c.DisposeAsync().AsTask().GetAwaiter().GetResult();
        foreach (var d in _disposables) d.Dispose();
    }

    private sealed class SettableDisk : IDiskSpaceProbe
    {
        public long Mib { get; set; } = 500 * 1024;
        public long FreeMiB(string path) => Mib;
    }

    private sealed class Rig
    {
        public required MaldexAppFactory Factory { get; init; }
        public required SettableDisk Disk { get; init; }
        public FakeTemplateStore Store => Factory.Services.GetRequiredService<FakeTemplateStore>();
        public System.Collections.Concurrent.ConcurrentDictionary<string, long> Sizes =>
            ((Maldex.Integration.Tests.Orchestrator.SizedTemplateStore)Factory.Services.GetRequiredService<ITemplateStore>()).Sizes;
    }

    private Rig NewRig()
    {
        var disk = new SettableDisk();
        var f = new JobFactory(sql, disk);
        _disposables.Add(f);
        return new Rig { Factory = f, Disk = disk };
    }

    private sealed class JobFactory(SqlServerFixture sql, SettableDisk disk) : MaldexAppFactory(sql)
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(s =>
            {
                s.Replace(ServiceDescriptor.Singleton<IDiskSpaceProbe>(disk));
                // The store is read through its interface everywhere; expose the singleton so a test can make a disk go missing.
                s.AddSingleton<FakeTemplateStore>();
                s.Replace(ServiceDescriptor.Singleton<ITemplateStore>(sp => new Maldex.Integration.Tests.Orchestrator.SizedTemplateStore(sp.GetRequiredService<FakeTemplateStore>())));
            });
        }
    }

    private static HttpClient Admin(Rig r, string user = "tester") => r.Factory.CreateClient(UserRole.Admin, user);

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage resp) => await resp.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static async Task<T> InDbAsync<T>(Rig r, Func<MaldexDbContext, Task<T>> read)
    {
        await using var scope = r.Factory.Services.CreateAsyncScope();
        return await read(scope.ServiceProvider.GetRequiredService<MaldexDbContext>());
    }

    private static Task<List<AuditLog>> AuditsAsync(Rig r, string action) =>
        InDbAsync(r, db => db.AuditLogs.AsNoTracking().Where(a => a.Action == action).OrderBy(a => a.Id).ToListAsync(Ct));

    /// <summary>Seeds a job on the seeded template in <paramref name="state"/> (not Queued by default: the endpoints never start VMs).</summary>
    private static async Task<Guid> SeedJobAsync(Rig r, TemplateJobState state, TemplateJobKind kind = TemplateJobKind.Edit,
        NetworkMode network = NetworkMode.None, DateTimeOffset? createdAt = null)
    {
        using var client = Admin(r);
        await client.GetAsync("/api/me", Ct); // creates the dev user row
        await using var scope = r.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        var v1 = await db.TemplateVersions.AsNoTracking().SingleAsync(v => v.TemplateId == Seeded && v.Number == 1, Ct);
        var user = await db.Users.AsNoTracking().FirstAsync(Ct);
        var now = createdAt ?? DateTimeOffset.UtcNow;
        var terminal = Maldex.Core.Lifecycle.TemplateJobStateMachine.IsTerminal(state);
        var job = new TemplateJob
        {
            Id = Guid.NewGuid(), Kind = kind, TemplateId = Seeded, BaseVersionId = v1.Id, Network = network, State = state,
            RequestedByUserId = user.Id, CreatedAt = now, EndedAt = terminal ? now : null,
            ConfiguringSince = state == TemplateJobState.Configuring ? now : null,
        };
        db.TemplateJobs.Add(job);
        await db.SaveChangesAsync(Ct);
        return job.Id;
    }

    /// <summary>Adds a Ready version that was never current (a held version) to the seeded template.</summary>
    private static async Task<Guid> SeedHeldVersionAsync(Rig r, TemplateVersionStatus status = TemplateVersionStatus.Ready, int number = 2)
    {
        await using var scope = r.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        var v = new TemplateVersion
        {
            Id = Guid.NewGuid(), TemplateId = Seeded, Number = number, Origin = TemplateVersionOrigin.Edit, FileStem = $"held-{number}",
            SealedAt = DateTimeOffset.UtcNow, Status = status,
        };
        db.TemplateVersions.Add(v);
        await db.SaveChangesAsync(Ct);
        return v.Id;
    }

    // ---- start ----

    [Fact]
    public async Task Edit_Returns202WithTheJob_StoresIt_AndAudits()
    {
        var r = NewRig();
        using var client = Admin(r, "editor");

        var resp = await client.PostAsJsonAsync($"/api/admin/templates/{Seeded}/edit", new { network = "Direct", publishOnSuccess = false }, Ct);

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var dto = await ReadAsync(resp);
        Assert.Equal("Edit", dto.GetProperty("kind").GetString());
        Assert.Equal(Seeded, dto.GetProperty("templateId").GetString());
        Assert.Equal(1, dto.GetProperty("baseVersionNumber").GetInt32());
        Assert.Equal("Direct", dto.GetProperty("network").GetString());
        Assert.Equal("Queued", dto.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, dto.GetProperty("idleDeadline").ValueKind);
        var id = dto.GetProperty("id").GetGuid();

        var job = await InDbAsync(r, db => db.TemplateJobs.AsNoTracking().SingleAsync(j => j.Id == id, Ct));
        Assert.Equal((TemplateJobKind.Edit, NetworkMode.Direct, false, TemplateJobState.Queued), (job.Kind, job.Network, job.PublishOnSuccess, job.State));
        var audit = Assert.Single(await AuditsAsync(r, "template.job.start"));
        Assert.Equal(Seeded, audit.Target);
        Assert.NotNull(audit.UserId);
        Assert.Contains(id.ToString(), audit.Details);
        Assert.Contains("Edit", audit.Details);
        Assert.Contains("Direct", audit.Details);
    }

    [Fact]
    public async Task Edit_DefaultsToNetworkNone_AndPublishing()
    {
        var r = NewRig();
        using var client = Admin(r);

        var resp = await client.PostAsJsonAsync($"/api/admin/templates/{Seeded}/edit", new { }, Ct);

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var id = (await ReadAsync(resp)).GetProperty("id").GetGuid();
        var job = await InDbAsync(r, db => db.TemplateJobs.AsNoTracking().SingleAsync(j => j.Id == id, Ct));
        Assert.Equal((NetworkMode.None, true), (job.Network, job.PublishOnSuccess));
    }

    [Theory]
    [InlineData("Simulated")]
    [InlineData("LiveTor")]
    [InlineData("bogus")]
    public async Task Edit_RejectsOtherNetworks_With400(string network)
    {
        var r = NewRig();
        using var client = Admin(r);

        var resp = await client.PostAsJsonAsync($"/api/admin/templates/{Seeded}/edit", new { network }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(0, await InDbAsync(r, db => db.TemplateJobs.CountAsync(Ct)));
    }

    [Fact]
    public async Task Refresh_Returns202_RunsWithoutNetwork_AndAudits()
    {
        var r = NewRig();
        using var client = Admin(r);

        var resp = await client.PostAsJsonAsync($"/api/admin/templates/{Seeded}/refresh", new { publishOnSuccess = false }, Ct);

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var dto = await ReadAsync(resp);
        Assert.Equal("Refresh", dto.GetProperty("kind").GetString());
        Assert.Equal("None", dto.GetProperty("network").GetString());
        var id = dto.GetProperty("id").GetGuid();
        var job = await InDbAsync(r, db => db.TemplateJobs.AsNoTracking().SingleAsync(j => j.Id == id, Ct));
        Assert.Equal((TemplateJobKind.Refresh, NetworkMode.None, false), (job.Kind, job.Network, job.PublishOnSuccess));
        var audit = Assert.Single(await AuditsAsync(r, "template.job.start"));
        Assert.Contains("Refresh", audit.Details);
    }

    [Fact]
    public async Task Refresh_IgnoresANetworkInTheBody()
    {
        var r = NewRig();
        using var client = Admin(r);

        var resp = await client.PostAsJsonAsync($"/api/admin/templates/{Seeded}/refresh", new { network = "Direct" }, Ct);

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        Assert.Equal("None", (await ReadAsync(resp)).GetProperty("network").GetString());
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("refresh")]
    public async Task Start_UnknownTemplate_Is404(string route)
    {
        var r = NewRig();
        using var client = Admin(r);

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/admin/templates/nope/{route}", new { }, Ct)).StatusCode);
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("refresh")]
    public async Task Start_WhileAJobIsActive_Is409WithTheReason(string route)
    {
        var r = NewRig();
        await SeedJobAsync(r, TemplateJobState.Configuring);
        using var client = Admin(r);

        var resp = await client.PostAsJsonAsync($"/api/admin/templates/{Seeded}/{route}", new { }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Equal(TemplateCoordinator.AlreadyRunning, (await ReadAsync(resp)).GetProperty("title").GetString());
        Assert.Equal(1, await InDbAsync(r, db => db.TemplateJobs.CountAsync(Ct)));
        Assert.Empty(await AuditsAsync(r, "template.job.start"));
    }

    [Fact]
    public async Task Start_AfterAFinishedJob_IsAccepted()
    {
        var r = NewRig();
        await SeedJobAsync(r, TemplateJobState.Failed);
        using var client = Admin(r);

        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync($"/api/admin/templates/{Seeded}/refresh", new { }, Ct)).StatusCode);
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("refresh")]
    public async Task Start_WithTheBaseDiskMissing_Is409(string route)
    {
        var r = NewRig();
        using var client = Admin(r);
        await client.GetAsync("/api/admin/templates", Ct); // boots the host: the startup import needs the disk present
        r.Store.Missing.Add(await InDbAsync(r, db => db.TemplateVersions.Where(v => v.TemplateId == Seeded).Select(v => v.FileStem).SingleAsync(Ct)));

        var resp = await client.PostAsJsonAsync($"/api/admin/templates/{Seeded}/{route}", new { }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Equal(TemplateCoordinator.BaseDiskMissing, (await ReadAsync(resp)).GetProperty("title").GetString());
        Assert.Equal(0, await InDbAsync(r, db => db.TemplateJobs.CountAsync(Ct)));
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("refresh")]
    public async Task Start_WithTooLittleFreeSpace_Is409(string route)
    {
        var r = NewRig();
        r.Disk.Mib = 10 * 1024; // MinFreeGiB defaults to 30
        using var client = Admin(r);

        var resp = await client.PostAsJsonAsync($"/api/admin/templates/{Seeded}/{route}", new { }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Equal("not enough disk space for a template copy: 10 GiB free, 30 GiB required", (await ReadAsync(resp)).GetProperty("title").GetString());
        Assert.Equal(0, await InDbAsync(r, db => db.TemplateJobs.CountAsync(Ct)));
    }

    [Fact]
    public async Task Start_RequiresTheBaseSizeAndTheCopiesInFlight_OnTopOfTheMargin()
    {
        var r = NewRig();
        const long GiB = 1024L * 1024 * 1024;
        var v1 = await InDbAsync(r, db => db.TemplateVersions.AsNoTracking().SingleAsync(v => v.TemplateId == Seeded && v.Number == 1, Ct));
        r.Sizes[v1.FileStem] = 8 * GiB;
        r.Disk.Mib = 40 * 1024;
        using var client = Admin(r);

        // 30 GiB margin + 8 GiB base fits in 40 GiB ...
        var first = await client.PostAsJsonAsync($"/api/admin/templates/{Seeded}/refresh", new { }, Ct);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var firstId = (await ReadAsync(first)).GetProperty("id").GetGuid();
        // ... but not while that job is still copying the same base: 30 + 8 + 8. (It moves to another template, so that the
        // one-active-job rule does not refuse first.)
        await InDbAsync(r, async db =>
        {
            db.Templates.Add(new Template { Id = "other", DisplayName = "other", CreatedAt = DateTimeOffset.UtcNow });
            return await db.SaveChangesAsync(Ct);
        });
        await InDbAsync(r, db => db.TemplateJobs.Where(j => j.Id == firstId)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.State, TemplateJobState.Preparing).SetProperty(j => j.TemplateId, "other"), Ct));
        var second = await client.PostAsJsonAsync($"/api/admin/templates/{Seeded}/refresh", new { }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("not enough disk space for a template copy: 40 GiB free, 46 GiB required", (await ReadAsync(second)).GetProperty("title").GetString());
    }

    [Fact]
    public async Task Start_TwoRacingRequests_OneWins_TheOtherIs409_Not500()
    {
        var r = NewRig();
        using var a = Admin(r, "a");
        using var b = Admin(r, "b");
        await a.GetAsync("/api/admin/templates", Ct);

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            (i % 2 == 0 ? a : b).PostAsJsonAsync($"/api/admin/templates/{Seeded}/refresh", new { }, Ct)));

        Assert.Equal(1, results.Count(x => x.StatusCode == HttpStatusCode.Accepted));
        Assert.Equal(5, results.Count(x => x.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(1, await InDbAsync(r, db => db.TemplateJobs.CountAsync(Ct)));
        Assert.Single(await AuditsAsync(r, "template.job.start"));
    }

    // ---- read ----

    [Fact]
    public async Task GetJob_ReturnsTheDto_WithTheIdleDeadlineWhileConfiguring()
    {
        var r = NewRig();
        var since = DateTimeOffset.UtcNow.AddMinutes(-10);
        var id = await SeedJobAsync(r, TemplateJobState.Configuring, createdAt: since);
        using var client = Admin(r);

        var dto = await client.GetFromJsonAsync<JsonElement>($"/api/admin/template-jobs/{id}", Ct);

        Assert.Equal("Configuring", dto.GetProperty("state").GetString());
        Assert.Equal(since + TimeSpan.FromHours(2), dto.GetProperty("idleDeadline").GetDateTimeOffset());

        // The later of ConfiguringSince and LastConsoleAt counts.
        var console = since.AddMinutes(5);
        await InDbAsync(r, db => db.TemplateJobs.Where(j => j.Id == id).ExecuteUpdateAsync(s => s.SetProperty(j => j.LastConsoleAt, console), Ct));
        dto = await client.GetFromJsonAsync<JsonElement>($"/api/admin/template-jobs/{id}", Ct);
        Assert.Equal(console + TimeSpan.FromHours(2), dto.GetProperty("idleDeadline").GetDateTimeOffset());
    }

    [Fact]
    public async Task GetJob_Terminal_HasNoIdleDeadline_AndShowsTheResultVersion()
    {
        var r = NewRig();
        var id = await SeedJobAsync(r, TemplateJobState.Published);
        var held = await SeedHeldVersionAsync(r);
        await InDbAsync(r, db => db.TemplateJobs.Where(j => j.Id == id).ExecuteUpdateAsync(s => s.SetProperty(j => j.ResultVersionId, held), Ct));
        using var client = Admin(r);

        var dto = await client.GetFromJsonAsync<JsonElement>($"/api/admin/template-jobs/{id}", Ct);

        Assert.Equal(JsonValueKind.Null, dto.GetProperty("idleDeadline").ValueKind);
        Assert.Equal(2, dto.GetProperty("resultVersionNumber").GetInt32());
        Assert.Equal(1, dto.GetProperty("baseVersionNumber").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, dto.GetProperty("endedAt").ValueKind);
    }

    [Fact]
    public async Task GetJob_Unknown_Is404()
    {
        var r = NewRig();
        using var client = Admin(r);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/admin/template-jobs/{Guid.NewGuid()}", Ct)).StatusCode);
    }

    [Fact]
    public async Task ListJobs_IsNewestFirst_Max20_AndPerTemplate()
    {
        var r = NewRig();
        var t0 = DateTimeOffset.UtcNow.AddDays(-1);
        var ids = new List<Guid>();
        for (var i = 0; i < 22; i++) ids.Add(await SeedJobAsync(r, TemplateJobState.Failed, createdAt: t0.AddMinutes(i)));
        using var client = Admin(r);

        var list = await client.GetFromJsonAsync<JsonElement>($"/api/admin/templates/{Seeded}/jobs", Ct);

        Assert.Equal(20, list.GetArrayLength());
        Assert.Equal(ids.AsEnumerable().Reverse().Take(20), list.EnumerateArray().Select(j => j.GetProperty("id").GetGuid()));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/admin/templates/nope/jobs", Ct)).StatusCode);
    }

    // ---- seal / cancel ----

    [Fact]
    public async Task Seal_InConfiguring_Returns202_RecordsTheRequestAndNote_AndAudits()
    {
        var r = NewRig();
        var id = await SeedJobAsync(r, TemplateJobState.Configuring);
        using var client = Admin(r);

        var resp = await client.PostAsJsonAsync($"/api/admin/template-jobs/{id}/seal", new { note = "installed 7-Zip" }, Ct);

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var job = await InDbAsync(r, db => db.TemplateJobs.AsNoTracking().SingleAsync(j => j.Id == id, Ct));
        Assert.NotNull(job.SealRequestedAt);
        Assert.Equal("installed 7-Zip", job.Note);
        Assert.Equal(TemplateJobState.Configuring, job.State); // the coordinator moves it on
        var audit = Assert.Single(await AuditsAsync(r, "template.job.seal"));
        Assert.Equal((Seeded, true), (audit.Target, audit.UserId is not null));
        Assert.Contains(id.ToString(), audit.Details);
    }

    [Fact]
    public async Task Seal_WithoutABody_IsAccepted()
    {
        var r = NewRig();
        var id = await SeedJobAsync(r, TemplateJobState.Configuring);
        using var client = Admin(r);

        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"/api/admin/template-jobs/{id}/seal", null, Ct)).StatusCode);
    }

    [Theory]
    [InlineData(TemplateJobState.Queued)]
    [InlineData(TemplateJobState.Preparing)]
    [InlineData(TemplateJobState.Sealing)]
    [InlineData(TemplateJobState.Published)]
    [InlineData(TemplateJobState.Cancelled)]
    public async Task Seal_OutsideConfiguring_Is409(TemplateJobState state)
    {
        var r = NewRig();
        var id = await SeedJobAsync(r, state);
        using var client = Admin(r);

        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/admin/template-jobs/{id}/seal", new { note = "x" }, Ct)).StatusCode);
        Assert.Null((await InDbAsync(r, db => db.TemplateJobs.AsNoTracking().SingleAsync(j => j.Id == id, Ct))).SealRequestedAt);
        Assert.Empty(await AuditsAsync(r, "template.job.seal"));
    }

    [Fact]
    public async Task Seal_Unknown_Is404()
    {
        var r = NewRig();
        using var client = Admin(r);

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/admin/template-jobs/{Guid.NewGuid()}/seal", new { }, Ct)).StatusCode);
    }

    [Theory]
    [InlineData(TemplateJobState.Queued)]
    [InlineData(TemplateJobState.Configuring)]
    [InlineData(TemplateJobState.Sealing)]
    public async Task Cancel_Returns202_RecordsTheRequest_AndAuditsTheCallingAdmin(TemplateJobState state)
    {
        var r = NewRig();
        var id = await SeedJobAsync(r, state);
        using var client = Admin(r, "canceller");
        var me = (await client.GetFromJsonAsync<JsonElement>("/api/me", Ct)).GetProperty("id").GetGuid();

        var resp = await client.PostAsync($"/api/admin/template-jobs/{id}/cancel", null, Ct);

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        Assert.NotNull((await InDbAsync(r, db => db.TemplateJobs.AsNoTracking().SingleAsync(j => j.Id == id, Ct))).CancelRequestedAt);
        var audit = Assert.Single(await AuditsAsync(r, "template.job.cancel"));
        Assert.Equal((Seeded, me), (audit.Target, audit.UserId));
        Assert.Contains(id.ToString(), audit.Details);
    }

    [Fact]
    public async Task Cancel_Twice_IsAccepted_ButAuditedOnce()
    {
        var r = NewRig();
        var id = await SeedJobAsync(r, TemplateJobState.Configuring);
        using var client = Admin(r);

        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"/api/admin/template-jobs/{id}/cancel", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"/api/admin/template-jobs/{id}/cancel", null, Ct)).StatusCode);

        Assert.Single(await AuditsAsync(r, "template.job.cancel"));
    }

    [Theory]
    [InlineData(TemplateJobState.Published)]
    [InlineData(TemplateJobState.Sealed)]
    [InlineData(TemplateJobState.Failed)]
    [InlineData(TemplateJobState.Cancelled)]
    public async Task Cancel_OfATerminalJob_Is409(TemplateJobState state)
    {
        var r = NewRig();
        var id = await SeedJobAsync(r, state);
        using var client = Admin(r);

        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/admin/template-jobs/{id}/cancel", null, Ct)).StatusCode);
        Assert.Empty(await AuditsAsync(r, "template.job.cancel"));
    }

    [Fact]
    public async Task Cancel_Unknown_Is404()
    {
        var r = NewRig();
        using var client = Admin(r);

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/admin/template-jobs/{Guid.NewGuid()}/cancel", null, Ct)).StatusCode);
    }

    // ---- console token ----

    [Fact]
    public async Task TunnelToken_InConfiguring_IsIssuedForTheJobAndAdmin_AndAudited()
    {
        var r = NewRig();
        var id = await SeedJobAsync(r, TemplateJobState.Configuring);
        using var client = Admin(r);

        var resp = await client.GetAsync($"/api/admin/template-jobs/{id}/tunnel-token", Ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var token = (await ReadAsync(resp)).GetProperty("token").GetString()!;
        Assert.Equal(43, token.Length);
        var tokens = r.Factory.Services.GetRequiredService<GuacamoleTokenService>();
        Assert.False(tokens.TryConsume(token, out _, out _), "a job token must not open a task tunnel");
        var audit = Assert.Single(await AuditsAsync(r, "template.console.token"));
        Assert.Equal(Seeded, audit.Target);
        Assert.DoesNotContain(await InDbAsync(r, db => db.AuditLogs.AsNoTracking().ToListAsync(Ct)), a => a.Details?.Contains(token) == true);
    }

    [Fact]
    public async Task TunnelToken_IsBoundToTheJobAndTheCallingAdmin()
    {
        var r = NewRig();
        var id = await SeedJobAsync(r, TemplateJobState.Configuring);
        using var client = Admin(r, "console-admin");
        var me = (await client.GetFromJsonAsync<JsonElement>("/api/me", Ct)).GetProperty("id").GetGuid();
        var token = (await client.GetFromJsonAsync<JsonElement>($"/api/admin/template-jobs/{id}/tunnel-token", Ct)).GetProperty("token").GetString()!;

        var tokens = r.Factory.Services.GetRequiredService<GuacamoleTokenService>();
        Assert.True(tokens.TryConsume(TunnelTokenKind.TemplateJob, token, out var bound, out var user));
        Assert.Equal((id, me), (bound, user));
    }

    [Theory]
    [InlineData(TemplateJobState.Queued)]
    [InlineData(TemplateJobState.Sealing)]
    [InlineData(TemplateJobState.Published)]
    [InlineData(TemplateJobState.Cancelled)]
    public async Task TunnelToken_OutsideConfiguring_Is409(TemplateJobState state)
    {
        var r = NewRig();
        var id = await SeedJobAsync(r, state);
        using var client = Admin(r);

        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync($"/api/admin/template-jobs/{id}/tunnel-token", Ct)).StatusCode);
        Assert.Equal(0, r.Factory.Services.GetRequiredService<GuacamoleTokenService>().PendingCount);
    }

    [Theory]
    [InlineData("seal")]
    [InlineData("cancel")]
    public async Task TunnelToken_AfterASealOrCancelRequest_Is409(string request)
    {
        var r = NewRig();
        var id = await SeedJobAsync(r, TemplateJobState.Configuring);
        using var client = Admin(r);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"/api/admin/template-jobs/{id}/{request}", null, Ct)).StatusCode);

        var resp = await client.GetAsync($"/api/admin/template-jobs/{id}/tunnel-token", Ct);

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode); // the job is still Configuring, but the guest is about to be sealed or destroyed
        Assert.Equal(0, r.Factory.Services.GetRequiredService<GuacamoleTokenService>().PendingCount);
        Assert.Empty(await AuditsAsync(r, "template.console.token"));
    }

    [Fact]
    public async Task TunnelToken_Unknown_Is404()
    {
        var r = NewRig();
        using var client = Admin(r);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/admin/template-jobs/{Guid.NewGuid()}/tunnel-token", Ct)).StatusCode);
    }

    // ---- held versions ----

    [Fact]
    public async Task Publish_AHeldVersion_MakesItCurrent_AndAudits()
    {
        var r = NewRig();
        var held = await SeedHeldVersionAsync(r);
        using var client = Admin(r);

        var resp = await client.PostAsync($"/api/admin/templates/{Seeded}/versions/{held}/publish", null, Ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var dto = await ReadAsync(resp);
        Assert.Equal(Seeded, dto.GetProperty("id").GetString());
        Assert.Equal(2, dto.GetProperty("current").GetProperty("number").GetInt32());
        Assert.Equal("Current", dto.GetProperty("versions").EnumerateArray().Single(v => v.GetProperty("number").GetInt32() == 2).GetProperty("role").GetString());
        var audit = Assert.Single(await AuditsAsync(r, "template.publish"));
        Assert.Equal(Seeded, audit.Target);
        Assert.NotNull(audit.UserId);
        Assert.Contains("v2", audit.Details);
    }

    [Fact]
    public async Task Discard_AHeldVersion_RetiresIt_AndAudits()
    {
        var r = NewRig();
        var held = await SeedHeldVersionAsync(r);
        using var client = Admin(r);

        var resp = await client.PostAsync($"/api/admin/templates/{Seeded}/versions/{held}/discard", null, Ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var dto = await ReadAsync(resp);
        Assert.Equal(1, dto.GetProperty("current").GetProperty("number").GetInt32()); // unchanged
        Assert.Equal("Retired", dto.GetProperty("versions").EnumerateArray().Single(v => v.GetProperty("number").GetInt32() == 2).GetProperty("status").GetString());
        Assert.Equal(TemplateVersionStatus.Retired, (await InDbAsync(r, db => db.TemplateVersions.AsNoTracking().SingleAsync(v => v.Id == held, Ct))).Status);
        var audit = Assert.Single(await AuditsAsync(r, "template.version.discard"));
        Assert.Equal(Seeded, audit.Target);
        Assert.NotNull(audit.UserId);
        Assert.Contains("v2", audit.Details);
    }

    [Fact]
    public async Task Discard_ThenTheSweep_DeletesTheDisk()
    {
        var r = NewRig();
        var held = await SeedHeldVersionAsync(r);
        using var client = Admin(r);
        await client.PostAsync($"/api/admin/templates/{Seeded}/versions/{held}/discard", null, Ct);

        await using var scope = r.Factory.Services.CreateAsyncScope();
        var swept = await scope.ServiceProvider.GetRequiredService<Maldex.Data.Templates.TemplateCatalog>().SweepAsync(Ct);

        Assert.Equal([held], swept.Select(v => v.Id));
        Assert.Contains("held-2", r.Store.Deleted);
    }

    [Theory]
    [InlineData("publish")]
    [InlineData("discard")]
    public async Task HeldVersionActions_RefuseAVersionThatIsNotPending_With409(string action)
    {
        var r = NewRig();
        var retired = await SeedHeldVersionAsync(r, TemplateVersionStatus.Retired, number: 2);
        using var client = Admin(r);
        var current = await InDbAsync(r, db => db.Templates.AsNoTracking().Where(t => t.Id == Seeded).Select(t => t.CurrentVersionId).SingleAsync(Ct));

        foreach (var id in new[] { retired, current!.Value })
        {
            var resp = await client.PostAsync($"/api/admin/templates/{Seeded}/versions/{id}/{action}", null, Ct);
            Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        }

        Assert.Empty(await AuditsAsync(r, "template.publish"));
        Assert.Empty(await AuditsAsync(r, "template.version.discard"));
        Assert.Equal(current, await InDbAsync(r, db => db.Templates.AsNoTracking().Where(t => t.Id == Seeded).Select(t => t.CurrentVersionId).SingleAsync(Ct)));
    }

    [Theory]
    [InlineData("publish")]
    [InlineData("discard")]
    public async Task HeldVersionActions_UnknownTemplateOrVersion_Is404(string action)
    {
        var r = NewRig();
        var held = await SeedHeldVersionAsync(r);
        using var client = Admin(r);

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/admin/templates/nope/versions/{held}/{action}", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/admin/templates/{Seeded}/versions/{Guid.NewGuid()}/{action}", null, Ct)).StatusCode);
    }

    // ---- access ----

    [Fact]
    public async Task EveryRoute_IsAdminOnly()
    {
        var r = NewRig();
        var configuring = await SeedJobAsync(r, TemplateJobState.Configuring);
        var held = await SeedHeldVersionAsync(r);
        using var analyst = r.Factory.CreateClient(UserRole.Analyst, "analyst");

        var calls = new (HttpMethod Method, string Url)[]
        {
            (HttpMethod.Post, $"/api/admin/templates/{Seeded}/edit"),
            (HttpMethod.Post, $"/api/admin/templates/{Seeded}/refresh"),
            (HttpMethod.Get, $"/api/admin/templates/{Seeded}/jobs"),
            (HttpMethod.Get, $"/api/admin/template-jobs/{configuring}"),
            (HttpMethod.Post, $"/api/admin/template-jobs/{configuring}/seal"),
            (HttpMethod.Post, $"/api/admin/template-jobs/{configuring}/cancel"),
            (HttpMethod.Get, $"/api/admin/template-jobs/{configuring}/tunnel-token"),
            (HttpMethod.Post, $"/api/admin/templates/{Seeded}/versions/{held}/publish"),
            (HttpMethod.Post, $"/api/admin/templates/{Seeded}/versions/{held}/discard"),
        };
        foreach (var (method, url) in calls)
        {
            var resp = await analyst.SendAsync(new HttpRequestMessage(method, url), Ct);
            Assert.True(resp.StatusCode == HttpStatusCode.Forbidden, $"{method} {url} answered {(int)resp.StatusCode} for an analyst");
        }

        Assert.Equal(1, await InDbAsync(r, db => db.TemplateJobs.CountAsync(Ct)));
        Assert.Empty(await InDbAsync(r, db => db.AuditLogs.AsNoTracking().Where(a => a.Action.StartsWith("template.")).ToListAsync(Ct)));
    }

    // ---- live progress (hub) ----

    private HubConnection Connect(Rig r, UserRole role)
    {
        var server = r.Factory.Server;
        var user = "hub-" + role;
        var conn = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, "/hubs/tasks"), o =>
            {
                o.Transports = HttpTransportType.WebSockets;
                o.HttpMessageHandlerFactory = _ => server.CreateHandler();
                o.Headers["X-Dev-Role"] = role.ToString();
                o.Headers["X-Dev-User"] = user;
                o.WebSocketFactory = async (ctx, ct) =>
                {
                    var ws = server.CreateWebSocketClient();
                    ws.ConfigureRequest = req =>
                    {
                        req.Headers["X-Dev-Role"] = role.ToString();
                        req.Headers["X-Dev-User"] = user;
                    };
                    return await ws.ConnectAsync(ctx.Uri, ct);
                };
            })
            .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();
        _connections.Add(conn);
        return conn;
    }

    private static TemplateJobDto Dto(Guid id, string state, string? message) =>
        new(id, "Edit", Seeded, 1, null, "None", state, message, null, DateTimeOffset.UtcNow, null, null);

    [Fact]
    public async Task Hub_JoinTemplateJob_RequiresAdmin()
    {
        var r = NewRig();
        var id = await SeedJobAsync(r, TemplateJobState.Configuring);
        var analyst = Connect(r, UserRole.Analyst);
        await analyst.StartAsync(Ct);

        await Assert.ThrowsAsync<HubException>(() => analyst.InvokeAsync("JoinTemplateJob", id, Ct));

        var admin = Connect(r, UserRole.Admin);
        await admin.StartAsync(Ct);
        await admin.InvokeAsync("JoinTemplateJob", id, Ct); // no exception
        await Assert.ThrowsAsync<HubException>(() => admin.InvokeAsync("JoinTemplateJob", Guid.NewGuid(), Ct));
    }

    [Fact]
    public async Task Hub_Admin_ReceivesTheJobsUpdates_AndOthersDoNot()
    {
        var r = NewRig();
        var id = await SeedJobAsync(r, TemplateJobState.Preparing);
        var other = await SeedJobAsync(r, TemplateJobState.Failed);
        var got = new List<TemplateJobDto>();
        var admin = Connect(r, UserRole.Admin);
        admin.On<TemplateJobDto>("templateJob", d => { lock (got) got.Add(d); });
        await admin.StartAsync(Ct);
        await admin.InvokeAsync("JoinTemplateJob", id, Ct);
        var publisher = r.Factory.Services.GetRequiredService<ITemplateJobPublisher>();

        await publisher.PublishAsync(Dto(other, "Cancelled", "not mine"), Ct);
        await publisher.PublishAsync(Dto(id, "Configuring", "waiting for the admin to configure and seal"), Ct);
        var until = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < until) { lock (got) if (got.Count > 0) break; await Task.Delay(25, Ct); }
        await Task.Delay(200, Ct);

        TemplateJobDto[] seen;
        lock (got) seen = [.. got];
        var one = Assert.Single(seen);
        Assert.Equal((id, "Configuring"), (one.Id, one.State));

        await admin.InvokeAsync("LeaveTemplateJob", id, Ct);
        await publisher.PublishAsync(Dto(id, "Sealing", "after leaving"), Ct);
        await Task.Delay(300, Ct);
        lock (got) Assert.Single(got);
    }
}
