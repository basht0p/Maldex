using System.Text;
using Maldex.Core.Abstractions;
using Maldex.Core.Agent;
using Maldex.Core.Domain;
using Maldex.Core.Verdicts;
using Maldex.Infrastructure.Fakes;
using Maldex.Integration.Tests.Fixtures;
using Maldex.Orchestrator;
using Maldex.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Maldex.Integration.Tests.Orchestrator;

/// <summary>The collection protocol on its own: no database, a scripted (and hostile) guest.</summary>
public class DroppedFileCollectorProtocolTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Data(int length, int seed = 1)
    {
        var b = new byte[length];
        new Random(seed).NextBytes(b);
        return b;
    }

    /// <summary>A well-behaved guest serving <paramref name="files"/>; <paramref name="tamper"/> may alter the chunks it sends.</summary>
    private static Func<AgentCommand, IEnumerable<AgentMessage>> Guest(
        Dictionary<string, byte[]> files, Func<List<FileChunkMessage>, List<FileChunkMessage>>? tamper = null) => command =>
    {
        if (command is not CollectFileCommand c) return [];
        if (!files.TryGetValue(c.Path, out var data)) return [new CommandResultMessage(c.CommandId, false, "not found")];
        var count = Math.Max(1, (data.Length + AgentLimits.ChunkBytes - 1) / AgentLimits.ChunkBytes);
        var chunks = Enumerable.Range(0, count).Select(i =>
        {
            var offset = i * AgentLimits.ChunkBytes;
            return new FileChunkMessage(DateTimeOffset.UtcNow, c.CommandId, i, count, data.Length, ScriptedChannel.Sha(data), c.Path,
                Convert.ToBase64String(data.AsSpan(offset, Math.Min(AgentLimits.ChunkBytes, data.Length - offset))));
        }).ToList();
        if (tamper is not null) chunks = tamper(chunks);
        return [.. chunks, new CommandResultMessage(c.CommandId, true)];
    };

    private static async Task<(IReadOnlyList<DroppedFile> Kept, ScriptedChannel Channel, MemoryArtifactStore Store)> RunAsync(
        Func<AgentCommand, IEnumerable<AgentMessage>> guest, IEnumerable<string> paths, string? excludeSha = null, FakeTimeProvider? time = null)
    {
        var inbox = new TransferInbox();
        var channel = new ScriptedChannel(inbox, guest);
        var store = new MemoryArtifactStore();
        var kept = await new DroppedFileCollector(store, time ?? new FakeTimeProvider(), NullLogger.Instance).CollectAsync(channel, inbox, paths, excludeSha, Ct);
        return (kept, channel, store);
    }

    [Fact]
    public async Task Collect_ReassemblesChunks_StoresDropped()
    {
        var data = Data(1_300_000);
        var (kept, channel, store) = await RunAsync(Guest(new() { [@"C:\Temp\a.exe"] = data }), [@"C:\Temp\a.exe"]);

        var file = Assert.Single(kept);
        Assert.Equal(@"C:\Temp\a.exe", file.GuestPath);
        Assert.Equal(ScriptedChannel.Sha(data), file.Blob.Sha256);
        Assert.Equal(data, store.Blobs[file.Blob.Key]);
        Assert.StartsWith("dropped/", file.Blob.Key);
        Assert.IsType<CollectFileCommand>(Assert.Single(channel.Sent));
    }

    [Fact]
    public async Task Collect_ChunksOutOfOrder_StillReassembles()
    {
        var data = Data(1_300_000);
        var (kept, _, store) = await RunAsync(Guest(new() { ["p"] = data }, chunks => [.. chunks.AsEnumerable().Reverse()]), ["p"]);
        Assert.Equal(data, store.Blobs[Assert.Single(kept).Blob.Key]);
    }

    [Fact]
    public async Task Collect_DeclaredOverCap_Rejected()
    {
        var data = Data(1000);
        var (kept, _, store) = await RunAsync(
            Guest(new() { ["p"] = data }, chunks => [.. chunks.Select(c => c with { TotalBytes = AgentLimits.MaxDroppedFileBytes + 1 })]), ["p"]);
        Assert.Empty(kept);
        Assert.Empty(store.Blobs);
    }

    [Fact]
    public async Task Collect_OverCap_Rejected()
    {
        // The guest claims a small file and keeps sending: more data than it declared.
        var data = Data(AgentLimits.ChunkBytes * 4);
        var (kept, _, store) = await RunAsync(
            Guest(new() { ["p"] = data }, chunks => [.. chunks.Select(c => c with { TotalBytes = AgentLimits.ChunkBytes })]), ["p"]);
        Assert.Empty(kept);
        Assert.Empty(store.Blobs);
    }

    [Fact]
    public async Task Collect_HashMismatch_Rejected()
    {
        var (kept, _, store) = await RunAsync(
            Guest(new() { ["p"] = Data(5000) }, chunks => [.. chunks.Select(c => c with { Sha256 = new string('0', 64) })]), ["p"]);
        Assert.Empty(kept);
        Assert.Empty(store.Blobs);
    }

    [Fact]
    public async Task Collect_MissingChunk_Rejected()
    {
        var (kept, _, store) = await RunAsync(Guest(new() { ["p"] = Data(AgentLimits.ChunkBytes * 3) }, chunks => [chunks[0], chunks[2]]), ["p"]);
        Assert.Empty(kept);
        Assert.Empty(store.Blobs);
    }

    [Fact]
    public async Task Collect_DuplicateOrOutOfRangeChunk_Rejected()
    {
        var (dup, _, _) = await RunAsync(Guest(new() { ["p"] = Data(AgentLimits.ChunkBytes * 2) }, chunks => [chunks[0], chunks[0], chunks[1]]), ["p"]);
        Assert.Empty(dup);
        var (range, _, _) = await RunAsync(Guest(new() { ["p"] = Data(100) }, chunks => [.. chunks.Select(c => c with { Index = 5 })]), ["p"]);
        Assert.Empty(range);
        var (count, _, _) = await RunAsync(Guest(new() { ["p"] = Data(100) }, chunks => [.. chunks.Select(c => c with { Count = 65 })]), ["p"]);
        Assert.Empty(count);
    }

    [Fact]
    public async Task Collect_InconsistentChunks_Rejected()
    {
        var (kept, _, _) = await RunAsync(
            Guest(new() { ["p"] = Data(AgentLimits.ChunkBytes * 2) }, chunks => [chunks[0], chunks[1] with { TotalBytes = 5 }]), ["p"]);
        Assert.Empty(kept);
    }

    [Fact]
    public async Task Collect_OneBadFile_DoesNotStopTheOthers()
    {
        var good = Data(2000, seed: 5);
        var (kept, channel, _) = await RunAsync(
            Guest(new() { ["bad"] = Data(100), ["good"] = good }, chunks => chunks[0].Path == "bad" ? [.. chunks.Select(c => c with { Sha256 = new string('1', 64) })] : chunks),
            ["bad", "missing", "good"]);

        Assert.Equal(["good"], kept.Select(k => k.GuestPath));
        Assert.Equal(3, channel.Sent.Count);
    }

    [Fact]
    public async Task Collect_DeduplicatesCaseInsensitively_AndCapsAt50Paths()
    {
        var paths = Enumerable.Range(0, 80).Select(i => $@"C:\Temp\f{i}.bin").Append(@"c:\temp\F0.BIN").ToList();
        var (_, channel, _) = await RunAsync(Guest([]), paths);
        var asked = channel.Sent.Cast<CollectFileCommand>().Select(c => c.Path).ToList();
        Assert.Equal(50, asked.Count);
        Assert.Equal(paths.Take(50), asked);
    }

    [Fact]
    public async Task Collect_StopsWhenTheTotalBudgetIsSpent()
    {
        // 20 files of 5 MiB: 12 fit in 64 MiB; the 13th would make 65 MiB, so it is neither stored nor followed by a 14th request.
        var files = Enumerable.Range(0, 20).ToDictionary(i => $"f{i}", i => Data(5 * 1024 * 1024, seed: i));
        var (kept, channel, store) = await RunAsync(Guest(files), files.Keys);
        Assert.Equal(13, channel.Sent.Count);
        Assert.Equal(12, kept.Count);
        Assert.Equal(12, store.Blobs.Count);
        Assert.True(kept.Sum(k => k.Blob.Size) <= DroppedFileCollector.MaxTotalBytes);
    }

    [Fact]
    public async Task Collect_OverlongCandidatePaths_AreNeverRequested()
    {
        var huge = new string('a', 40_000);
        var edge = new string('b', TransferInbox.MaxPathChars);
        var (_, channel, _) = await RunAsync(Guest([]), [huge, "  ", "", edge, "ok"]);
        Assert.Equal([edge, "ok"], channel.Sent.Cast<CollectFileCommand>().Select(c => c.Path));
        Assert.All(channel.Sent, c => Assert.True(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes<AgentCommand>(c, AgentJson.Options).Length <= AgentFrameReader.MaxFrameBytes));
    }

    /// <summary>An inbox whose sample was opened and whose first process after the open (the sample's root) is <paramref name="pid"/>.</summary>
    private static async Task<TransferInbox> OpenedAsync(int pid = 1, int? agentPid = null)
    {
        var inbox = new TransferInbox(agentPid);
        inbox.MarkOpened();
        await inbox.OfferAsync(new ProcessStartedMessage(DateTimeOffset.UnixEpoch, pid, agentPid ?? 0, @"C:\s.exe", "s.exe"), Ct);
        return inbox;
    }

    private static FileEventMessage Create(int pid, string path) => new(DateTimeOffset.UnixEpoch, pid, "create", path);

    private static ProcessStartedMessage Started(int pid, int parent, string image = @"C:\x.exe") => new(DateTimeOffset.UnixEpoch, pid, parent, image, image);

    [Fact]
    public async Task Inbox_TracksOnlyTheSampleProcessTree_FromTheAgentPid()
    {
        var inbox = new TransferInbox(agentPid: 900);
        await inbox.OfferAsync(Started(500, 4), Ct);
        await inbox.OfferAsync(Create(500, @"C:\before-open.txt"), Ct); // before the open: never the sample's
        inbox.MarkOpened();
        await inbox.OfferAsync(Started(2000, 4, @"C:\Windows\System32\svchost.exe"), Ct); // unrelated, even though first after the open
        await inbox.OfferAsync(Started(1000, 900, @"C:\Users\a\Desktop\sample.exe"), Ct); // launched by the agent's opener
        await inbox.OfferAsync(Started(1001, 1000, @"C:\Windows\System32\cmd.exe"), Ct);
        await inbox.OfferAsync(Started(1002, 1001, @"C:\Users\a\AppData\Local\Temp\stage2.exe"), Ct);
        await inbox.OfferAsync(Create(1000, @"C:\Users\a\AppData\Local\Temp\stage2.exe"), Ct);
        await inbox.OfferAsync(Create(2000, @"C:\Windows\Temp\svchost-noise.etl"), Ct);
        await inbox.OfferAsync(Create(1002, @"C:\Users\a\AppData\Roaming\evil.dll"), Ct);
        await inbox.OfferAsync(Create(500, @"C:\other.txt"), Ct);
        await inbox.OfferAsync(Create(31337, @"C:\unknown-pid.txt"), Ct);
        Assert.Equal([@"C:\Users\a\AppData\Local\Temp\stage2.exe", @"C:\Users\a\AppData\Roaming\evil.dll"], inbox.CreatedPaths);
    }

    [Fact]
    public async Task Inbox_WithoutAgentPid_SeedsWithTheFirstProcessAfterTheOpen()
    {
        var inbox = new TransferInbox();
        await inbox.OfferAsync(Create(7, @"C:\early.txt"), Ct);
        inbox.MarkOpened();
        await inbox.OfferAsync(Create(7, @"C:\no-tree-yet.txt"), Ct);
        await inbox.OfferAsync(Started(4120, 1, @"C:\Windows\explorer.exe"), Ct); // first after the open: the seed
        await inbox.OfferAsync(Started(8088, 4120), Ct);
        await inbox.OfferAsync(Started(9000, 640), Ct); // not in the tree
        await inbox.OfferAsync(Create(8088, @"C:\a.exe"), Ct);
        await inbox.OfferAsync(Create(9000, @"C:\b.exe"), Ct);
        Assert.Equal([@"C:\a.exe"], inbox.CreatedPaths);
    }

    [Fact]
    public async Task Inbox_ExitedPidLeavesTheTree_SoAReusedPidIsNotTheSample()
    {
        var inbox = await OpenedAsync(1000);
        await inbox.OfferAsync(new ProcessExitedMessage(DateTimeOffset.UnixEpoch, 1000, 0), Ct);
        await inbox.OfferAsync(Create(1000, @"C:\reused.txt"), Ct);
        Assert.Empty(inbox.CreatedPaths);
    }

    [Theory]
    [InlineData(@"C:\Windows\Prefetch\SAMPLE.EXE-1234ABCD.pf")]
    [InlineData(@"C:\Users\a\AppData\Local\Microsoft\Windows\INetCache\IE\X1\page[1].htm")]
    [InlineData(@"C:\Users\a\AppData\Local\Microsoft\Edge\User Data\Default\Cache\Cache_Data\f_000001")]
    [InlineData(@"C:\Users\a\AppData\Local\Microsoft\Edge\User Data\Default\Code Cache\js\index")]
    [InlineData(@"C:\Users\a\AppData\Local\Microsoft\Edge\User Data\ShaderCache\GPUCache\data_0")]
    [InlineData(@"C:\Users\a\AppData\Local\Microsoft\Edge\User Data\Default\Service Worker\CacheStorage\x\y")]
    [InlineData(@"C:\Users\a\AppData\Local\Temp\__PSScriptPolicyTest_abcd1234.efg.ps1")]
    [InlineData(@"c:\users\a\appdata\local\temp\__psscriptpolicytest_x.psm1")]
    public async Task Inbox_IgnoresWellKnownNoise(string path)
    {
        var inbox = await OpenedAsync();
        await inbox.OfferAsync(Create(1, path), Ct);
        await inbox.OfferAsync(Create(1, @"C:\Users\a\Downloads\payload.exe"), Ct);
        Assert.Equal([@"C:\Users\a\Downloads\payload.exe"], inbox.CreatedPaths);
    }

    [Fact]
    public async Task Inbox_DoesNotTrackOverlongOrBlankPaths()
    {
        var inbox = await OpenedAsync();
        var t = DateTimeOffset.UnixEpoch;
        await inbox.OfferAsync(new FileEventMessage(t, 1, "create", new string('a', 1_000_000)), Ct);
        await inbox.OfferAsync(new FileEventMessage(t, 1, "create", "   "), Ct);
        await inbox.OfferAsync(new FileEventMessage(t, 1, "create", @"C:\ok.exe"), Ct);
        Assert.Equal([@"C:\ok.exe"], inbox.CreatedPaths);
    }

    [Fact]
    public async Task Collect_SilentGuest_GivesUpOnTheFile_AndMovesOn()
    {
        var time = new FakeTimeProvider();
        var inbox = new TransferInbox();
        var channel = new ScriptedChannel(inbox, c => c is CollectFileCommand { Path: "silent" } ? [] : Guest(new() { ["ok"] = Data(10) })(c));
        var task = Task.Run(() => new DroppedFileCollector(new MemoryArtifactStore(), time, NullLogger.Instance)
            .CollectAsync(channel, inbox, ["silent", "ok"], null, Ct), Ct);

        while (channel.Sent.Count == 0) await Task.Delay(1, Ct);
        time.Advance(DroppedFileCollector.PerFileTimeout + TimeSpan.FromSeconds(1));

        var kept = await task;
        Assert.Equal(["ok"], kept.Select(k => k.GuestPath));
    }

    [Fact]
    public async Task Collect_OverallTimeout_StopsCollecting()
    {
        var time = new FakeTimeProvider();
        var inbox = new TransferInbox();
        var channel = new ScriptedChannel(inbox, _ => []);
        var task = Task.Run(() => new DroppedFileCollector(new MemoryArtifactStore(), time, NullLogger.Instance)
            .CollectAsync(channel, inbox, Enumerable.Range(0, 10).Select(i => $"f{i}"), null, Ct), Ct);

        // Each silent file burns its per-file timeout; the overall 60 s budget ends the loop after about three.
        while (!task.IsCompleted)
        {
            time.Advance(TimeSpan.FromSeconds(5));
            await Task.Delay(1, Ct);
        }

        Assert.Empty(await task);
        Assert.True(channel.Sent.Count < 10);
    }

    [Fact]
    public async Task Collect_SkipsTheSampleItself_AndEmptyFiles()
    {
        var sample = Data(300, seed: 9);
        var (kept, _, store) = await RunAsync(Guest(new() { ["sample"] = sample, ["empty"] = [] }), ["sample", "empty"], excludeSha: ScriptedChannel.Sha(sample));
        Assert.Empty(kept);
        Assert.Empty(store.Blobs);
    }

    [Fact]
    public async Task Collect_NoCandidates_SendsNothing()
    {
        var (kept, channel, _) = await RunAsync(Guest([]), []);
        Assert.Empty(kept);
        Assert.Empty(channel.Sent);
    }

    [Fact]
    public async Task Inbox_TracksCreatedPaths_AndSwallowsUnsolicitedChunks()
    {
        var inbox = await OpenedAsync();
        var t = DateTimeOffset.UnixEpoch;
        Assert.False(await inbox.OfferAsync(new FileEventMessage(t, 1, "create", @"C:\a.exe"), Ct));
        Assert.False(await inbox.OfferAsync(new FileEventMessage(t, 1, "create", @"c:\A.EXE"), Ct));
        Assert.False(await inbox.OfferAsync(new FileEventMessage(t, 1, "write", @"C:\w.txt"), Ct));
        Assert.False(await inbox.OfferAsync(new FileEventMessage(t, 1, "create", @"C:\b.exe"), Ct));
        Assert.Equal([@"C:\a.exe", @"C:\b.exe"], inbox.CreatedPaths);

        Assert.True(await inbox.OfferAsync(new FileChunkMessage(t, "x", 0, 1, 0, new string('a', 64), "p", ""), Ct));
        Assert.False(await inbox.OfferAsync(new CommandResultMessage("x", true), Ct)); // no one is waiting: ingest as before
    }
}

