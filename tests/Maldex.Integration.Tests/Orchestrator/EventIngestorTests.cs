using System.Text.Json;
using Maldex.Core.Abstractions;
using Maldex.Core.Agent;
using Maldex.Core.Domain;
using Maldex.Integration.Tests.Fixtures;
using Maldex.Orchestrator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Maldex.Integration.Tests.Orchestrator;

[Collection("sql")]
public class EventIngestorTests(SqlServerFixture sql)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset T0 = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private async Task<(OrchestratorHarness H, Guid TaskId, EventIngestor Ingestor, Maldex.Data.MaldexDbContext Db)> NewAsync()
    {
        var h = await OrchestratorHarness.CreateAsync(sql);
        var id = await h.SeedPreparingTaskAsync();
        var db = h.NewContext();
        var ingestor = new EventIngestor(id, db, h.Get<IArtifactStore>(), h.Publisher, new FakeTimeProvider(T0));
        return (h, id, ingestor, db);
    }

    [Fact]
    public async Task Summaries_UseTheDocumentedFormats()
    {
        var (h, id, ingestor, db) = await NewAsync();
        await using var _ = h;
        await using var __ = db;

        ingestor.Add(new ProcessStartedMessage(T0, 100, 1, "C:\\Windows\\System32\\cmd.exe", "cmd.exe /c x"));
        ingestor.Add(new RegistryEventMessage(T0, 100, "set", "HKCU\\Software\\Run", "Updater", "evil.exe"));
        ingestor.Add(new RegistryEventMessage(T0, 100, "create", "HKCU\\Software\\New"));
        ingestor.Add(new NetworkEventMessage(T0, 100, "tcp", "203.0.113.9", 443));
        ingestor.Add(new DnsEventMessage(T0, 100, "evil.test", "A", ["1.2.3.4", "5.6.7.8"]));
        ingestor.Add(new DnsEventMessage(T0, 100, "nx.test"));
        ingestor.Add(new FileEventMessage(T0, 100, "rename", "C:\\a", "C:\\b"));
        await ingestor.FlushAsync(Ct);

        var events = await db.BehaviorEvents.AsNoTracking().Where(e => e.TaskId == id).OrderBy(e => e.Id).ToListAsync(Ct);
        Assert.Equal(
            [
                "cmd.exe (pid 100)",
                "set HKCU\\Software\\Run\\Updater",
                "create HKCU\\Software\\New",
                "tcp 203.0.113.9:443",
                "evil.test → 1.2.3.4, 5.6.7.8",
                "nx.test",
                "rename C:\\a",
            ],
            events.Select(e => e.Summary));
        Assert.Equal(
            [EventType.Process, EventType.Registry, EventType.Registry, EventType.Network, EventType.Dns, EventType.Dns, EventType.File],
            events.Select(e => e.Type));
    }

    [Fact]
    public async Task ProcessTree_ExitAndEventAttribution_AcrossFlushes()
    {
        var (h, id, ingestor, db) = await NewAsync();
        await using var _ = h;
        await using var __ = db;

        // Parent and child in the same flush: the child's parent id must resolve before the parent's insert is visible.
        ingestor.Add(new ProcessStartedMessage(T0, 10, 1, "C:\\p.exe", "p.exe"));
        ingestor.Add(new ProcessStartedMessage(T0, 20, 10, "C:\\c.exe", "c.exe"));
        await ingestor.FlushAsync(Ct);
        ingestor.Add(new FileEventMessage(T0, 20, "write", "C:\\x"));
        ingestor.Add(new FileEventMessage(T0, 999, "write", "C:\\unattributed"));
        ingestor.Add(new ProcessExitedMessage(T0.AddSeconds(5), 20, 3));
        await ingestor.FlushAsync(Ct);

        var nodes = await db.ProcessNodes.AsNoTracking().Where(n => n.TaskId == id).OrderBy(n => n.Id).ToListAsync(Ct);
        Assert.Null(nodes[0].ParentNodeId);
        Assert.Equal(nodes[0].Id, nodes[1].ParentNodeId);
        Assert.Equal(T0.AddSeconds(5), nodes[1].EndedAt);
        Assert.Equal(3, nodes[1].ExitCode);

        var files = await db.BehaviorEvents.AsNoTracking().Where(e => e.TaskId == id && e.Type == EventType.File).OrderBy(e => e.Id).ToListAsync(Ct);
        Assert.Equal(nodes[1].Id, files[0].ProcessNodeId);
        Assert.Null(files[1].ProcessNodeId);

        // The exit was pushed as an updated process node.
        Assert.Contains(h.Publisher.Processes, p => p.Process.Pid == 20 && p.Process.EndedAt == T0.AddSeconds(5));
    }

    [Fact]
    public async Task Data_IsTheSerializedAgentMessage_WithCleanedFields()
    {
        var (h, id, ingestor, db) = await NewAsync();
        await using var _ = h;
        await using var __ = db;

        ingestor.Add(new DnsEventMessage(T0, 7, "a\0b.test", "A", ["1\u00072.3.4.5"]));
        await ingestor.FlushAsync(Ct);

        var e = await db.BehaviorEvents.AsNoTracking().SingleAsync(x => x.TaskId == id, Ct);
        var message = JsonSerializer.Deserialize<AgentMessage>(e.Data, AgentJson.Options);
        var dns = Assert.IsType<DnsEventMessage>(message);
        Assert.Equal("ab.test", dns.Query);
        Assert.Equal(["12.3.4.5"], dns.Answers!);
        Assert.Equal("ab.test → 12.3.4.5", e.Summary);
    }

    [Fact]
    public async Task Screenshot_IsStoredAsArtifact_AndNotAnEvent()
    {
        var (h, id, ingestor, db) = await NewAsync();
        await using var _ = h;
        await using var __ = db;
        var bytes = new byte[] { 0xFF, 0xD8, 1, 2, 3, 0xFF, 0xD9 };

        ingestor.Add(new ScreenshotMessage(T0, "jpeg", Convert.ToBase64String(bytes)));
        ingestor.Add(new ScreenshotMessage(T0, "jpeg", "!!not base64!!"));
        await ingestor.FlushAsync(Ct);

        var artifact = Assert.Single(await db.Artifacts.AsNoTracking().Where(a => a.TaskId == id).ToListAsync(Ct));
        Assert.Equal(bytes.Length, artifact.Size);
        Assert.StartsWith("screenshots/", artifact.BlobKey);
        await using var stream = await h.Get<IArtifactStore>().OpenReadAsync(artifact.BlobKey, Ct);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, Ct);
        Assert.Equal(bytes, ms.ToArray());
        Assert.Empty(await db.BehaviorEvents.Where(e => e.TaskId == id).ToListAsync(Ct));
    }
}
