using System.Text.Json;
using Maldex.Core.Abstractions;
using Maldex.Core.Agent;
using Maldex.Core.Domain;
using Maldex.Core.Live;
using Maldex.Data;
using Maldex.Infrastructure;
using Maldex.Infrastructure.Fakes;
using Maldex.Integration.Tests.Fixtures;
using Maldex.Orchestrator;
using Maldex.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using OrgEntity = Maldex.Core.Domain.Org;

namespace Maldex.Integration.Tests.Orchestrator;

/// <summary>In-memory <see cref="ITaskEventPublisher"/> that records everything it is given.</summary>
internal sealed class RecordingPublisher : ITaskEventPublisher
{
    private readonly object _gate = new();
    private readonly List<TaskStatusUpdate> _statuses = [];
    private readonly List<(Guid TaskId, ProcessNodeDto Process)> _processes = [];
    private readonly List<(Guid TaskId, LiveEventDto Event)> _events = [];

    public IReadOnlyList<TaskStatusUpdate> Statuses { get { lock (_gate) return _statuses.ToArray(); } }
    public IReadOnlyList<(Guid TaskId, ProcessNodeDto Process)> Processes { get { lock (_gate) return _processes.ToArray(); } }
    public IReadOnlyList<(Guid TaskId, LiveEventDto Event)> Events { get { lock (_gate) return _events.ToArray(); } }

    public Task PublishStatusAsync(TaskStatusUpdate update, CancellationToken ct)
    {
        lock (_gate) _statuses.Add(update);
        return Task.CompletedTask;
    }

    public Task PublishProcessAsync(Guid taskId, ProcessNodeDto process, CancellationToken ct)
    {
        lock (_gate) _processes.Add((taskId, process));
        return Task.CompletedTask;
    }

    public Task PublishEventsAsync(Guid taskId, IReadOnlyList<LiveEventDto> events, CancellationToken ct)
    {
        lock (_gate) foreach (var e in events) _events.Add((taskId, e));
        return Task.CompletedTask;
    }
}

/// <summary>Remembers the channels the fake factory hands out so tests can inspect sent commands.</summary>
internal sealed class CapturingChannelFactory(Func<IAgentChannelFactory> inner) : IAgentChannelFactory
{
    public List<ReplayAgentChannel> Channels { get; } = [];

    public async Task<IAgentChannel> ConnectAsync(VmHandle vm, CancellationToken ct)
    {
        var channel = await inner().ConnectAsync(vm, ct);
        if (channel is ReplayAgentChannel replay) lock (Channels) Channels.Add(replay);
        return channel;
    }
}

