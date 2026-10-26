using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Maldex.Core.Domain;
using Maldex.Core.Live;
using Maldex.Data;
using Maldex.Integration.Tests.Fixtures;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Maldex.Integration.Tests.Web;

/// <summary>
/// End-to-end tests of the real-time surface: the SignalR hub, the real <c>SignalRTaskEventPublisher</c>, the
/// orchestrator worker (Fake mode, real time) and the extend/stop/backlog endpoints. Every test boots its own app.
/// </summary>
[Collection("sql")]
public sealed class LiveTests(SqlServerFixture sql) : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(45);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly List<MaldexAppFactory> _factories = [];
    private readonly List<HubConnection> _connections = [];
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var c in _connections) c.DisposeAsync().AsTask().GetAwaiter().GetResult();
        foreach (var f in _factories) f.Dispose();
        foreach (var d in _tempDirs) try { Directory.Delete(d, recursive: true); } catch (IOException) { }
    }

    /// <summary>Workers on, real time, replay at speed 1. <paramref name="holdOpen"/> keeps the fake agent connected until stopped.</summary>
    private MaldexAppFactory Factory(bool holdOpen, bool workers = true, Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Maldex:Orchestrator:MinFreeDiskMiB"] = "1",
            ["Maldex:Fake:Replay:Speed"] = "1",
            ["Maldex:Fake:Replay:HoldOpen"] = holdOpen ? "true" : "false",
        };
        if (extra is not null) foreach (var (k, v) in extra) settings[k] = v;
        var f = new MaldexAppFactory(sql, workers: workers, settings: settings);
        _factories.Add(f);
        return f;
    }

    private static async Task<Guid> SubmitUrlTaskAsync(MaldexAppFactory f, int? durationSec = null)
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
        if (durationSec is not null) form.Add(new StringContent(durationSec.Value.ToString()), "durationSec");
        var resp = await analyst.PostAsync("/api/tasks", form, Ct);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> GetTaskAsync(HttpClient client, Guid id) =>
        await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{id}", Ct);

    private static async Task<JsonElement> WaitForStatusAsync(HttpClient client, Guid id, string status)
    {
        var until = DateTimeOffset.UtcNow + Patience;
        while (true)
        {
            var t = await GetTaskAsync(client, id);
            if (t.GetProperty("status").GetString() == status) return t;
            Assert.True(DateTimeOffset.UtcNow < until, $"task {id} never reached {status} (now {t.GetProperty("status").GetString()})");
            await Task.Delay(50, Ct);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var until = DateTimeOffset.UtcNow + Patience;
        while (!condition())
        {
            Assert.True(DateTimeOffset.UtcNow < until, "timed out waiting for " + what);
            await Task.Delay(25, Ct);
        }
    }

    private HubConnection Connect(MaldexAppFactory f, string user = "live-analyst")
    {
        var server = f.Server;
        void Headers(IDictionary<string, string> h)
        {
            h["X-Dev-Role"] = "Analyst";
            h["X-Dev-User"] = user;
            h["X-Maldex-Csrf"] = "1"; // the negotiate POST, as the SPA's SignalR client sends it
        }

        var conn = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, "/hubs/tasks"), o =>
            {
                o.Transports = HttpTransportType.WebSockets;
                o.HttpMessageHandlerFactory = _ => server.CreateHandler();
                Headers(o.Headers);
                o.WebSocketFactory = async (ctx, ct) =>
                {
                    var ws = server.CreateWebSocketClient();
                    ws.ConfigureRequest = r =>
                    {
                        r.Headers["X-Dev-Role"] = "Analyst";
                        r.Headers["X-Dev-User"] = user;
                    };
                    return await ws.ConnectAsync(ctx.Uri, ct);
                };
            })
            .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();
        _connections.Add(conn);
        return conn;
    }

    /// <summary>Records everything a hub connection pushes, with the time each callback fired.</summary>
    private sealed class Collector
    {
        private readonly object _gate = new();
        private readonly List<TaskStatusUpdate> _statuses = [];
        private readonly List<(LiveEventDto Event, DateTimeOffset ReceivedAt)> _events = [];
        private readonly List<ProcessNodeDto> _processes = [];

        public Collector(HubConnection conn)
        {
            conn.On<TaskStatusUpdate>("status", s => { lock (_gate) _statuses.Add(s); });
            conn.On<LiveEventDto[]>("events", batch =>
            {
                var now = DateTimeOffset.UtcNow;
                lock (_gate) foreach (var e in batch) _events.Add((e, now));
            });
            conn.On<ProcessNodeDto>("process", p => { lock (_gate) _processes.Add(p); });
        }

        public List<TaskStatusUpdate> Statuses() { lock (_gate) return [.. _statuses]; }
        public List<(LiveEventDto Event, DateTimeOffset ReceivedAt)> Events() { lock (_gate) return [.. _events]; }
        public List<ProcessNodeDto> Processes() { lock (_gate) return [.. _processes]; }
        public bool Saw(RunStatus s) { lock (_gate) return _statuses.Any(x => x.Status == s); }
    }

    private static async Task<List<AuditLog>> AuditAsync(MaldexAppFactory f, string action)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        return await db.AuditLogs.AsNoTracking().Where(a => a.Action == action).ToListAsync(Ct);
    }

    // ---- live push ----

    [Fact]
    public async Task LiveEvents_ArriveWithin2s_OfIngest()
    {
        var f = Factory(holdOpen: false);
        var conn = Connect(f);
        var live = new Collector(conn);
        await conn.StartAsync(Ct);

        var id = await SubmitUrlTaskAsync(f);
        await conn.InvokeAsync("JoinTask", id, Ct);

        await WaitUntilAsync(() => live.Saw(RunStatus.Processing), "the run to reach Processing");

        // The default replay produces four behavior events (two processes, a file write, a DNS lookup).
        var events = live.Events();
        Assert.True(events.Count >= 4, $"expected at least 4 live events, got {events.Count}");
        // LiveEventDto.At is stamped when the (fake) agent emits the event, i.e. right before ingest; receipt is when the client callback fired.
        foreach (var (e, receivedAt) in events)
            Assert.True(receivedAt - e.At < TimeSpan.FromSeconds(2), $"event {e.Id} took {receivedAt - e.At} to arrive");
        Assert.Equal(events.Select(x => x.Event.Id).Order().ToList(), events.Select(x => x.Event.Id).ToList());
        Assert.NotEmpty(live.Processes());
    }

    [Fact]
    public async Task Status_PushedOnEachTransition()
    {
        var f = Factory(holdOpen: false);
        var conn = Connect(f);
        var live = new Collector(conn);
        await conn.StartAsync(Ct);

        var id = await SubmitUrlTaskAsync(f);
        await conn.InvokeAsync("JoinTask", id, Ct);
        await WaitUntilAsync(() => live.Saw(RunStatus.Processing), "the run to reach Processing");

        // The coordinator publishes every transition it makes (the worker's Queued -> Preparing claim happens before any client could be subscribed).
        var pushed = live.Statuses();
        Assert.All(pushed, s => Assert.Equal(id, s.TaskId));
        Assert.Equal([RunStatus.Running, RunStatus.Collecting, RunStatus.Processing], pushed.Select(s => s.Status).ToList());
        Assert.NotNull(pushed[0].DeadlineAt);

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        var history = await db.TaskStatusHistory.AsNoTracking().Where(h => h.TaskId == id).OrderBy(h => h.At).ThenBy(h => h.Id).ToListAsync(Ct);
        var recorded = history.Select(h => h.To).Where(s => s is RunStatus.Running or RunStatus.Collecting or RunStatus.Processing).ToList();
        Assert.Equal(recorded, pushed.Select(s => s.Status).ToList());
    }

    [Fact]
    public async Task JoinTask_AuditsSessionJoin_AndRejectsUnknownTask()
    {
        var f = Factory(holdOpen: true, workers: false);
        var id = await SubmitUrlTaskAsync(f);
        var conn = Connect(f, "joiner");
        await conn.StartAsync(Ct);

        await conn.InvokeAsync("JoinTask", id, Ct);
        await conn.InvokeAsync("LeaveTask", id, Ct);

        var audit = Assert.Single(await AuditAsync(f, "session.join"));
        Assert.Equal(id.ToString(), audit.Target);
        Assert.NotNull(audit.UserId);

        await Assert.ThrowsAsync<HubException>(() => conn.InvokeAsync("JoinTask", Guid.NewGuid(), Ct));
        Assert.Single(await AuditAsync(f, "session.join"));
    }

    // ---- extend / stop ----

    [Fact]
    public async Task Extend_MovesDeadline_CappedAt1800()
    {
        var f = Factory(holdOpen: true);
        var id = await SubmitUrlTaskAsync(f); // default 300 s
        using var client = f.CreateClient(UserRole.Analyst, "analyst");
        var running = await WaitForStatusAsync(client, id, "Running");
        var startedAt = running.GetProperty("startedAt").GetDateTimeOffset();
        var deadline0 = running.GetProperty("deadlineAt").GetDateTimeOffset();
        Assert.Equal(startedAt.AddSeconds(300), deadline0);

        var r1 = await client.PostAsJsonAsync($"/api/tasks/{id}/extend", new { seconds = 120 }, Ct);
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        Assert.Equal(deadline0.AddSeconds(120), (await r1.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("deadlineAt").GetDateTimeOffset());
        Assert.Equal(deadline0.AddSeconds(120), (await GetTaskAsync(client, id)).GetProperty("deadlineAt").GetDateTimeOffset());

        // Far more than is left: clamped to StartedAt + 1800 s.
        var r2 = await client.PostAsJsonAsync($"/api/tasks/{id}/extend", new { seconds = 1800 }, Ct);
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        Assert.Equal(startedAt.AddSeconds(1800), (await GetTaskAsync(client, id)).GetProperty("deadlineAt").GetDateTimeOffset());

        // Already at the maximum.
        var r3 = await client.PostAsJsonAsync($"/api/tasks/{id}/extend", new { seconds = 10 }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, r3.StatusCode);
        Assert.Equal(startedAt.AddSeconds(1800), (await GetTaskAsync(client, id)).GetProperty("deadlineAt").GetDateTimeOffset());

        // Out-of-range requests are rejected before anything changes.
        foreach (var bad in new[] { 0, -5, 1801 })
        {
            var r = await client.PostAsJsonAsync($"/api/tasks/{id}/extend", new { seconds = bad }, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.True((await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors").TryGetProperty("seconds", out _));
        }

        Assert.Equal(2, (await AuditAsync(f, "task.extend")).Count);
        (await client.PostAsync($"/api/tasks/{id}/stop", null, Ct)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Extend_NotRunning_409()
    {
        var f = Factory(holdOpen: true, workers: false);
        var id = await SubmitUrlTaskAsync(f); // stays Queued
        using var client = f.CreateClient(UserRole.Analyst, "analyst");

        var resp = await client.PostAsJsonAsync($"/api/tasks/{id}/extend", new { seconds = 60 }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Empty(await AuditAsync(f, "task.extend"));

        var stop = await client.PostAsync($"/api/tasks/{id}/stop", null, Ct);
        Assert.Equal(HttpStatusCode.Conflict, stop.StatusCode);

        var unknown = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/tasks/{unknown}/extend", new { seconds = 60 }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/tasks/{unknown}/stop", null, Ct)).StatusCode);
    }

    [Fact]
    public async Task Extend_AfterRunEnded_409()
    {
        var f = Factory(holdOpen: false);
        var id = await SubmitUrlTaskAsync(f);
        using var client = f.CreateClient(UserRole.Analyst, "analyst");
        await WaitForStatusAsync(client, id, "Processing");

        var resp = await client.PostAsJsonAsync($"/api/tasks/{id}/extend", new { seconds = 60 }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task Stop_EndsRun_ToProcessing()
    {
        var f = Factory(holdOpen: true);
        var conn = Connect(f);
        var live = new Collector(conn);
        await conn.StartAsync(Ct);
        var id = await SubmitUrlTaskAsync(f);
        await conn.InvokeAsync("JoinTask", id, Ct);

        using var client = f.CreateClient(UserRole.Analyst, "analyst");
        await WaitForStatusAsync(client, id, "Running");

        var stop = await client.PostAsync($"/api/tasks/{id}/stop", null, Ct);
        Assert.Equal(HttpStatusCode.Accepted, stop.StatusCode);

        var done = await WaitForStatusAsync(client, id, "Processing");
        Assert.Equal(JsonValueKind.Null, done.GetProperty("failureReason").ValueKind);

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        var row = await db.Tasks.AsNoTracking().Include(t => t.StatusHistory).SingleAsync(t => t.Id == id, Ct);
        Assert.NotNull(row.StopRequestedAt);
        Assert.Contains(row.StatusHistory, h => h.To == RunStatus.Collecting && h.Reason == "stopped by analyst");

        Assert.Equal(id.ToString(), Assert.Single(await AuditAsync(f, "task.stop")).Target);
        await WaitUntilAsync(() => live.Saw(RunStatus.Processing), "the Processing push");
        Assert.Equal([RunStatus.Running, RunStatus.Collecting, RunStatus.Processing], live.Statuses().Select(s => s.Status).ToList());

        // Once the run has left Running, a further stop is a conflict.
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/tasks/{id}/stop", null, Ct)).StatusCode);
    }

    // ---- backlog ----

    [Fact]
    public async Task Backlog_Endpoints_ReturnProcessesAndFilteredEvents()
    {
        var f = Factory(holdOpen: false);
        var id = await SubmitUrlTaskAsync(f);
        using var client = f.CreateClient(UserRole.Analyst, "analyst");
        await WaitForStatusAsync(client, id, "Processing");

        var processes = (await client.GetFromJsonAsync<ProcessNodeDto[]>($"/api/tasks/{id}/processes", Json, Ct))!;
        Assert.Equal(2, processes.Length);
        var explorer = Assert.Single(processes, p => p.ImagePath.EndsWith("explorer.exe"));
        Assert.Equal(explorer.Id, Assert.Single(processes, p => p.ImagePath.EndsWith("notepad.exe")).ParentId);

        var all = (await client.GetFromJsonAsync<LiveEventDto[]>($"/api/tasks/{id}/events", Json, Ct))!;
        Assert.Equal(4, all.Length);
        Assert.Equal(all.Select(e => e.Id).Order().ToList(), all.Select(e => e.Id).ToList());

        var dns = (await client.GetFromJsonAsync<LiveEventDto[]>($"/api/tasks/{id}/events?type=Dns", Json, Ct))!;
        Assert.Equal(EventType.Dns, Assert.Single(dns).Type);

        var page1 = (await client.GetFromJsonAsync<LiveEventDto[]>($"/api/tasks/{id}/events?take=3", Json, Ct))!;
        Assert.Equal(all.Take(3).Select(e => e.Id), page1.Select(e => e.Id));
        var page2 = (await client.GetFromJsonAsync<LiveEventDto[]>($"/api/tasks/{id}/events?afterId={page1[^1].Id}&take=3", Json, Ct))!;
        Assert.Equal(all.Skip(3).Select(e => e.Id), page2.Select(e => e.Id));

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/tasks/{id}/events?type=Bogus", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/tasks/{id}/events?afterId=-1", Ct)).StatusCode);

        var unknown = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/tasks/{unknown}/processes", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/tasks/{unknown}/events", Ct)).StatusCode);
    }

    // ---- Review Focus 4 ----

    /// <summary>A client that follows the documented rejoin protocol (see TaskHub.cs).</summary>
    private sealed class RejoiningClient
    {
        private readonly object _gate = new();
        private readonly List<LiveEventDto> _buffer = [];
        private bool _syncing = true;
        private long _highest;

        public List<long> AppliedEventIds { get; } = [];
        public Dictionary<long, ProcessNodeDto> Processes { get; } = [];

        // Step 2: callbacks are registered before the join and buffer until the backlog has been applied.
        public RejoiningClient(HubConnection conn)
        {
            conn.On<LiveEventDto[]>("events", batch =>
            {
                lock (_gate)
                {
                    if (_syncing) _buffer.AddRange(batch);
                    else Apply(batch);
                }
            });
            conn.On<ProcessNodeDto>("process", p =>
            {
                lock (_gate) Processes[p.Id] = p; // an upsert: a node is re-sent when it exits
            });
        }

        // Applying skips every id <= the highest already applied, both for the buffer and for later live batches.
        private void Apply(IEnumerable<LiveEventDto> batch)
        {
            foreach (var e in batch)
            {
                if (e.Id <= _highest) continue;
                AppliedEventIds.Add(e.Id);
                _highest = e.Id;
            }
        }

        public void CompleteSync(IReadOnlyList<LiveEventDto> backlogEvents, IReadOnlyList<ProcessNodeDto> backlogProcesses)
        {
            lock (_gate)
            {
                foreach (var p in backlogProcesses) Processes[p.Id] = p;
                Apply(backlogEvents); // step 3 result
                Apply(_buffer);       // step 4
                _buffer.Clear();
                _syncing = false;
            }
        }

        public List<long> Applied() { lock (_gate) return [.. AppliedEventIds]; }
    }

    private string WriteLongReplay(int events, int delayMs)
    {
        var dir = Path.Combine(Path.GetTempPath(), "maldex-replay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        var lines = new List<string>
        {
            """{"delayMs":100,"message":{"type":"ready","agentVersion":"0.1.0-fake","hostname":"MALDEX-WIN10"}}""",
            """{"delayMs":50,"message":{"type":"process_started","at":"2026-01-01T00:00:00+00:00","pid":4120,"parentPid":1,"imagePath":"C:\\Windows\\explorer.exe","commandLine":"explorer.exe","user":"analyst"}}""",
        };
        static string Line(int delayMs, object message) => JsonSerializer.Serialize(new { delayMs, message });
        for (var i = 0; i < events; i++)
        {
            lines.Add(Line(delayMs, new { type = "file", at = "2026-01-01T00:00:00+00:00", pid = 4120, operation = "write", path = $"C:\\Users\\analyst\\f{i}.txt" }));
            if (i % 20 == 10)
                lines.Add(Line(delayMs, new { type = "process_started", at = "2026-01-01T00:00:00+00:00", pid = 9000 + i, parentPid = 4120, imagePath = $"C:\\Temp\\p{i}.exe", commandLine = $"p{i}.exe", user = "analyst" }));
        }
        File.WriteAllLines(Path.Combine(dir, "long.jsonl"), lines);
        return dir;
    }

    [Fact]
    public async Task Rejoin_GetsBacklogThenLive()
    {
        var replayDir = WriteLongReplay(events: 100, delayMs: 40); // about 4 s of steady events
        var f = Factory(holdOpen: false, extra: new()
        {
            ["Maldex:Fake:Replay:Directory"] = replayDir,
            ["Maldex:Fake:Replay:DefaultFile"] = "long.jsonl",
        });

        // First session: join, see some events, then drop off mid-run.
        var first = Connect(f);
        var firstSeen = new Collector(first);
        await first.StartAsync(Ct);
        var id = await SubmitUrlTaskAsync(f);
        await first.InvokeAsync("JoinTask", id, Ct);
        await WaitUntilAsync(() => firstSeen.Events().Count >= 10, "the first session to see events");
        await first.StopAsync(Ct);
        var seenByFirst = firstSeen.Events().Count;

        await Task.Delay(500, Ct); // events the disconnected client misses

        // Second session follows the protocol: join first, buffer, fetch the backlog, apply the buffer.
        var second = Connect(f);
        var secondStatuses = new Collector(second);
        var client2 = new RejoiningClient(second);
        await second.StartAsync(Ct);
        await second.InvokeAsync("JoinTask", id, Ct);

        using var http = f.CreateClient(UserRole.Analyst, "analyst");
        var processes = (await http.GetFromJsonAsync<ProcessNodeDto[]>($"/api/tasks/{id}/processes", Json, Ct))!;
        var backlog = new List<LiveEventDto>();
        while (true) // events?afterId=0, paged
        {
            var after = backlog.Count == 0 ? 0 : backlog[^1].Id;
            var page = (await http.GetFromJsonAsync<LiveEventDto[]>($"/api/tasks/{id}/events?afterId={after}&take=30", Json, Ct))!;
            backlog.AddRange(page);
            if (page.Length < 30) break;
        }
        Assert.True(backlog.Count > seenByFirst, "the backlog must contain events the first session missed");
        client2.CompleteSync(backlog, processes);

        await WaitUntilAsync(() => secondStatuses.Saw(RunStatus.Processing), "the run to finish");

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        var dbEventIds = await db.BehaviorEvents.AsNoTracking().Where(e => e.TaskId == id).OrderBy(e => e.Id).Select(e => e.Id).ToListAsync(Ct);
        var dbNodes = await db.ProcessNodes.AsNoTracking().Where(p => p.TaskId == id).OrderBy(p => p.Id).ToListAsync(Ct);

        var applied = client2.Applied();
        Assert.Equal(dbEventIds.Count, applied.Distinct().Count()); // no duplicates
        Assert.Equal(dbEventIds, applied);                           // no gaps, same order
        Assert.True(dbEventIds.Count >= 100);
        Assert.Equal(dbNodes.Select(n => n.Id), client2.Processes.Keys.Order());
        Assert.All(dbNodes, n => Assert.Equal(n.EndedAt, client2.Processes[n.Id].EndedAt));
    }
}
