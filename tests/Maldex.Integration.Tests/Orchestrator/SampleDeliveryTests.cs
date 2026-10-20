using Maldex.Core.Agent;
using Maldex.Core.Domain;
using Maldex.Infrastructure.Fakes;
using Maldex.Integration.Tests.Fixtures;
using Maldex.Orchestrator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Maldex.Integration.Tests.Orchestrator;

/// <summary>The delivery protocol on its own: no database, a scripted guest.</summary>
public class SampleDeliveryProtocolTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Data(int length)
    {
        var b = new byte[length];
        new Random(42).NextBytes(b);
        return b;
    }

    private static IEnumerable<AgentMessage> Ack(AgentCommand c) => [new CommandResultMessage(c.CommandId, true)];

    [Fact]
    public async Task Deliver_SendsChunksThenOpen_InOrder()
    {
        var data = Data(1_258_291); // 1.2 MiB: three chunks
        var inbox = new TransferInbox();
        var channel = new ScriptedChannel(inbox, Ack);

        await new SampleDelivery(new FakeTimeProvider()).DeliverAsync(
            channel, inbox, "samples/ab/cd/blob", "invoice.exe", new MemoryStream(data), data.Length, ScriptedChannel.Sha(data).ToUpperInvariant(), Ct);

        Assert.Equal(4, channel.Sent.Count);
        var chunks = channel.Sent.Take(3).Cast<PutFileChunkCommand>().ToList();
        Assert.Equal([0, 1, 2], chunks.Select(c => c.Index));
        Assert.All(chunks, c =>
        {
            Assert.Equal(3, c.Count);
            Assert.Equal("samples/ab/cd/blob", c.TransferId);
            Assert.Equal(data.Length, c.TotalBytes);
            Assert.Equal(ScriptedChannel.Sha(data), c.Sha256); // lowercase on the wire
            Assert.True(Convert.FromBase64String(c.DataBase64).Length <= AgentLimits.ChunkBytes);
        });
        Assert.Equal(data, chunks.SelectMany(c => Convert.FromBase64String(c.DataBase64)).ToArray());
        Assert.Equal(AgentLimits.ChunkBytes, Convert.FromBase64String(chunks[0].DataBase64).Length);
        var open = Assert.IsType<OpenSampleCommand>(channel.Sent[3]);
        Assert.Equal(("invoice.exe", "samples/ab/cd/blob"), (open.FileName, open.BlobKey));
        Assert.Equal(4, channel.Sent.Select(c => c.CommandId).Distinct().Count());
    }

    [Fact]
    public async Task Deliver_NextChunkIsOnlySentAfterTheAnswer()
    {
        var data = Data(AgentLimits.ChunkBytes * 2);
        var inbox = new TransferInbox();
        var answered = new List<string>();
        var channel = new ScriptedChannel(inbox, c =>
        {
            // Everything before this command must already have been answered.
            Assert.Equal(answered.Count, answered.Distinct().Count());
            answered.Add(c.CommandId);
            return Ack(c);
        });

        await new SampleDelivery(new FakeTimeProvider()).DeliverAsync(channel, inbox, "k", "a.exe", new MemoryStream(data), data.Length, ScriptedChannel.Sha(data), Ct);

        Assert.Equal(3, answered.Count);
    }

    [Fact]
    public async Task Deliver_AgentRefusesAChunk_Throws_AndSendsNothingMore()
    {
        var data = Data(AgentLimits.ChunkBytes * 3);
        var inbox = new TransferInbox();
        var channel = new ScriptedChannel(inbox, c =>
            c is PutFileChunkCommand { Index: 1 } ? [new CommandResultMessage(c.CommandId, false, "disk full")] : Ack(c));

        var ex = await Assert.ThrowsAsync<SampleDeliveryException>(() => new SampleDelivery(new FakeTimeProvider())
            .DeliverAsync(channel, inbox, "k", "a.exe", new MemoryStream(data), data.Length, ScriptedChannel.Sha(data), Ct));

        Assert.StartsWith("sample delivery failed: chunk 2 of 3", ex.Message);
        Assert.Contains("disk full", ex.Message);
        Assert.Equal(2, channel.Sent.Count);
    }

    [Fact]
    public async Task Deliver_NoAnswer_TimesOutAfter30Seconds()
    {
        var time = new FakeTimeProvider();
        var data = Data(1000);
        var inbox = new TransferInbox();
        var channel = new ScriptedChannel(inbox, _ => []);
        var delivery = Task.Run(() => new SampleDelivery(time).DeliverAsync(channel, inbox, "k", "a.exe", new MemoryStream(data), data.Length, ScriptedChannel.Sha(data), Ct), Ct);

        while (channel.Sent.Count == 0) await Task.Delay(1, Ct);
        time.Advance(TimeSpan.FromSeconds(29));
        Assert.False(delivery.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(2));

        var ex = await Assert.ThrowsAsync<SampleDeliveryException>(() => delivery);
        Assert.Contains("no answer", ex.Message);
    }

    [Fact]
    public async Task Deliver_UnrelatedResults_AreIgnored()
    {
        var data = Data(100);
        var inbox = new TransferInbox();
        var channel = new ScriptedChannel(inbox, c => [new CommandResultMessage("someone-else", false, "x"), .. Ack(c)]);

        await new SampleDelivery(new FakeTimeProvider()).DeliverAsync(channel, inbox, "k", "a.exe", new MemoryStream(data), data.Length, ScriptedChannel.Sha(data), Ct);
    }

    [Fact]
    public async Task Deliver_AgentDisconnects_Throws()
    {
        var data = Data(100);
        var inbox = new TransferInbox();
        var channel = new ScriptedChannel(inbox, c => { inbox.Complete(); return []; });

        var ex = await Assert.ThrowsAsync<SampleDeliveryException>(() => new SampleDelivery(new FakeTimeProvider())
            .DeliverAsync(channel, inbox, "k", "a.exe", new MemoryStream(data), data.Length, ScriptedChannel.Sha(data), Ct));
        Assert.Contains("disconnected", ex.Message);
    }

    [Fact]
    public async Task Deliver_ChannelEndedByViolations_ReportsViolations()
    {
        var data = Data(100);
        var inbox = new TransferInbox();
        var channel = new ScriptedChannel(inbox, c => { inbox.Complete(); return []; }) { Violations = 10 };

        var ex = await Assert.ThrowsAsync<SampleDeliveryException>(() => new SampleDelivery(new FakeTimeProvider())
            .DeliverAsync(channel, inbox, "k", "a.exe", new MemoryStream(data), data.Length, ScriptedChannel.Sha(data), Ct));
        Assert.Equal("sample delivery failed: agent protocol violations", ex.Message);
    }

    [Fact]
    public async Task Deliver_StopRequestedWhileWaitingForAnAck_EndsPromptly()
    {
        var time = new FakeTimeProvider();
        var data = Data(AgentLimits.ChunkBytes * 3);
        var inbox = new TransferInbox();
        var channel = new ScriptedChannel(inbox, c => c is PutFileChunkCommand { Index: 1 } ? [] : Ack(c)); // chunk 2 is never acked
        var stop = false;
        var delivery = Task.Run(() => new SampleDelivery(time, pollInterval: TimeSpan.FromSeconds(1)).DeliverAsync(
            channel, inbox, "k", "a.exe", new MemoryStream(data), data.Length, ScriptedChannel.Sha(data), Ct, _ => Task.FromResult(stop)), Ct);

        while (channel.Sent.Count < 2) await Task.Delay(1, Ct);
        stop = true;
        for (var i = 0; i < 5 && !delivery.IsCompleted; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1)); // well short of the 30 s chunk timeout
            await Task.Delay(20, Ct);
        }

        await Assert.ThrowsAsync<DeliveryStoppedException>(() => delivery);
        Assert.Equal(2, channel.Sent.Count);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(32L * 1024 * 1024 + 1)]
    public async Task Deliver_EmptyOrTooLarge_ThrowsBeforeSending(long size)
    {
        var inbox = new TransferInbox();
        var channel = new ScriptedChannel(inbox, Ack);
        await Assert.ThrowsAsync<SampleDeliveryException>(() => new SampleDelivery(new FakeTimeProvider())
            .DeliverAsync(channel, inbox, "k", "a.exe", new MemoryStream(), size, new string('a', 64), Ct));
        Assert.Empty(channel.Sent);
    }

    [Fact]
    public async Task Deliver_StoredBlobShorterThanRecorded_Throws()
    {
        var inbox = new TransferInbox();
        var channel = new ScriptedChannel(inbox, Ack);
        await Assert.ThrowsAsync<SampleDeliveryException>(() => new SampleDelivery(new FakeTimeProvider())
            .DeliverAsync(channel, inbox, "k", "a.exe", new MemoryStream(new byte[10]), 20, new string('a', 64), Ct));
    }
}

