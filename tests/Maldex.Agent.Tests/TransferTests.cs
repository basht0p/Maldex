using System.Security.Cryptography;
using Maldex.Agent;
using Maldex.Agent.Commands;
using Maldex.Agent.Transfers;
using Maldex.Core.Agent;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Maldex.Agent.Tests;

public class TransferTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mxxfer-" + Guid.NewGuid().ToString("N"));
    private string Desktop => Path.Combine(_root, "Desktop");
    private string Staging => Path.Combine(_root, "staging");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class RecordingSampleOpener : ISampleOpener
    {
        public List<string> Opened { get; } = [];
        public Task OpenAsync(string filePath, CancellationToken ct) { Opened.Add(filePath); return Task.CompletedTask; }
    }

    private (CommandDispatcher Dispatcher, RecordingSampleOpener Opener) Make()
    {
        var opener = new RecordingSampleOpener();
        return (new CommandDispatcher(opener, new RecordingUrlOpener(), () => { }, Desktop, Staging), opener);
    }

    private static byte[] Data(int length, int seed = 1)
    {
        var b = new byte[length];
        new Random(seed).NextBytes(b);
        return b;
    }

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static IEnumerable<PutFileChunkCommand> Chunks(string transferId, byte[] data, string? sha = null, int chunkBytes = AgentLimits.ChunkBytes)
    {
        var count = Math.Max(1, (data.Length + chunkBytes - 1) / chunkBytes);
        for (var i = 0; i < count; i++)
        {
            var part = data.AsSpan(i * chunkBytes, Math.Min(chunkBytes, data.Length - i * chunkBytes));
            yield return new PutFileChunkCommand("cmd" + i, transferId, i, count, data.Length, sha ?? Sha(data), Convert.ToBase64String(part));
        }
    }

    [Fact]
    public async Task Deliver_ChunksThenOpen_WritesSampleToDesktop_AndOpensIt()
    {
        var (d, opener) = Make();
        var data = Data(1_300_000);
        foreach (var c in Chunks("blob-1", data))
            Assert.True((await d.DispatchAsync(c, CancellationToken.None)).Success);

        var res = await d.DispatchAsync(new OpenSampleCommand("o1", "invoice.exe", "blob-1"), CancellationToken.None);

        Assert.True(res.Success, res.Error);
        var target = Path.Combine(Desktop, "invoice.exe");
        Assert.Equal(data, File.ReadAllBytes(target));
        Assert.Equal([target], opener.Opened);
        Assert.Empty(Directory.GetFiles(Staging)); // staging is cleaned once the sample is published
    }

    [Fact]
    public async Task Deliver_HashMismatch_AgentRejects()
    {
        var (d, opener) = Make();
        var data = Data(700_000);
        var results = new List<CommandResultMessage>();
        foreach (var c in Chunks("blob-2", data, sha: new string('0', 64)))
            results.Add(await d.DispatchAsync(c, CancellationToken.None));

        Assert.True(results[0].Success);
        Assert.False(results[^1].Success);
        Assert.Contains("SHA-256", results[^1].Error);
        Assert.Empty(Directory.GetFiles(Staging)); // the partial data is discarded

        var open = await d.DispatchAsync(new OpenSampleCommand("o2", "x.exe", "blob-2"), CancellationToken.None);
        Assert.False(open.Success);
        Assert.Empty(opener.Opened);
        Assert.False(Directory.Exists(Desktop) && Directory.GetFiles(Desktop).Length > 0);
    }

    [Fact]
    public async Task OpenSample_UnknownOrIncompleteTransfer_Fails()
    {
        var (d, opener) = Make();
        Assert.False((await d.DispatchAsync(new OpenSampleCommand("o1", "a.exe", "nope"), CancellationToken.None)).Success);

        var data = Data(AgentLimits.ChunkBytes * 2);
        var first = Chunks("partial", data).First();
        Assert.True((await d.DispatchAsync(first, CancellationToken.None)).Success);
        Assert.False((await d.DispatchAsync(new OpenSampleCommand("o2", "a.exe", "partial"), CancellationToken.None)).Success);
        Assert.Empty(opener.Opened);
    }

    [Fact]
    public async Task Deliver_OutOfOrderChunk_FailsAndDiscards()
    {
        var (d, _) = Make();
        var all = Chunks("ooo", Data(AgentLimits.ChunkBytes * 3)).ToList();
        Assert.True((await d.DispatchAsync(all[0], CancellationToken.None)).Success);
        Assert.False((await d.DispatchAsync(all[2], CancellationToken.None)).Success);
        Assert.False((await d.DispatchAsync(all[1], CancellationToken.None)).Success); // transfer was dropped
        Assert.Empty(Directory.GetFiles(Staging));
    }

    [Theory]
    [InlineData(65, 100L)]          // too many chunks
    [InlineData(2, 33L * 1024 * 1024)] // too many bytes
    [InlineData(0, 10L)]
    public async Task Deliver_OverCap_Rejected(int count, long total)
    {
        var (d, _) = Make();
        var res = await d.DispatchAsync(new PutFileChunkCommand("c", "big", 0, count, total, new string('a', 64), Convert.ToBase64String(new byte[10])), CancellationToken.None);
        Assert.False(res.Success);
    }

    [Fact]
    public async Task Deliver_OversizeChunk_Rejected()
    {
        var (d, _) = Make();
        var res = await d.DispatchAsync(
            new PutFileChunkCommand("c", "t", 0, 1, AgentLimits.ChunkBytes + 1, new string('a', 64), Convert.ToBase64String(new byte[AgentLimits.ChunkBytes + 1])),
            CancellationToken.None);
        Assert.False(res.Success);
    }

    [Fact]
    public async Task Deliver_ChunkExceedingDeclaredTotal_Rejected()
    {
        var (d, _) = Make();
        var data = Data(100);
        var res = await d.DispatchAsync(new PutFileChunkCommand("c", "t", 0, 1, 50, Sha(data), Convert.ToBase64String(data)), CancellationToken.None);
        Assert.False(res.Success);
    }

    [Theory]
    [InlineData(@"..\..\evil.exe", "evil.exe")]
    [InlineData("../../evil.exe", "evil.exe")]
    [InlineData(@"C:\Windows\System32\evil.exe", "evil.exe")]
    [InlineData("a<b>c:d|e?.exe", "a_b_c_d_e_.exe")]
    [InlineData("name.exe.", "name.exe")]
    [InlineData("CON.txt", "_CON.txt")]
    public void SanitizeFileName_KeepsLeafOnly_AndReplacesInvalidChars(string input, string expected) =>
        Assert.Equal(expected, InboundFileAssembler.SanitizeFileName(input));

    [Fact]
    public void SanitizeFileName_Truncates_To200_KeepingExtension()
    {
        var result = InboundFileAssembler.SanitizeFileName(new string('a', 500) + ".exe");
        Assert.Equal(200, result.Length);
        Assert.EndsWith(".exe", result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("a/")]
    public void SanitizeFileName_RejectsEmptyLeaf(string input) =>
        Assert.Throws<ArgumentException>(() => InboundFileAssembler.SanitizeFileName(input));

    [Fact]
    public async Task OpenSample_FileNameCannotEscapeDesktop()
    {
        var (d, _) = Make();
        var data = Data(10);
        foreach (var c in Chunks("esc", data)) await d.DispatchAsync(c, CancellationToken.None);
        var res = await d.DispatchAsync(new OpenSampleCommand("o", @"..\..\evil.exe", "esc"), CancellationToken.None);
        Assert.True(res.Success, res.Error);
        Assert.True(File.Exists(Path.Combine(Desktop, "evil.exe")));
        Assert.False(File.Exists(Path.Combine(_root, "evil.exe")));
    }

    // ---- outbound (collect_file) ----

    private static async Task<(List<FileChunkMessage> Chunks, CommandResultMessage Result)> CollectAsync(CommandDispatcher d, string path)
    {
        var sent = new List<AgentMessage>();
        var res = await d.DispatchAsync(new CollectFileCommand("col1", path), CancellationToken.None, (m, _) => { sent.Add(m); return Task.CompletedTask; });
        return (sent.OfType<FileChunkMessage>().ToList(), res);
    }

    [Fact]
    public async Task Collect_SendsChunks_ThenSuccess()
    {
        Directory.CreateDirectory(_root);
        var data = Data(1_300_000, seed: 7);
        var path = Path.Combine(_root, "dropped.bin");
        File.WriteAllBytes(path, data);
        var (d, _) = Make();

        var (chunks, res) = await CollectAsync(d, path);

        Assert.True(res.Success, res.Error);
        Assert.Equal(3, chunks.Count);
        Assert.All(chunks, c =>
        {
            Assert.Equal("col1", c.TransferId);
            Assert.Equal(3, c.Count);
            Assert.Equal(data.Length, c.TotalBytes);
            Assert.Equal(Sha(data), c.Sha256);
            Assert.Equal(path, c.Path);
            Assert.Null(AgentMessageValidator.Validate(c));
        });
        var joined = chunks.OrderBy(c => c.Index).SelectMany(c => Convert.FromBase64String(c.DataBase64)).ToArray();
        Assert.Equal(data, joined);
    }

    [Fact]
    public async Task Collect_EmptyFile_SendsOneEmptyChunk()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "empty.bin");
        File.WriteAllBytes(path, []);
        var (chunks, res) = await CollectAsync(Make().Dispatcher, path);
        Assert.True(res.Success, res.Error);
        var c = Assert.Single(chunks);
        Assert.Equal(0, c.TotalBytes);
        Assert.Null(AgentMessageValidator.Validate(c));
    }

    [Fact]
    public async Task Collect_MissingFile_Fails_WithoutChunks()
    {
        var (chunks, res) = await CollectAsync(Make().Dispatcher, Path.Combine(_root, "nope.bin"));
        Assert.False(res.Success);
        Assert.Empty(chunks);
    }

    [Fact]
    public async Task Collect_OverCap_Fails_WithoutChunks()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "huge.bin");
        await using (var f = File.Create(path)) f.SetLength(AgentLimits.MaxDroppedFileBytes + 1);
        var (chunks, res) = await CollectAsync(Make().Dispatcher, path);
        Assert.False(res.Success);
        Assert.Empty(chunks);
    }

    [Fact]
    public async Task Collect_Directory_Fails()
    {
        Directory.CreateDirectory(_root);
        var (chunks, res) = await CollectAsync(Make().Dispatcher, _root);
        Assert.False(res.Success);
        Assert.Empty(chunks);
    }

    [Fact]
    public async Task Host_CollectFile_WritesChunksBeforeTheResult()
    {
        Directory.CreateDirectory(_root);
        var data = Data(600_000, seed: 3);
        var path = Path.Combine(_root, "d.bin");
        File.WriteAllBytes(path, data);

        var pair = new ChannelPair();
        var host = new AgentHost(pair.GuestSide, new ScriptedSource(), Make().Dispatcher, new FakeTimeProvider());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var run = host.RunAsync(cts.Token);

        Assert.IsType<ReadyMessage>(await pair.NextAsync());
        await pair.SendAsync(new CollectFileCommand("c9", path));

        var a = Assert.IsType<FileChunkMessage>(await pair.NextAsync());
        var b = Assert.IsType<FileChunkMessage>(await pair.NextAsync());
        var r = Assert.IsType<CommandResultMessage>(await pair.NextAsync());
        Assert.Equal([0, 1], [a.Index, b.Index]);
        Assert.Equal(new CommandResultMessage("c9", true), r);

        pair.CloseHostSide();
        await run;
    }

    [Fact]
    public async Task Host_PutFileChunk_RoundTripsThroughTheRealFraming_WorstCaseBytes()
    {
        var raw = new byte[AgentLimits.ChunkBytes];
        for (var i = 0; i < raw.Length; i += 3) { raw[i] = 0xFB; if (i + 1 < raw.Length) raw[i + 1] = 0xEF; if (i + 2 < raw.Length) raw[i + 2] = 0xBE; }

        var pair = new ChannelPair();
        var host = new AgentHost(pair.GuestSide, new ScriptedSource(), Make().Dispatcher, new FakeTimeProvider());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var run = host.RunAsync(cts.Token);

        Assert.IsType<ReadyMessage>(await pair.NextAsync());
        await pair.SendAsync(Chunks("wc", raw).Single());
        var r = Assert.IsType<CommandResultMessage>(await pair.NextAsync());
        Assert.True(r.Success, r.Error);

        pair.CloseHostSide();
        await run;
    }
}
