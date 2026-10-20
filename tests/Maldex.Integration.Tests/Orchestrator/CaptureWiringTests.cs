using System.Security.Cryptography;
using Maldex.Core.Abstractions;
using Maldex.Core.Agent;
using Maldex.Core.Domain;
using Maldex.Integration.Tests.Fixtures;
using Maldex.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Maldex.Integration.Tests.Orchestrator;

/// <summary>Records when the coordinator starts/stops the capture and what the rest of the run looked like at that moment.</summary>
internal sealed class RecordingCapture(Func<IServiceProvider> services) : ICaptureService

{
    public Func<IServiceProvider> Services { get; } = services;
    public string Dir { get; } = Directory.CreateTempSubdirectory("maldex-cap-test-").FullName;
    public Exception? StartFails { get; set; }
    public Exception? StopFails { get; set; }
    public bool Disposed { get; private set; }
    public bool? OpenSentAtStart;
    public int VmsAtStop = -1;
    public int LeasesAtStop = -1;
    public List<string> Order { get; } = [];

    public async Task<ICaptureHandle> StartAsync(NetworkLease lease, CancellationToken ct)
    {
        Order.Add("start");
        var channels = ((CapturingChannelFactory)Services.Invoke().GetRequiredService<IAgentChannelFactory>()).Channels;
        OpenSentAtStart = channels.SelectMany(c => c.SentCommands).Any(c => c is OpenSampleCommand or OpenUrlCommand);
        if (StartFails is not null) throw StartFails;
        await Task.CompletedTask;
        return new Handle(this);
    }

    private sealed class Handle(RecordingCapture owner) : ICaptureHandle
    {
        public async Task<IReadOnlyList<CapturedFile>> StopAsync(CancellationToken ct)
        {
            owner.Order.Add("stop");
            var sp = owner.Services();
            owner.VmsAtStop = (await sp.GetRequiredService<IHypervisor>().ListAsync(ct)).Count;
            owner.LeasesAtStop = (await sp.GetRequiredService<IEgressRouter>().ListAsync(ct)).Count;
            if (owner.StopFails is not null) throw owner.StopFails;
            var pcap = Path.Combine(owner.Dir, "capture.pcap");
            var flows = Path.Combine(owner.Dir, "flows.jsonl");
            await File.WriteAllBytesAsync(pcap, [.. Enumerable.Range(0, 100).Select(i => (byte)i)], ct);
            await File.WriteAllTextAsync(flows, "{\"method\":\"GET\",\"url\":\"http://203.0.113.5/\",\"host\":\"203.0.113.5\",\"status\":200}\n", ct);
            return [new CapturedFile(ArtifactKind.Pcap, pcap), new CapturedFile(ArtifactKind.MitmFlows, flows)];
        }

        public ValueTask DisposeAsync()
        {
            owner.Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}

[Collection("sql")]
public class CaptureWiringTests(SqlServerFixture sql)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(OrchestratorHarness H, RecordingCapture Cap)> HarnessAsync(SqlServerFixture sql)
    {
        RecordingCapture? cap = null;
        OrchestratorHarness? h = null;
        h = await OrchestratorHarness.CreateAsync(sql, null, services =>
        {
            cap = new RecordingCapture(() => h!.Services);
            services.Replace(ServiceDescriptor.Singleton<ICaptureService>(cap));
        });
        return (h, cap!);
    }

    [Fact]
    public async Task Coordinator_StoresPcapAndFlowsArtifacts()
    {
        var (h, cap) = await HarnessAsync(sql);
        await using var _ = h;
        var id = await h.SeedPreparingTaskAsync();

        await h.RunAsync(id);

        await using var db = h.NewContext();
        var task = await db.Tasks.SingleAsync(t => t.Id == id, Ct);
        Assert.Equal(RunStatus.Processing, task.Status);
        var artifacts = await db.Artifacts.Where(a => a.TaskId == id && (a.Kind == ArtifactKind.Pcap || a.Kind == ArtifactKind.MitmFlows)).ToListAsync(Ct);
        Assert.Equal([ArtifactKind.Pcap, ArtifactKind.MitmFlows], artifacts.OrderBy(a => a.Kind).Select(a => a.Kind));

        var pcap = artifacts.Single(a => a.Kind == ArtifactKind.Pcap);
        var expected = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(expected)), pcap.Sha256);
        Assert.Equal(100, pcap.Size);
        await using (var stored = await h.Get<IArtifactStore>().OpenReadAsync(pcap.BlobKey, Ct))
        {
            using var ms = new MemoryStream();
            await stored.CopyToAsync(ms, Ct);
            Assert.Equal(expected, ms.ToArray());
        }
        var flows = artifacts.Single(a => a.Kind == ArtifactKind.MitmFlows);
        Assert.True(flows.Size > 0);
        Assert.True(h.Get<IArtifactStore>().Exists(flows.BlobKey));