/// <summary>Delivery as the coordinator drives it, against the fake guest.</summary>
[Collection("sql")]
public class SampleDeliveryTests(SqlServerFixture sql)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Sample(int length)
    {
        var b = new byte[length];
        new Random(7).NextBytes(b);
        return b;
    }

    [Fact]
    public async Task Deliver_ThroughCoordinator_SendsChunksThenOpen_BeforeRunning()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql);
        var data = Sample(1_258_291);
        var id = await h.SeedPreparingTaskAsync(sampleContent: data);

        await h.RunAsync(id);

        await using var db = h.NewContext();
        Assert.Equal(RunStatus.Processing, (await db.Tasks.SingleAsync(t => t.Id == id, Ct)).Status);
        var sent = Assert.Single(h.Channels.Channels).SentCommands;
        Assert.Equal(4, sent.Count);
        var chunks = sent.Take(3).Cast<PutFileChunkCommand>().ToList();
        Assert.Equal(data, chunks.SelectMany(c => Convert.FromBase64String(c.DataBase64)).ToArray());
        Assert.IsType<OpenSampleCommand>(sent[3]);
    }

    [Fact]
    public async Task Deliver_ChunkFailure_FailsInPreparing()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql);
        h.Get<FakeGuest>().RejectSampleChunkIndex = 1;
        var id = await h.SeedPreparingTaskAsync(sampleContent: Sample(1_258_291));

        await h.RunAsync(id);

        await using var db = h.NewContext();
        var task = await db.Tasks.SingleAsync(t => t.Id == id, Ct);
        Assert.Equal(RunStatus.Failed, task.Status);
        Assert.StartsWith("sample delivery failed:", task.FailureReason);
        Assert.Contains("chunk 2 of 3", task.FailureReason);
        var history = (await db.TaskStatusHistory.Where(x => x.TaskId == id).ToListAsync(Ct)).OrderBy(x => x.Id).Select(x => x.To).ToList();
        Assert.Equal([RunStatus.Preparing, RunStatus.Failed], history); // never Running, and not retried on a second VM
        Assert.Null(task.StartedAt);
        Assert.Single(h.Channels.Channels);
        Assert.DoesNotContain(h.Channels.Channels[0].SentCommands, c => c is OpenSampleCommand);
    }

    [Fact]
    public async Task Deliver_StopWhileAgentIsSlowToAck_CancelsFromPreparing()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql);
        h.Get<FakeGuest>().SilentSampleChunkIndex = 1;
        var id = await h.SeedPreparingTaskAsync(sampleContent: Sample(1_258_291));

        await h.RunAsync(id, async () =>
        {
            while (!h.Channels.Channels.Any(c => c.SentCommands.Count >= 2)) await Task.Delay(2, Ct);
            await using var api = h.NewContext();
            var t = await api.Tasks.SingleAsync(x => x.Id == id, Ct);
            t.StopRequestedAt = h.Time.GetUtcNow();
            await api.SaveChangesAsync(Ct);
        });

        await using var db = h.NewContext();
        var task = await db.Tasks.SingleAsync(t => t.Id == id, Ct);
        Assert.Equal(RunStatus.Cancelled, task.Status);
        var history = (await db.TaskStatusHistory.Where(x => x.TaskId == id).ToListAsync(Ct)).OrderBy(x => x.Id).Select(x => x.To).ToList();
        Assert.Equal([RunStatus.Preparing, RunStatus.Cancelled], history);
        Assert.Null(task.StartedAt);
    }

    [Fact]
    public async Task Deliver_MissingStoredBlob_FailsInPreparing()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql);
        var id = await h.SeedPreparingTaskAsync();
        await using (var db = h.NewContext())
        {
            var sample = await db.Samples.SingleAsync(s => s.Sha256 == db.Tasks.Single(t => t.Id == id).SampleSha256, Ct);
            await h.Get<Maldex.Core.Abstractions.IArtifactStore>().DeleteAsync(sample.BlobKey, Ct);
        }

        await h.RunAsync(id);

        await using var check = h.NewContext();
        var task = await check.Tasks.SingleAsync(t => t.Id == id, Ct);
        Assert.Equal(RunStatus.Failed, task.Status);
        Assert.StartsWith("sample delivery failed:", task.FailureReason);
    }

    [Fact]
    public async Task UrlTask_SkipsDelivery_AndSendsOpenUrl()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql);
        var id = await h.SeedPreparingTaskAsync();
        await using (var db = h.NewContext())
        {
            var t = await db.Tasks.SingleAsync(x => x.Id == id, Ct);
            t.SampleSha256 = null;
            t.OriginalFileName = null;
            t.Url = "http://example.test/";
            await db.SaveChangesAsync(Ct);
        }

        await h.RunAsync(id);

        var sent = Assert.Single(Assert.Single(h.Channels.Channels).SentCommands);
        Assert.Equal("http://example.test/", Assert.IsType<OpenUrlCommand>(sent).Url);
    }
}
