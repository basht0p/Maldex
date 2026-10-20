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

    private sealed class TunnelFactory(SqlServerFixture sql, int guacdPort, VncEndpoint? endpoint)
        : MaldexAppFactory(sql, settings: new Dictionary<string, string?>
        {
            ["Maldex:Guacamole:GuacdPort"] = guacdPort.ToString(),
            ["Maldex:Guacamole:StatusPollInterval"] = "00:00:00.100",
        })
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(s => s.AddSingleton<IVmConsoleLocator>(new StubLocator(endpoint)));
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
        public List<string[]> Received { get; } = [];
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

                Received.Add((await reader.ReadAsync(Ct))!); // select
                await Send("args", "hostname", "port", "password", "read-only", "VERSION_1_5_0");
                for (var i = 0; i < 5; i++) Received.Add((await reader.ReadAsync(Ct))!); // size, audio, video, image, connect
                await Send("ready", "guacd-conn-1");
                Connected.SetResult(stream);
                while (await reader.ReadAsync(Ct) is { } more) Received.Add(more);
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

        // A real token used by a different user.
        var token = await GetTokenAsync(analyst, id);
        using (var ws = await ConnectAsync(f, id, token, user: "someone-else"))
        {
            Assert.StartsWith("<close:PolicyViolation>", await ReceiveTextAsync(ws));
        }

        // The token is single use: now burnt even for its owner.
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
    }

    [Fact]
    public async Task Tunnel_RejectsCrossOriginUpgrade()
    {
        var f = Factory(1);
        var id = await CreateTaskAsync(f, RunStatus.Running);
        using var analyst = f.CreateClient(UserRole.Analyst, "analyst");
        var token = await GetTokenAsync(analyst, id);

        var ws = f.Server.CreateWebSocketClient();
        ws.SubProtocols.Add("guacamole");
        ws.ConfigureRequest = r =>
        {
            r.Headers["X-Dev-Role"] = "Analyst";
            r.Headers["X-Dev-User"] = "analyst";
            r.Headers["Origin"] = "https://evil.example";
        };
        await Assert.ThrowsAnyAsync<Exception>(() => ws.ConnectAsync(new Uri(f.Server.BaseAddress, $"/api/tasks/{id}/tunnel?token={token}"), Ct));
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
}