/// <summary>
/// Fake-mode orchestrator wired to a fresh migrated database, a <see cref="FakeTimeProvider"/> and a
/// <see cref="RecordingPublisher"/>. <see cref="RunAsync"/> pumps fake time so replay delays elapse.
/// </summary>
internal sealed class OrchestratorHarness : IAsyncDisposable
{
    private readonly string _artifactRoot = Path.Combine(Path.GetTempPath(), "maldex-orch-" + Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider _services = null!;
    private readonly string _connectionString;

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
    public RecordingPublisher Publisher { get; } = new();
    public CapturingChannelFactory Channels { get; }

    private OrchestratorHarness(SqlServerFixture sql, IDictionary<string, string?> settings)
    {
        _connectionString = sql.CreateDatabaseConnectionString();
        var values = new Dictionary<string, string?>
        {
            ["Maldex:Mode"] = "Fake",
            ["Maldex:Artifacts:Root"] = _artifactRoot,
            ["Maldex:Fake:Replay:Speed"] = "100",
            ["Maldex:Fake:Replay:HoldOpen"] = "false",
        };
        foreach (var (k, v) in settings) values[k] = v;
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(Time);
        services.AddDbContext<MaldexDbContext>(o => o.UseSqlServer(_connectionString));
        services.AddMaldexInfrastructure(config);
        services.AddSingleton<FakeAgentChannelFactory>();
        Channels = new CapturingChannelFactory(() => _services!.GetRequiredService<FakeAgentChannelFactory>());
        services.Replace(ServiceDescriptor.Singleton<IAgentChannelFactory>(Channels));
        services.AddSingleton<ITaskEventPublisher>(Publisher);
        services.AddMaldexOrchestrator(config);
        services.AddMaldexProcessing(config);
        _services = services.BuildServiceProvider();
    }

    public static async Task<OrchestratorHarness> CreateAsync(SqlServerFixture sql, IDictionary<string, string?>? settings = null)
    {
        var h = new OrchestratorHarness(sql, settings ?? new Dictionary<string, string?>());
        await using var db = h.NewContext();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        return h;
    }

    public T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    public MaldexDbContext NewContext() =>
        new(new DbContextOptionsBuilder<MaldexDbContext>().UseSqlServer(_connectionString).Options);

    /// <summary>Seeds a sample-backed task that the worker would already have claimed (<c>Preparing</c>).</summary>
    public async Task<Guid> SeedPreparingTaskAsync(NetworkMode mode = NetworkMode.Simulated, int durationSec = 300, byte[]? sampleContent = null)
    {
        await using var db = NewContext();
        var sha = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var blobKey = $"samples/{sha[..2]}/{sha[2..4]}/{sha}";
        // A real blob in the artifact store, keyed by its real hash (what the submission endpoint does): the coordinator
        // streams it into the guest, so it has to exist. Unique per call unless the test supplies its own bytes.
        sampleContent ??= System.Text.Encoding.ASCII.GetBytes("benign sample " + Guid.NewGuid());
        {
            await using var content = new MemoryStream(sampleContent, writable: false);
            var blob = await Get<IArtifactStore>().PutAsync("samples", content, 33_554_432, TestContext.Current.CancellationToken);
            sha = blob.Sha256;
            blobKey = blob.Key;
        }
        var org = new OrgEntity { Id = Guid.NewGuid(), Name = "Org-" + Guid.NewGuid().ToString("N"), RetentionDays = 30 };
        var user = new AppUser { Id = Guid.NewGuid(), EntraObjectId = Guid.NewGuid().ToString("N"), DisplayName = "A", Role = UserRole.Analyst };
        var sample = new Sample { Sha256 = sha, Sha1 = new string('b', 40), Md5 = new string('c', 32), Size = 5, BlobKey = blobKey, FirstSeen = Time.GetUtcNow() };
        var task = new AnalysisTask
        {
            Id = Guid.NewGuid(), OrgId = org.Id, SubmittedByUserId = user.Id, SampleSha256 = sha, OriginalFileName = "invoice.exe",
            Image = "win10", NetworkMode = mode, DurationSec = durationSec, CreatedAt = Time.GetUtcNow(),
        };
        task.TransitionTo(RunStatus.Preparing, "picked up", Time.GetUtcNow());
        db.AddRange(org, user, sample, task);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return task.Id;
    }

    /// <summary>Starts the run and keeps advancing fake time until it finishes (or the real-time budget runs out).</summary>
    public async Task RunAsync(Guid taskId, Func<Task>? whilePumping = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var run = Task.Run(() => Get<RunCoordinator>().RunAsync(taskId, ct), ct);
        var script = whilePumping is null ? Task.CompletedTask : Task.Run(whilePumping, ct);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!run.IsCompleted)
        {
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60), "Run did not finish within 60 s of real time.");
            Time.Advance(TimeSpan.FromMilliseconds(50));
            await Task.Delay(1, ct);
        }
        await run;
        await script;
    }

    /// <summary>Polls (in real time) until the task reaches <paramref name="status"/>.</summary>
    public async Task WaitForStatusAsync(Guid taskId, RunStatus status)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            await using var db = NewContext();
            var current = await db.Tasks.AsNoTracking().Where(t => t.Id == taskId).Select(t => t.Status).SingleAsync(TestContext.Current.CancellationToken);
            if (current == status) return;
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"Task never reached {status} (now {current}).");
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        if (Directory.Exists(_artifactRoot))
        {
            try { Directory.Delete(_artifactRoot, recursive: true); } catch (IOException) { }
        }
    }
}