        Assert.Empty(task.ProcessingWarnings);
        Assert.True(cap.Disposed, "the handle is disposed once the files are stored");
    }

    [Fact]
    public async Task Coordinator_Order_CaptureStartsBeforeDelivery_StopsAfterVmGone_BeforeLeaseRelease()
    {
        var (h, cap) = await HarnessAsync(sql);
        await using var _ = h;
        var id = await h.SeedPreparingTaskAsync();

        await h.RunAsync(id);

        Assert.Equal(["start", "stop"], cap.Order);
        Assert.False(cap.OpenSentAtStart);    // recording was already on when the sample was delivered
        Assert.Equal(0, cap.VmsAtStop);       // the VM was destroyed before capture stopped
        Assert.Equal(1, cap.LeasesAtStop);    // the lease is still held while capture stops
        Assert.Empty(await h.Get<IEgressRouter>().ListAsync(Ct));
        Assert.Contains(h.Channels.Channels.SelectMany(c => c.SentCommands), c => c is OpenSampleCommand);
    }

    [Fact]
    public async Task Coordinator_CaptureStartFails_FailsInPreparing_SampleNeverSent()
    {
        var (h, cap) = await HarnessAsync(sql);
        await using var _ = h;
        cap.StartFails = new CaptureStartException("tcpdump did not start capturing in time");
        var id = await h.SeedPreparingTaskAsync();

        await h.RunAsync(id);

        await using var db = h.NewContext();
        var task = await db.Tasks.SingleAsync(t => t.Id == id, Ct);
        Assert.Equal(RunStatus.Failed, task.Status);
        Assert.Equal("capture failed to start: tcpdump did not start capturing in time", task.FailureReason);
        var failed = await db.TaskStatusHistory.Where(x => x.TaskId == id).OrderByDescending(x => x.Id).FirstAsync(Ct);
        Assert.Equal(RunStatus.Preparing, failed.From);
        Assert.Equal(RunStatus.Failed, failed.To);
        Assert.DoesNotContain(h.Channels.Channels.SelectMany(c => c.SentCommands), c => c is OpenSampleCommand or OpenUrlCommand);
        Assert.Empty(await h.Get<IHypervisor>().ListAsync(Ct));
        Assert.Empty(await h.Get<IEgressRouter>().ListAsync(Ct));
        Assert.Empty(await db.Artifacts.Where(a => a.TaskId == id && (a.Kind == ArtifactKind.Pcap || a.Kind == ArtifactKind.MitmFlows)).ToListAsync(Ct));
    }

    [Fact]
    public async Task Coordinator_CaptureStopFails_RecordsWarning_AndContinuesToProcessing()
    {
        var (h, cap) = await HarnessAsync(sql);
        await using var _ = h;
        cap.StopFails = new IOException("disk full");
        var id = await h.SeedPreparingTaskAsync();

        await h.RunAsync(id);

        await using (var db = h.NewContext())
        {
            var task = await db.Tasks.SingleAsync(t => t.Id == id, Ct);
            Assert.Equal(RunStatus.Processing, task.Status);
            Assert.Equal(["capture: the capture could not be stopped cleanly: disk full"], task.ProcessingWarnings);
            Assert.Empty(await db.Artifacts.Where(a => a.TaskId == id && a.Kind == ArtifactKind.Pcap).ToListAsync(Ct));
        }
        Assert.True(cap.Disposed);

        // processing rewrites the warnings list but keeps the run's own capture warning
        await h.Get<IProcessingPipeline>().ProcessAsync(id, Ct);
        await using var db2 = h.NewContext();
        var done = await db2.Tasks.SingleAsync(t => t.Id == id, Ct);
        Assert.Equal(RunStatus.Completed, done.Status);
        Assert.Contains("capture: the capture could not be stopped cleanly: disk full", done.ProcessingWarnings);
    }
}
