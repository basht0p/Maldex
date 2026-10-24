using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Maldex.Data;
using Maldex.Infrastructure.Guacamole;
using Maldex.Integration.Tests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Maldex.Integration.Tests.Web;

/// <summary>The live-view tunnel against a TCP listener that plays guacd, with a stub console locator.</summary>
[Collection("sql")]
public sealed class GuacamoleTunnelTests(SqlServerFixture sql) : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string VncPassword = "s3cret-vnc";

    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
    }

    private sealed class StubLocator(VncEndpoint? endpoint) : IVmConsoleLocator
    {
        public Task<VncEndpoint?> GetAsync(VmHandle vm, CancellationToken ct) => Task.FromResult(endpoint);
    }

    private sealed class TunnelFactory(SqlServerFixture sql, int guacdPort, VncEndpoint? endpoint, string authMode = "Dev", FakeTimeProvider? clock = null, string keepAlive = "00:01:00", Maldex.Orchestrator.Templates.ITemplateJobPublisher? publisher = null)
        : MaldexAppFactory(sql, authMode: authMode, settings: new Dictionary<string, string?>
        {
            ["Maldex:Guacamole:GuacdPort"] = guacdPort.ToString(),
            ["Maldex:Guacamole:StatusPollInterval"] = "00:00:00.100",
            ["Maldex:Guacamole:ConsoleKeepAliveInterval"] = keepAlive,
            // Only read in Entra mode: placeholders so the JWT bearer scheme can be built (no token is ever validated here).
            ["AzureAd:Instance"] = "https://login.microsoftonline.com/",
            ["AzureAd:TenantId"] = "00000000-0000-0000-0000-000000000000",
            ["AzureAd:ClientId"] = "00000000-0000-0000-0000-000000000001",
        })
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(s =>
            {
                s.AddSingleton<IVmConsoleLocator>(new StubLocator(endpoint));
                if (clock is not null) s.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock));
                if (publisher is not null) s.Replace(ServiceDescriptor.Singleton(publisher));
            });
        }
    }

    private TunnelFactory Factory(int guacdPort, VncEndpoint? endpoint = null)
    {
        var f = new TunnelFactory(sql, guacdPort, endpoint ?? new VncEndpoint("127.0.0.1", 5901, VncPassword));
        _disposables.Add(f);
        return f;
    }

    private static async Task<Guid> CreateTaskAsync(MaldexAppFactory f, RunStatus status)
    {
        using var admin = f.CreateClient(UserRole.Admin, "admin");
        var orgResp = await admin.PostAsJsonAsync("/api/orgs", new { name = "Org-" + Guid.NewGuid().ToString("N")[..8], retentionDays = 30 }, Ct);
        orgResp.EnsureSuccessStatusCode();
        var orgId = (await orgResp.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

        using var analyst = f.CreateClient(UserRole.Analyst, "analyst");
        using var form = new MultipartFormDataContent
        {
            { new StringContent(orgId.ToString()), "orgId" },
            { new StringContent("http://example.test/"), "url" },
        };
        var resp = await analyst.PostAsync("/api/tasks", form, Ct);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var id = (await resp.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        await SetStatusAsync(f, id, status);
        return id;
    }

    private static async Task SetStatusAsync(MaldexAppFactory f, Guid id, RunStatus status)
    {
        await using var scope = f.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        await db.Tasks.Where(t => t.Id == id).ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, status), Ct);
    }

    private static async Task<string> GetTokenAsync(HttpClient client, Guid id)
    {
        var resp = await client.GetAsync($"/api/tasks/{id}/tunnel-token", Ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("token").GetString()!;
    }

    private static async Task<WebSocket> ConnectAsync(MaldexAppFactory f, Guid id, string token, string user = "analyst")
    {
        var ws = f.Server.CreateWebSocketClient();
        ws.SubProtocols.Add("guacamole");
        ws.ConfigureRequest = r =>
        {
            r.Headers["X-Dev-Role"] = "Analyst";
            r.Headers["X-Dev-User"] = user;
        };
        return await ws.ConnectAsync(new Uri(f.Server.BaseAddress, $"/api/tasks/{id}/tunnel?token={token}&width=800&height=600"), Ct);
    }

    private static async Task<string> ReceiveTextAsync(WebSocket ws)
    {
        var buf = new byte[65536];
        using var ms = new MemoryStream();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        WebSocketReceiveResult r;
        do
        {
            r = await ws.ReceiveAsync(buf, cts.Token);
            if (r.MessageType == WebSocketMessageType.Close) return "<close:" + r.CloseStatus + ">";
            ms.Write(buf, 0, r.Count);
        } while (!r.EndOfMessage);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static Task SendTextAsync(WebSocket ws, string text) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, Ct);

    /// <summary>A guacd stand-in: speaks the server side of the Guacamole handshake and records what the proxy sends.</summary>
    private sealed class FakeGuacd : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        private readonly object _gate = new();
        private readonly List<string[]> _received = [];

        /// <summary>A snapshot of what guacd has received so far (written by the server task, read by the test).</summary>
        public List<string[]> Received { get { lock (_gate) return [.. _received]; } }

        private void Record(string[] instruction) { lock (_gate) _received.Add(instruction); }

        public TaskCompletionSource<NetworkStream> Connected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Run { get; private set; } = Task.CompletedTask;

        public FakeGuacd()
        {
            _listener.Start();
            Run = Task.Run(async () =>
            {
                using var client = await _listener.AcceptTcpClientAsync(Ct);
                var stream = client.GetStream();
                var reader = new GuacamoleInstructionReader(new StreamReader(stream, new UTF8Encoding(false)));
                async Task Send(params string[] e) => await stream.WriteAsync(Encoding.UTF8.GetBytes(GuacamoleProtocol.Encode(e)), Ct);

                Record((await reader.ReadAsync(Ct))!); // select
                await Send("args", "hostname", "port", "password", "read-only", "VERSION_1_5_0");
                for (var i = 0; i < 5; i++) Record((await reader.ReadAsync(Ct))!); // size, audio, video, image, connect
                await Send("ready", "guacd-conn-1");
                Connected.SetResult(stream);
                while (await reader.ReadAsync(Ct) is { } more) Record(more);
            }, Ct);
        }

        public void Dispose() => _listener.Stop();
    }

    [Fact]
    public async Task TunnelToken_Endpoint_RequiresRunningTask_Audited()
    {
        var f = Factory(1);
        var running = await CreateTaskAsync(f, RunStatus.Running);
        var queued = await CreateTaskAsync(f, RunStatus.Queued);
        using var analyst = f.CreateClient(UserRole.Analyst, "analyst");

        Assert.Equal(HttpStatusCode.NotFound, (await analyst.GetAsync($"/api/tasks/{Guid.NewGuid()}/tunnel-token", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await analyst.GetAsync($"/api/tasks/{queued}/tunnel-token", Ct)).StatusCode);

        var token = await GetTokenAsync(analyst, running);
        Assert.Equal(43, token.Length);

        await using var scope = f.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        Assert.Single(await db.AuditLogs.Where(a => a.Action == "session.token" && a.Target == running.ToString()).ToListAsync(Ct));
        Assert.DoesNotContain(await db.AuditLogs.ToListAsync(Ct), a => a.Details?.Contains(token) == true);
    }

    [Fact]
    public async Task Tunnel_RejectsBadToken_ClosesSocket()
    {
        var f = Factory(1);
        var id = await CreateTaskAsync(f, RunStatus.Running);
        using var analyst = f.CreateClient(UserRole.Analyst, "analyst");

        // Garbage token.
        using (var ws = await ConnectAsync(f, id, "not-a-token"))
        {
            Assert.StartsWith("<close:PolicyViolation>", await ReceiveTextAsync(ws));
        }

        // The token is single use: the first connect consumes it (no guacd is listening, so that attempt ends with an error close)...
        var token = await GetTokenAsync(analyst, id);
        using (var ws = await ConnectAsync(f, id, token))
        {
            Assert.DoesNotContain("PolicyViolation", await ReceiveTextAsync(ws));
        }
        // ...and a second use is refused.
        using (var ws = await ConnectAsync(f, id, token))
        {
            Assert.StartsWith("<close:PolicyViolation>", await ReceiveTextAsync(ws));
        }

        // A token issued for another task does not open this one.
        var other = await CreateTaskAsync(f, RunStatus.Running);
        var otherToken = await GetTokenAsync(analyst, other);
        using (var ws = await ConnectAsync(f, id, otherToken))
        {
            Assert.StartsWith("<close:PolicyViolation>", await ReceiveTextAsync(ws));
        }

        // A valid token for a task that is no longer running is refused too.
        await SetStatusAsync(f, other, RunStatus.Completed);
        var lateToken = await GetTokenAsync(analyst, id);
        await SetStatusAsync(f, id, RunStatus.Completed);
        using (var ws = await ConnectAsync(f, id, lateToken))
        {
            Assert.StartsWith("<close:PolicyViolation>", await ReceiveTextAsync(ws));
        }

        // Refusals of a KNOWN token (wrong task, task not running) are audited as session.join.denied; unknown, garbage or
        // reused tokens are not (an anonymous upgrade must not be able to flood the audit trail). No token is ever logged.
        await using var scope = f.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        var audit = await db.AuditLogs.ToListAsync(Ct);
        Assert.Equal(["task is not running", "token was issued for another task"],
            audit.Where(a => a.Action == "session.join.denied").Select(a => a.Details).Order(StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(audit, a => a.Details is not null && (a.Details.Contains(token) || a.Details.Contains(otherToken) || a.Details.Contains(lateToken)));
    }

    [Fact]
    public async Task Tunnel_AnonymousUpgradesWithoutAKnownToken_AreNotAudited()
    {
        var f = Factory(1);
        var id = await CreateTaskAsync(f, RunStatus.Running);
        for (var i = 0; i < 25; i++)
        {
            using var ws = await ConnectAsync(f, id, i % 2 == 0 ? "" : "forged-" + i);
            Assert.StartsWith("<close:PolicyViolation>", await ReceiveTextAsync(ws));
        }

        await using var scope = f.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        Assert.DoesNotContain(await db.AuditLogs.ToListAsync(Ct), a => a.Action.StartsWith("session.join", StringComparison.Ordinal));
    }

    private static async Task<Exception?> UpgradeWithOriginAsync(MaldexAppFactory f, Guid id, string token, string url, string origin)
    {
        var ws = f.Server.CreateWebSocketClient();
        ws.SubProtocols.Add("guacamole");
        ws.ConfigureRequest = r => r.Headers["Origin"] = origin;
        try
        {
            using var socket = await ws.ConnectAsync(new Uri(url.Replace("{id}", id.ToString()) + $"?token={token}"), Ct);
            return null;
        }
        catch (Exception ex) { return ex; }
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("http://localhost:5174")]   // other port
    [InlineData("https://localhost:5173")]  // other scheme
    [InlineData("http://localhost")]        // default port 80 is not 5173
    public async Task Tunnel_RejectsCrossOriginUpgrade_With403(string origin)
    {
        var f = Factory(1);
        var id = await CreateTaskAsync(f, RunStatus.Running);
        using var analyst = f.CreateClient(UserRole.Analyst, "analyst");
        var token = await GetTokenAsync(analyst, id);

        var ex = await UpgradeWithOriginAsync(f, id, token, "ws://localhost:5173/api/tasks/{id}/tunnel", origin);
        Assert.NotNull(ex);
        Assert.Contains("403", ex.Message);
    }

    [Fact]
    public async Task Tunnel_AcceptsDevServerOrigin_HostAndOriginMatch()
    {
        // The Vite dev server (changeOrigin: false) forwards Host: localhost:5173 and Origin: http://localhost:5173.
        using var guacd = new FakeGuacd();
        var f = Factory(guacd.Port);
        var id = await CreateTaskAsync(f, RunStatus.Running);
        using var analyst = f.CreateClient(UserRole.Analyst, "analyst");
        var token = await GetTokenAsync(analyst, id);

        Assert.Null(await UpgradeWithOriginAsync(f, id, token, "ws://localhost:5173/api/tasks/{id}/tunnel", "http://localhost:5173"));
    }

    [Fact]
    public async Task Tunnel_NormalisesDefaultPorts()
    {
        using var guacd = new FakeGuacd();
        var f = Factory(guacd.Port);
        var id = await CreateTaskAsync(f, RunStatus.Running);
        using var analyst = f.CreateClient(UserRole.Analyst, "analyst");

        // Host without a port and Origin with the explicit default port are the same origin.
        Assert.Null(await UpgradeWithOriginAsync(f, id, await GetTokenAsync(analyst, id), "ws://localhost/api/tasks/{id}/tunnel", "http://localhost:80"));
    }

    [Fact]
    public async Task Tunnel_TokenIsTheCredential_NoAuthHeaders_EntraMode()
    {
        using var guacd = new FakeGuacd();
        var f = new TunnelFactory(sql, guacd.Port, new VncEndpoint("127.0.0.1", 5901, VncPassword), authMode: "Entra");
        _disposables.Add(f);

        // Seed a Running task and a user straight into the database: Entra mode has no dev sign-in.
        var userId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
            var org = new Maldex.Core.Domain.Org { Id = Guid.NewGuid(), Name = "Org-" + Guid.NewGuid().ToString("N"), RetentionDays = 30 };
            db.AddRange(
                org,
                new AppUser { Id = userId, EntraObjectId = Guid.NewGuid().ToString("N"), DisplayName = "A", Role = UserRole.Analyst },
                new AnalysisTask
                {
                    Id = taskId, OrgId = org.Id, SubmittedByUserId = userId, Url = "http://example.test/", Image = "win10",
                    NetworkMode = NetworkMode.Simulated, DurationSec = 300, CreatedAt = DateTimeOffset.UtcNow, Status = RunStatus.Running,
                });
            await db.SaveChangesAsync(Ct);
        }

        // Issuing a token still needs an authenticated Analyst.
        using var anonymous = f.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/tasks/{taskId}/tunnel-token", Ct)).StatusCode);

        // The upgrade carries no Authorization header and no dev headers: only the token.
        var token = f.Services.GetRequiredService<GuacamoleTokenService>().Issue(taskId, userId);
        var client = f.Server.CreateWebSocketClient();
        client.SubProtocols.Add("guacamole");
        using var ws = await client.ConnectAsync(new Uri(f.Server.BaseAddress, $"/api/tasks/{taskId}/tunnel?token={token}"), Ct);
        await ReceiveTextAsync(ws); // tunnel uuid
        Assert.Equal("ready", Assert.Single(await GuacamoleInstructionReader.ParseAllAsync(await ReceiveTextAsync(ws), Ct))[0]);

        // The audit row names the token's user, not a caller.
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
            var join = Assert.Single(await db.AuditLogs.Where(a => a.Action == "session.join").ToListAsync(Ct));
            Assert.Equal(userId, join.UserId);
        }

        // Without a valid token the same unauthenticated upgrade is refused.
        var bad = f.Server.CreateWebSocketClient();
        bad.SubProtocols.Add("guacamole");
        using var rejected = await bad.ConnectAsync(new Uri(f.Server.BaseAddress, $"/api/tasks/{taskId}/tunnel"), Ct);
        Assert.StartsWith("<close:PolicyViolation>", await ReceiveTextAsync(rejected));
    }

    [Fact]
    public async Task Tunnel_ClosesOnDisallowedUpstreamOpcode()
    {
        using var guacd = new FakeGuacd();
        var f = Factory(guacd.Port);
        var id = await CreateTaskAsync(f, RunStatus.Running);
        using var analyst = f.CreateClient(UserRole.Analyst, "analyst");
        using var ws = await ConnectAsync(f, id, await GetTokenAsync(analyst, id));
        await ReceiveTextAsync(ws);
        await ReceiveTextAsync(ws);

        // Allowed opcodes pass.
        foreach (var op in new[] { "mouse", "key", "sync", "size", "disconnect", "nop", "ack" })
            await SendTextAsync(ws, GuacamoleProtocol.Encode(op, "1"));
        var until = DateTime.UtcNow.AddSeconds(20);
        while (guacd.Received.Count < 6 + 7 && DateTime.UtcNow < until) await Task.Delay(20, Ct);
        Assert.Equal(13, guacd.Received.Count);

        // Anything else (here: file transfer / clipboard) ends the session, and never reaches guacd.
        await SendTextAsync(ws, GuacamoleProtocol.Encode("clipboard", "1", "text/plain"));
        Assert.StartsWith("<close:PolicyViolation>", await ReceiveTextAsync(ws));
        Assert.DoesNotContain(guacd.Received, i => i[0] == "clipboard");
    }

    [Fact]
    public async Task Tunnel_ProxiesHandshakeToFakeGuacd()
    {
        using var guacd = new FakeGuacd();
        var f = Factory(guacd.Port);
        var id = await CreateTaskAsync(f, RunStatus.Running);
        using var analyst = f.CreateClient(UserRole.Analyst, "analyst");
        var token = await GetTokenAsync(analyst, id);

        using var ws = await ConnectAsync(f, id, token);
        Assert.Equal("guacamole", ws.SubProtocol);

        // First the tunnel UUID (internal opcode), then guacd's ready.
        var first = await ReceiveTextAsync(ws);
        var uuid = Assert.Single(await GuacamoleInstructionReader.ParseAllAsync(first, Ct));
        Assert.Equal("", uuid[0]);
        Assert.Equal(2, uuid.Length);
        Assert.Equal(["ready", "guacd-conn-1"], Assert.Single(await GuacamoleInstructionReader.ParseAllAsync(await ReceiveTextAsync(ws), Ct)));

        // The handshake guacd saw: select vnc, size/audio/video/image, connect with values in args order.
        var stream = await guacd.Connected.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        Assert.Equal(["select", "vnc"], guacd.Received[0]);
        Assert.Equal("size", guacd.Received[1][0]);
        Assert.Equal(["800", "600"], guacd.Received[1][1..3]);
        Assert.Equal(["audio"], guacd.Received[2]);
        Assert.Equal(["video"], guacd.Received[3]);
        Assert.Equal("image", guacd.Received[4][0]);
        Assert.Equal(["connect", "127.0.0.1", "5901", VncPassword, "", "VERSION_1_5_0"], guacd.Received[5]);

        // guacd -> browser: instructions are relayed whole.
        await stream.WriteAsync(Encoding.UTF8.GetBytes(GuacamoleProtocol.Encode("sync", "1234")), Ct);
        Assert.Equal(["sync", "1234"], Assert.Single(await GuacamoleInstructionReader.ParseAllAsync(await ReceiveTextAsync(ws), Ct)));

        // browser -> guacd: forwarded; internal ping is answered locally and never reaches guacd.
        await SendTextAsync(ws, GuacamoleProtocol.Encode("", "ping", "42"));
        Assert.Equal(["", "ping", "42"], Assert.Single(await GuacamoleInstructionReader.ParseAllAsync(await ReceiveTextAsync(ws), Ct)));
        await SendTextAsync(ws, GuacamoleProtocol.Encode("mouse", "10", "20", "1"));
        var until = DateTime.UtcNow.AddSeconds(20);
        while (guacd.Received.Count < 7 && DateTime.UtcNow < until) await Task.Delay(20, Ct);
        Assert.Equal(["mouse", "10", "20", "1"], guacd.Received[6]);
        Assert.DoesNotContain(guacd.Received, i => i[0] == "");

        // Audited.
        await using var scope = f.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        Assert.Single(await db.AuditLogs.Where(a => a.Action == "session.join" && a.Target == id.ToString()).ToListAsync(Ct));
    }

    [Fact]
    public async Task Tunnel_ClosesWhenTaskLeavesRunning()
    {
        using var guacd = new FakeGuacd();
        var f = Factory(guacd.Port);
        var id = await CreateTaskAsync(f, RunStatus.Running);
        using var analyst = f.CreateClient(UserRole.Analyst, "analyst");
        using var ws = await ConnectAsync(f, id, await GetTokenAsync(analyst, id));
        await ReceiveTextAsync(ws); // uuid
        await ReceiveTextAsync(ws); // ready

        await SetStatusAsync(f, id, RunStatus.Collecting);
        var close = await ReceiveTextAsync(ws);
        Assert.StartsWith("<close:", close);
        Assert.DoesNotContain("PolicyViolation", close);
    }

    [Fact]
    public async Task Tunnel_ClosesWhenNoConsoleAvailable()
    {
        var f = new TunnelFactory(sql, 1, endpoint: null);
        _disposables.Add(f);
        var id = await CreateTaskAsync(f, RunStatus.Running);
        using var analyst = f.CreateClient(UserRole.Analyst, "analyst");
        using var ws = await ConnectAsync(f, id, await GetTokenAsync(analyst, id));
        var msg = await ReceiveTextAsync(ws);
        Assert.StartsWith("<close:", msg);
    }

    // ---- the configure console of a template job ----

    private const string SeededTemplate = "win10-ltsc-2021";

    private static async Task<Guid> CreateJobAsync(MaldexAppFactory f, TemplateJobState state, DateTimeOffset? configuringSince = null)
    {
        using var admin = f.CreateClient(UserRole.Admin, "admin");
        await admin.GetAsync("/api/me", Ct); // creates the dev user row
        await using var scope = f.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        var v1 = await db.TemplateVersions.AsNoTracking().SingleAsync(v => v.TemplateId == SeededTemplate && v.Number == 1, Ct);
        var since = configuringSince ?? DateTimeOffset.UtcNow;
        var job = new TemplateJob
        {
            Id = Guid.NewGuid(), Kind = TemplateJobKind.Edit, TemplateId = SeededTemplate, BaseVersionId = v1.Id, State = state,
            RequestedByUserId = (await db.Users.AsNoTracking().FirstAsync(Ct)).Id, CreatedAt = since, ConfiguringSince = since,
        };
        db.TemplateJobs.Add(job);
        await db.SaveChangesAsync(Ct);
        return job.Id;
    }

    private static async Task SetJobStateAsync(MaldexAppFactory f, Guid id, TemplateJobState state)
    {
        await using var scope = f.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        await db.TemplateJobs.Where(j => j.Id == id).ExecuteUpdateAsync(s => s.SetProperty(j => j.State, state), Ct);
    }

    private static async Task<DateTimeOffset?> LastConsoleAtAsync(MaldexAppFactory f, Guid id)
    {
        await using var scope = f.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        return await db.TemplateJobs.AsNoTracking().Where(j => j.Id == id).Select(j => j.LastConsoleAt).SingleAsync(Ct);
    }

    private static async Task<string> GetJobTokenAsync(HttpClient admin, Guid id)
    {
        var resp = await admin.GetAsync($"/api/admin/template-jobs/{id}/tunnel-token", Ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("token").GetString()!;
    }

    private static async Task<WebSocket> ConnectJobAsync(MaldexAppFactory f, Guid id, string token)
    {
        var ws = f.Server.CreateWebSocketClient();
        ws.SubProtocols.Add("guacamole");
        ws.ConfigureRequest = r =>
        {
            r.Headers["X-Dev-Role"] = "Admin";
            r.Headers["X-Dev-User"] = "admin";
        };
        return await ws.ConnectAsync(new Uri(f.Server.BaseAddress, $"/api/admin/template-jobs/{id}/tunnel?token={token}&width=800&height=600"), Ct);
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(20);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < until, "timed out waiting for " + what);
            await Task.Delay(25, Ct);
        }
    }

    [Fact]
    public async Task JobTunnel_ProxiesTheConsole_SetsLastConsoleAt_AndAudits()
    {
        using var guacd = new FakeGuacd();
        var f = Factory(guacd.Port);
        var id = await CreateJobAsync(f, TemplateJobState.Configuring);
        using var admin = f.CreateClient(UserRole.Admin, "admin");
        Assert.Null(await LastConsoleAtAsync(f, id));
        var before = DateTimeOffset.UtcNow;

        using var ws = await ConnectJobAsync(f, id, await GetJobTokenAsync(admin, id));
        Assert.Equal("guacamole", ws.SubProtocol);
        await ReceiveTextAsync(ws); // tunnel uuid
        Assert.Equal(["ready", "guacd-conn-1"], Assert.Single(await GuacamoleInstructionReader.ParseAllAsync(await ReceiveTextAsync(ws), Ct)));

        Assert.True(await LastConsoleAtAsync(f, id) >= before);
        var stream = await guacd.Connected.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(GuacamoleProtocol.Encode("sync", "99")), Ct);
        Assert.Equal(["sync", "99"], Assert.Single(await GuacamoleInstructionReader.ParseAllAsync(await ReceiveTextAsync(ws), Ct)));
        await SendTextAsync(ws, GuacamoleProtocol.Encode("key", "65", "1"));
        await WaitForAsync(() => Task.FromResult(guacd.Received.Count >= 7), "the key instruction");
        Assert.Equal(["key", "65", "1"], guacd.Received[6]);

        await using var scope = f.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        var join = Assert.Single(await db.AuditLogs.Where(a => a.Action == "template.console.join").ToListAsync(Ct));
        Assert.Equal(SeededTemplate, join.Target);
        Assert.NotNull(join.UserId);
        Assert.Equal($"job={id}", join.Details);
    }

    [Fact]
    public async Task JobTunnel_EnforcesTheOpcodeAllowlist()
    {
        using var guacd = new FakeGuacd();
        var f = Factory(guacd.Port);
        var id = await CreateJobAsync(f, TemplateJobState.Configuring);
        using var admin = f.CreateClient(UserRole.Admin, "admin");
        using var ws = await ConnectJobAsync(f, id, await GetJobTokenAsync(admin, id));
        await ReceiveTextAsync(ws);
        await ReceiveTextAsync(ws);

        await SendTextAsync(ws, GuacamoleProtocol.Encode("clipboard", "1", "text/plain"));

        Assert.StartsWith("<close:PolicyViolation>", await ReceiveTextAsync(ws));
        Assert.DoesNotContain(guacd.Received, i => i[0] == "clipboard");
    }

    [Fact]
    public async Task JobTunnel_RejectsCrossOriginUpgrade_With403()
    {
        var f = Factory(1);
        var id = await CreateJobAsync(f, TemplateJobState.Configuring);
        using var admin = f.CreateClient(UserRole.Admin, "admin");
        var token = await GetJobTokenAsync(admin, id);

        var ex = await UpgradeWithOriginAsync(f, id, token, "ws://localhost:5173/api/admin/template-jobs/{id}/tunnel", "https://evil.example");

        Assert.NotNull(ex);
        Assert.Contains("403", ex.Message);
    }

    [Fact]
    public async Task Tokens_OfTheOtherKind_OpenNothing()
    {
        using var guacd = new FakeGuacd();
        var f = Factory(guacd.Port);
        var taskId = await CreateTaskAsync(f, RunStatus.Running);
        var jobId = await CreateJobAsync(f, TemplateJobState.Configuring);
        using var admin = f.CreateClient(UserRole.Admin, "admin");

        // A task token on the job tunnel.
        using (var ws = await ConnectJobAsync(f, jobId, await GetTokenAsync(admin, taskId)))
            Assert.StartsWith("<close:PolicyViolation>", await ReceiveTextAsync(ws));
        // A job token on the task tunnel.
        using (var ws = await ConnectAsync(f, taskId, await GetJobTokenAsync(admin, jobId)))
            Assert.StartsWith("<close:PolicyViolation>", await ReceiveTextAsync(ws));

        // Even when the id matches (a token of the other kind issued for this very task or job), the kind decides.
        var tokens = f.Services.GetRequiredService<GuacamoleTokenService>();
        var someone = Guid.NewGuid();
        using (var ws = await ConnectJobAsync(f, jobId, tokens.Issue(TunnelTokenKind.Task, jobId, someone)))
            Assert.StartsWith("<close:PolicyViolation>", await ReceiveTextAsync(ws));
        using (var ws = await ConnectAsync(f, taskId, tokens.Issue(TunnelTokenKind.TemplateJob, taskId, someone)))
            Assert.StartsWith("<close:PolicyViolation>", await ReceiveTextAsync(ws));

        // Every one was consumed by the refusal, and nothing reached guacd or the audit trail as a join.
        Assert.Equal(0, f.Services.GetRequiredService<GuacamoleTokenService>().PendingCount);
        Assert.Empty(guacd.Received);
        await using var scope = f.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        Assert.DoesNotContain(await db.AuditLogs.ToListAsync(Ct), a => a.Action is "session.join" or "template.console.join");
        Assert.Null(await LastConsoleAtAsync(f, jobId));
    }

    [Fact]
    public async Task JobTunnel_RefusesATokenOfAnotherJob_Audited()
    {
        var f = Factory(1);
        var job = await CreateJobAsync(f, TemplateJobState.Configuring);
        var other = await CreateJobAsync(f, TemplateJobState.Failed);
        using var admin = f.CreateClient(UserRole.Admin, "admin");
        var token = await GetJobTokenAsync(admin, job);

        using (var ws = await ConnectJobAsync(f, other, token))
            Assert.StartsWith("<close:PolicyViolation>", await ReceiveTextAsync(ws));

        await using var scope = f.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        var denied = Assert.Single(await db.AuditLogs.Where(a => a.Action == "template.console.denied").ToListAsync(Ct));
        Assert.Equal($"job={other}; token was issued for another job", denied.Details);
        Assert.DoesNotContain(await db.AuditLogs.ToListAsync(Ct), a => a.Details?.Contains(token) == true);
    }

    [Fact]
    public async Task JobTunnel_IsRefusedOutsideConfiguring_Audited_AndLeavesLastConsoleAtAlone()
    {
        var f = Factory(1);
        var id = await CreateJobAsync(f, TemplateJobState.Configuring);
        using var admin = f.CreateClient(UserRole.Admin, "admin");
        var token = await GetJobTokenAsync(admin, id);
        await SetJobStateAsync(f, id, TemplateJobState.Sealing); // the token was valid when issued

        using (var ws = await ConnectJobAsync(f, id, token))
            Assert.StartsWith("<close:PolicyViolation>", await ReceiveTextAsync(ws));

        Assert.Null(await LastConsoleAtAsync(f, id));
        await using var scope = f.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        var denied = Assert.Single(await db.AuditLogs.Where(a => a.Action == "template.console.denied").ToListAsync(Ct));
        Assert.Equal($"job={id}; job is not configuring", denied.Details);
        Assert.Equal(SeededTemplate, denied.Target);
    }

    [Theory]
    [InlineData("seal")]
    [InlineData("cancel")]
    public async Task JobTunnel_IsRefusedAfterASealOrCancelRequest(string request)
    {
        var f = Factory(1);
        var id = await CreateJobAsync(f, TemplateJobState.Configuring);
        using var admin = f.CreateClient(UserRole.Admin, "admin");
        var token = await GetJobTokenAsync(admin, id); // valid when issued
        Assert.Equal(HttpStatusCode.Accepted, (await admin.PostAsync($"/api/admin/template-jobs/{id}/{request}", null, Ct)).StatusCode);

        using (var ws = await ConnectJobAsync(f, id, token))
            Assert.StartsWith("<close:PolicyViolation>", await ReceiveTextAsync(ws));

        Assert.Null(await LastConsoleAtAsync(f, id));
        Assert.Equal(0, f.Services.GetRequiredService<Maldex.Orchestrator.Templates.JobConsoleRegistry>().OpenCount(id));
    }

    [Fact]
    public async Task JobTunnel_OpenWhenASealIsRequested_IsClosedAtOnce_AndNoMoreInputReachesTheGuest()
    {
        using var guacd = new FakeGuacd();
        var f = Factory(guacd.Port);
        var id = await CreateJobAsync(f, TemplateJobState.Configuring);
        using var admin = f.CreateClient(UserRole.Admin, "admin");
        using var ws = await ConnectJobAsync(f, id, await GetJobTokenAsync(admin, id));
        await ReceiveTextAsync(ws);
        await ReceiveTextAsync(ws);
        var registry = f.Services.GetRequiredService<Maldex.Orchestrator.Templates.JobConsoleRegistry>();
        Assert.Equal(1, registry.OpenCount(id));
        await WaitForAsync(() => Task.FromResult(guacd.Received.Count >= 6), "the handshake");
        var received = guacd.Received.Count;

        // What the coordinator does once a seal is requested, before it isolates the guest: no waiting for the 2 s poll.
        Assert.True(await registry.CloseAsync(id, TimeSpan.FromSeconds(10), Ct));
        try { await SendTextAsync(ws, GuacamoleProtocol.Encode("key", "65", "1")); } catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException) { }

        Assert.Equal(0, registry.OpenCount(id));
        Assert.StartsWith("<close:", await ReceiveTextAsync(ws));
        await Task.Delay(200, Ct);
        Assert.Equal(received, guacd.Received.Count); // the key press never reached guacd
    }

    [Fact]
    public async Task JobTunnel_KeepAlive_PublishesTheMovedIdleDeadline()
    {
        using var guacd = new FakeGuacd();
        var clock = new FakeTimeProvider(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var publisher = new CollectingPublisher();
        var f = new TunnelFactory(sql, guacd.Port, new VncEndpoint("127.0.0.1", 5901, VncPassword), clock: clock, publisher: publisher);
        _disposables.Add(f);
        var since = clock.GetUtcNow();
        var id = await CreateJobAsync(f, TemplateJobState.Configuring, since);
        using var admin = f.CreateClient(UserRole.Admin, "admin");
        using var ws = await ConnectJobAsync(f, id, await GetJobTokenAsync(admin, id));
        await ReceiveTextAsync(ws);
        await ReceiveTextAsync(ws);
        await WaitForAsync(() => Task.FromResult(publisher.Count > 0), "the push for the connect");
        Assert.Equal(since + TimeSpan.FromHours(2), publisher.Last!.IdleDeadline);

        for (var i = 0; i < 100 && publisher.Last!.IdleDeadline == since + TimeSpan.FromHours(2); i++)
        {
            clock.Advance(TimeSpan.FromSeconds(61));
            await Task.Delay(30, Ct);
        }

        Assert.True(publisher.Last!.IdleDeadline > since + TimeSpan.FromHours(2));
        Assert.Equal(id, publisher.Last.Id);
        Assert.Equal("Configuring", publisher.Last.State);
    }

    private sealed class CollectingPublisher : Maldex.Orchestrator.Templates.ITemplateJobPublisher
    {
        private readonly object _gate = new();
        private readonly List<Maldex.Orchestrator.Templates.TemplateJobDto> _all = [];
        public int Count { get { lock (_gate) return _all.Count; } }
        public Maldex.Orchestrator.Templates.TemplateJobDto? Last { get { lock (_gate) return _all.LastOrDefault(); } }
        public Task PublishAsync(Maldex.Orchestrator.Templates.TemplateJobDto job, CancellationToken ct)
        {
            lock (_gate) _all.Add(job);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task JobTunnel_ClosesWhenTheJobLeavesConfiguring()
    {
        using var guacd = new FakeGuacd();
        var f = Factory(guacd.Port);
        var id = await CreateJobAsync(f, TemplateJobState.Configuring);
        using var admin = f.CreateClient(UserRole.Admin, "admin");
        using var ws = await ConnectJobAsync(f, id, await GetJobTokenAsync(admin, id));
        await ReceiveTextAsync(ws);
        await ReceiveTextAsync(ws);

        await SetJobStateAsync(f, id, TemplateJobState.Sealing);

        var close = await ReceiveTextAsync(ws);
        Assert.StartsWith("<close:", close);
        Assert.DoesNotContain("PolicyViolation", close);
    }

    [Fact]
    public async Task JobTunnel_KeepsTheJobInUse_WhileOpen_AndOnceMoreOnDisconnect()
    {
        // Held open past the 2 h idle timeout (fake clock): LastConsoleAt keeps moving, so the coordinator's rule
        // max(ConfiguringSince, LastConsoleAt) + timeout never discards a job an admin is working in.
        using var guacd = new FakeGuacd();
        var clock = new FakeTimeProvider(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var f = new TunnelFactory(sql, guacd.Port, new VncEndpoint("127.0.0.1", 5901, VncPassword), clock: clock);
        _disposables.Add(f);
        var since = clock.GetUtcNow();
        var id = await CreateJobAsync(f, TemplateJobState.Configuring, since);
        using var admin = f.CreateClient(UserRole.Admin, "admin");
        using var ws = await ConnectJobAsync(f, id, await GetJobTokenAsync(admin, id));
        await ReceiveTextAsync(ws);
        await ReceiveTextAsync(ws);
        Assert.Equal(since, await LastConsoleAtAsync(f, id));

        var timeout = TimeSpan.FromHours(2);
        for (var i = 0; i < 130 && !(await LastConsoleAtAsync(f, id) > since + timeout); i++)
        {
            clock.Advance(TimeSpan.FromSeconds(61));
            await Task.Delay(30, Ct);
        }
        await WaitForAsync(async () => await LastConsoleAtAsync(f, id) > since + timeout, "a keep-alive after the idle timeout");

        var dto = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/template-jobs/{id}", Ct);
        Assert.Equal("Configuring", dto.GetProperty("state").GetString());
        Assert.True(dto.GetProperty("idleDeadline").GetDateTimeOffset() > clock.GetUtcNow());
    }

    [Fact]
    public async Task JobTunnel_MarksTheJobOnceMore_WhenTheConsoleCloses()
    {
        // The keep-alive is a day apart here, so only the disconnect can move LastConsoleAt after the connect.
        using var guacd = new FakeGuacd();
        var clock = new FakeTimeProvider(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var f = new TunnelFactory(sql, guacd.Port, new VncEndpoint("127.0.0.1", 5901, VncPassword), clock: clock, keepAlive: "1.00:00:00");
        _disposables.Add(f);
        var connectedAt = clock.GetUtcNow();
        var id = await CreateJobAsync(f, TemplateJobState.Configuring, connectedAt);
        using var admin = f.CreateClient(UserRole.Admin, "admin");
        using var ws = await ConnectJobAsync(f, id, await GetJobTokenAsync(admin, id));
        await ReceiveTextAsync(ws);
        await ReceiveTextAsync(ws);
        Assert.Equal(connectedAt, await LastConsoleAtAsync(f, id));

        clock.Advance(TimeSpan.FromMinutes(7));
        var closedAt = clock.GetUtcNow();
        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", Ct);

        await WaitForAsync(async () => await LastConsoleAtAsync(f, id) == closedAt, "the disconnect touch");
    }
}