[Collection("sql")]
public class RunCoordinatorTests(SqlServerFixture sql)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<List<TaskStatusHistory>> HistoryAsync(MaldexDbContext db, Guid taskId) =>
        (await db.TaskStatusHistory.Where(h => h.TaskId == taskId).ToListAsync(Ct)).OrderBy(h => h.Id).ToList();

    [Fact]
    public async Task HappyPath_DefaultReplay_EndsInProcessing_WithEvents()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql);
        var id = await h.SeedPreparingTaskAsync();

        await h.RunAsync(id);

        await using var db = h.NewContext();
        var task = await db.Tasks.SingleAsync(t => t.Id == id, Ct);
        Assert.Equal(RunStatus.Processing, task.Status);
        // The worker's claim (Queued -> Preparing) came first; the coordinator did the rest.
        Assert.Equal(
            [RunStatus.Preparing, RunStatus.Running, RunStatus.Collecting, RunStatus.Processing],
            (await HistoryAsync(db, id)).Select(x => x.To).ToList());

        Assert.NotNull(task.StartedAt);
        Assert.Equal(task.StartedAt!.Value.AddSeconds(300), task.DeadlineAt);
        Assert.NotNull(task.EndedAt);

        var nodes = await db.ProcessNodes.Where(n => n.TaskId == id).OrderBy(n => n.Id).ToListAsync(Ct);
        Assert.Equal(2, nodes.Count);
        Assert.EndsWith("explorer.exe", nodes[0].ImagePath);
        Assert.EndsWith("notepad.exe", nodes[1].ImagePath);
        Assert.Equal(nodes[0].Id, nodes[1].ParentNodeId);

        var artifact = Assert.Single(await db.Artifacts.Where(a => a.TaskId == id).ToListAsync(Ct));
        Assert.Equal(ArtifactKind.Screenshot, artifact.Kind);
        Assert.True(h.Get<IArtifactStore>().Exists(artifact.BlobKey));

        // The coordinator told the guest what to open.
        var channel = Assert.Single(h.Channels.Channels);
        // (chunks first, then the open command; Task 12)
        var open = Assert.IsType<OpenSampleCommand>(channel.SentCommands[^1]);
        Assert.IsType<SelfTestCommand>(channel.SentCommands[0]); // the isolation self-test comes first
        Assert.All(channel.SentCommands.Skip(1).SkipLast(1), c => Assert.IsType<PutFileChunkCommand>(c));
        Assert.Equal("invoice.exe", open.FileName);
        Assert.StartsWith("samples/", open.BlobKey);
    }

    [Fact]
    public async Task Deadline_EndsRun()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql, new Dictionary<string, string?> { ["Maldex:Fake:Replay:HoldOpen"] = "true" });
        var id = await h.SeedPreparingTaskAsync(durationSec: 60);

        await h.RunAsync(id, async () =>
        {
            await h.WaitForStatusAsync(id, RunStatus.Running);
            h.Time.Advance(TimeSpan.FromSeconds(61));
        });

        await using var db = h.NewContext();
        Assert.Equal(RunStatus.Processing, (await db.Tasks.SingleAsync(t => t.Id == id, Ct)).Status);
        var collecting = Assert.Single(await HistoryAsync(db, id), x => x.To == RunStatus.Collecting);
        Assert.Equal("deadline reached", collecting.Reason);
    }

    [Fact]
    public async Task StopRequested_EndsRun()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql, new Dictionary<string, string?> { ["Maldex:Fake:Replay:HoldOpen"] = "true" });
        var id = await h.SeedPreparingTaskAsync();

        await h.RunAsync(id, async () =>
        {
            await h.WaitForStatusAsync(id, RunStatus.Running);
            await using var api = h.NewContext();
            var t = await api.Tasks.SingleAsync(x => x.Id == id, Ct);
            t.StopRequestedAt = h.Time.GetUtcNow();
            await api.SaveChangesAsync(Ct);
        });

        await using var db = h.NewContext();
        var task = await db.Tasks.SingleAsync(t => t.Id == id, Ct);
        Assert.Equal(RunStatus.Processing, task.Status);
        Assert.NotNull(task.StopRequestedAt); // the API's write survived the coordinator's later saves
        var collecting = Assert.Single(await HistoryAsync(db, id), x => x.To == RunStatus.Collecting);
        Assert.Equal("stopped by analyst", collecting.Reason);
    }

    [Fact]
    public async Task VmAndEgressReleased_Always()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql);
        var id = await h.SeedPreparingTaskAsync();

        await h.RunAsync(id);

        Assert.Empty(await h.Get<IHypervisor>().ListAsync(Ct));
        Assert.Empty(await h.Get<IEgressRouter>().ListAsync(Ct));
    }

    [Fact]
    public async Task EgressUnavailable_FailsTask_NoVmStarted()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql);
        var id = await h.SeedPreparingTaskAsync(NetworkMode.LiveVpn);

        await h.RunAsync(id);

        await using var db = h.NewContext();
        var task = await db.Tasks.SingleAsync(t => t.Id == id, Ct);
        Assert.Equal(RunStatus.Failed, task.Status);
        Assert.StartsWith("egress unavailable:", task.FailureReason);
        Assert.Empty(h.Channels.Channels);
        Assert.Empty(await h.Get<IHypervisor>().ListAsync(Ct));
        Assert.Empty(await h.Get<IEgressRouter>().ListAsync(Ct));
    }

    [Fact]
    public async Task Publisher_ReceivesStatusProcessesAndEvents()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql);
        var id = await h.SeedPreparingTaskAsync();

        await h.RunAsync(id);

        Assert.Equal(
            [RunStatus.Running, RunStatus.Collecting, RunStatus.Processing],
            h.Publisher.Statuses.Select(s => s.Status).ToList());
        Assert.All(h.Publisher.Statuses, s => Assert.Equal(id, s.TaskId));
        Assert.NotNull(h.Publisher.Statuses[0].DeadlineAt);

        var processes = h.Publisher.Processes.Select(p => p.Process).ToList();
        Assert.Equal(2, processes.Count);
        Assert.Equal(processes[0].Id, processes[1].ParentId);

        var events = h.Publisher.Events.Select(e => e.Event).ToList();
        Assert.Equal([EventType.Process, EventType.Process, EventType.File, EventType.Dns], events.Select(e => e.Type).ToList());
        Assert.Equal("notepad.exe (pid 5012)", events[1].Summary);
        Assert.Equal(processes[1].Id, events[2].ProcessId);
        Assert.Equal("write C:\\Users\\analyst\\Documents\\notes.txt", events[2].Summary);
        Assert.Equal("example.com → 93.184.216.34", events[3].Summary);

        // What was pushed live is exactly what was stored.
        await using var db = h.NewContext();
        var stored = await db.BehaviorEvents.Where(e => e.TaskId == id).OrderBy(e => e.Id).ToListAsync(Ct);
        Assert.Equal(events.Select(e => e.Id), stored.Select(e => e.Id));
        Assert.Equal(events.Select(e => e.DataJson), stored.Select(e => e.Data));
        using var doc = JsonDocument.Parse(events[2].DataJson);
        Assert.Equal("file", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(5012, doc.RootElement.GetProperty("pid").GetInt32());
    }

    [Fact]
    public async Task Ingest_TruncatesAndSanitizesStrings()
    {
        var dir = Directory.CreateTempSubdirectory("maldex-replay-").FullName;
        try
        {
            var hostile = "x\0y" + new string('a', 100_000);
            var lines = new[]
            {
                """{"delayMs":1,"message":{"type":"ready","agentVersion":"1","hostname":"h"}}""",
                JsonSerializer.Serialize(new
                {
                    delayMs = 1,
                    message = new
                    {
                        type = "process_started", at = "2000-01-01T00:00:00Z", pid = 10, parentPid = 1,
                        imagePath = "C:\\a\u0007.exe", commandLine = hostile, user = new string('u', 1000),
                    },
                }),
            };
            await File.WriteAllLinesAsync(Path.Combine(dir, "hostile.jsonl"), lines, Ct);

            await using var h = await OrchestratorHarness.CreateAsync(sql, new Dictionary<string, string?>
            {
                ["Maldex:Fake:Replay:Directory"] = dir,
                ["Maldex:Fake:Replay:DefaultFile"] = "hostile.jsonl",
            });
            var id = await h.SeedPreparingTaskAsync();

            await h.RunAsync(id);

            await using var db = h.NewContext();
            Assert.Equal(RunStatus.Processing, (await db.Tasks.SingleAsync(t => t.Id == id, Ct)).Status);
            var node = await db.ProcessNodes.SingleAsync(n => n.TaskId == id, Ct);
            Assert.DoesNotContain('\0', node.CommandLine);
            Assert.StartsWith("xyaaa", node.CommandLine);
            Assert.Equal(32_768 + 12, node.CommandLine.Length);
            Assert.EndsWith("…[truncated]", node.CommandLine);
            Assert.Equal("C:\\a.exe", node.ImagePath);
            Assert.True(node.User!.Length <= 256);

            var ev = await db.BehaviorEvents.SingleAsync(e => e.TaskId == id, Ct);
            Assert.DoesNotContain("\\u0000", ev.Data);
            using var doc = JsonDocument.Parse(ev.Data);
            Assert.Equal(32_768 + 12, doc.RootElement.GetProperty("commandLine").GetString()!.Length);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