/// <summary>Collection as the coordinator drives it, against the fake guest.</summary>
[Collection("sql")]
public class DroppedFileCollectionTests(SqlServerFixture sql)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Dropped = @"C:\Users\analyst\AppData\Local\Temp\x.exe";

    // The standard antivirus test file (split so this source file is not itself flagged by scanners).
    private static readonly byte[] EicarBytes =
        Encoding.ASCII.GetBytes(@"X5O!P%@AP[4\PZX54(P^)7CC)7}$" + "EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*");

    private static byte[] Data(int length, int seed = 3)
    {
        var b = new byte[length];
        new Random(seed).NextBytes(b);
        return b;
    }

    /// <summary>The shipped dropper replay (creates <see cref="Dropped"/>), held open so the run ends at its deadline and collection can happen.</summary>
    private static Dictionary<string, string?> DropperReplay() => new()
    {
        ["Maldex:Fake:Replay:DefaultFile"] = "dropper.jsonl",
        ["Maldex:Fake:Replay:HoldOpen"] = "true",
    };

    /// <summary>Runs a task to its deadline: wait for Running, then jump past the 60 s duration.</summary>
    private static async Task<Guid> RunToDeadlineAsync(OrchestratorHarness h, byte[]? sample = null)
    {
        var id = await h.SeedPreparingTaskAsync(durationSec: 60, sampleContent: sample);
        await h.RunAsync(id, async () =>
        {
            await h.WaitForStatusAsync(id, RunStatus.Running);
            h.Time.Advance(TimeSpan.FromSeconds(61));
        });
        return id;
    }

    [Fact]
    public async Task Collect_ReassemblesChunks_StoresDropped()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql, DropperReplay());
        var data = Data(1_300_000);
        h.Get<FakeGuest>().AddFile(Dropped, data);

        var id = await RunToDeadlineAsync(h);

        await using var db = h.NewContext();
        Assert.Equal(RunStatus.Processing, (await db.Tasks.SingleAsync(t => t.Id == id, Ct)).Status);
        var artifact = Assert.Single(await db.Artifacts.Where(a => a.TaskId == id && a.Kind == ArtifactKind.Dropped).ToListAsync(Ct));
        Assert.Equal(Dropped, artifact.GuestPath);
        Assert.Equal(data.Length, artifact.Size);
        await using var stored = await h.Get<IArtifactStore>().OpenReadAsync(artifact.BlobKey, Ct);
        using var ms = new MemoryStream();
        await stored.CopyToAsync(ms, Ct);
        Assert.Equal(data, ms.ToArray());

        // The coordinator asked for exactly the created file, after the sample was delivered.
        var sent = Assert.Single(h.Channels.Channels).SentCommands;
        Assert.Equal(Dropped, Assert.IsType<CollectFileCommand>(sent[^1]).Path);
    }

    [Fact]
    public async Task Collect_MissingFile_RunContinuesToProcessing_NoArtifact()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql, DropperReplay()); // the fake guest has no such file

        var id = await RunToDeadlineAsync(h);

        await using var db = h.NewContext();
        Assert.Equal(RunStatus.Processing, (await db.Tasks.SingleAsync(t => t.Id == id, Ct)).Status);
        Assert.DoesNotContain(await db.Artifacts.Where(a => a.TaskId == id).ToListAsync(Ct), a => a.Kind == ArtifactKind.Dropped);
    }

    [Fact]
    public async Task Collect_OverCap_Rejected()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql, DropperReplay());
        // 2 MiB of data under a 512 KiB declaration: the guest sends more than it said it would.
        h.Get<FakeGuest>().AddFile(Dropped, new FakeGuestFile(Data(AgentLimits.ChunkBytes * 4), ClaimedTotalBytes: AgentLimits.ChunkBytes));

        var id = await RunToDeadlineAsync(h);

        await using var db = h.NewContext();
        Assert.Equal(RunStatus.Processing, (await db.Tasks.SingleAsync(t => t.Id == id, Ct)).Status);
        Assert.DoesNotContain(await db.Artifacts.Where(a => a.TaskId == id).ToListAsync(Ct), a => a.Kind == ArtifactKind.Dropped);
    }

    [Fact]
    public async Task Collect_HashMismatch_Rejected()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql, DropperReplay());
        h.Get<FakeGuest>().AddFile(Dropped, new FakeGuestFile(Data(70_000), ClaimedSha256: new string('0', 64)));

        var id = await RunToDeadlineAsync(h);

        await using var db = h.NewContext();
        Assert.Equal(RunStatus.Processing, (await db.Tasks.SingleAsync(t => t.Id == id, Ct)).Status);
        Assert.DoesNotContain(await db.Artifacts.Where(a => a.TaskId == id).ToListAsync(Ct), a => a.Kind == ArtifactKind.Dropped);
    }

    [Fact]
    public async Task Collect_NotAttemptedWhenTheGuestAlreadyDisconnected()
    {
        // The default test replay ends and the stream closes; there is nobody left to ask.
        await using var h = await OrchestratorHarness.CreateAsync(sql, new Dictionary<string, string?> { ["Maldex:Fake:Replay:DefaultFile"] = "dropper.jsonl" });
        h.Get<FakeGuest>().AddFile(Dropped, Data(100));
        var id = await h.SeedPreparingTaskAsync();

        await h.RunAsync(id);

        await using var db = h.NewContext();
        Assert.Equal(RunStatus.Processing, (await db.Tasks.SingleAsync(t => t.Id == id, Ct)).Status);
        Assert.DoesNotContain(Assert.Single(h.Channels.Channels).SentCommands, c => c is CollectFileCommand);
    }

    [Fact]
    public async Task Collect_DroppedExe_YaraScannedInProcessing()
    {
        var yara = new Maldex.Infrastructure.Scanning.YaraOptions().Binary;
        Assert.SkipUnless(YaraBinaryAvailable(yara), $"The '{yara}' binary is not installed (apt install yara); skipping the EICAR scan.");

        await using var h = await OrchestratorHarness.CreateAsync(sql, DropperReplay());
        h.Get<FakeGuest>().AddFile(Dropped, EicarBytes);
        var id = await RunToDeadlineAsync(h, sample: Encoding.ASCII.GetBytes("benign sample " + Guid.NewGuid()));

        await h.Get<ProcessingPipeline>().ProcessAsync(id, Ct);

        await using var db = h.NewContext();
        var task = await db.Tasks.SingleAsync(t => t.Id == id, Ct);
        Assert.Equal(RunStatus.Completed, task.Status);
        Assert.Equal(Verdict.Malicious, task.Verdict);
        var detection = Assert.Single(await db.Detections.Where(d => d.TaskId == id && d.Source == DetectionSource.Yara).ToListAsync(Ct));
        Assert.Equal("EICAR_Test_File", detection.Rule);
        Assert.Equal(Dropped, detection.Target); // the file's path inside the guest
    }

    private static bool YaraBinaryAvailable(string binary)
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(binary, "--version") { RedirectStandardOutput = true, RedirectStandardError = true });
            p!.WaitForExit(5000);
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
