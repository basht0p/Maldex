using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Maldex.Core.Abstractions;
using Maldex.Core.Agent;
using Maldex.Infrastructure.Agent;
using Microsoft.Extensions.Options;
using Xunit;

namespace Maldex.Core.Tests.Agent;

public sealed class VirtioSerialAgentChannelTests : IDisposable
{
    private const string Ready = """{"type":"ready","agentVersion":"1.0","hostname":"vm"}""";
    private const string Invalid = """{"type":"process_exited","at":"2026-01-01T00:00:00Z","pid":-1,"exitCode":0}""";

    private readonly string _dir = Directory.CreateTempSubdirectory("mxagent").FullName;
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(10));

    public void Dispose()
    {
        _cts.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private CancellationToken Ct => _cts.Token;

    private static byte[] Frame(string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var buf = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(buf, (uint)body.Length);
        body.CopyTo(buf, 4);
        return buf;
    }

    private static string ReadyN(int n) =>
        $$"""{"type":"ready","agentVersion":"1.{{n}}","hostname":"vm"}""";

    /// <summary>Returns (channel under test, guest-side stream) over a real Unix socket.</summary>
    private async Task<(VirtioSerialAgentChannel Channel, NetworkStream Guest)> PairAsync()
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(1);
        var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        var accept = listener.AcceptAsync(Ct).AsTask();
        await client.ConnectAsync(new UnixDomainSocketEndPoint(path), Ct);
        var server = await accept;
        return (new VirtioSerialAgentChannel(new NetworkStream(client, true)), new NetworkStream(server, true));
    }

    private async Task<List<AgentMessage>> DrainAsync(IAgentChannel channel)
    {
        var list = new List<AgentMessage>();
        await foreach (var m in channel.ReadAsync(Ct)) list.Add(m);
        return list;
    }

    [Fact]
    public async Task YieldsValidatedMessagesInOrder()
    {
        var (channel, guest) = await PairAsync();
        await using var _ = channel;
        for (var i = 0; i < 3; i++) await guest.WriteAsync(Frame(ReadyN(i)), Ct);
        guest.Dispose();

        var got = await DrainAsync(channel);
        Assert.Equal(["1.0", "1.1", "1.2"], got.Cast<ReadyMessage>().Select(m => m.AgentVersion));
        Assert.Equal(0, channel.Violations);
    }

    [Fact]
    public async Task InvalidMessage_Dropped_CountsViolation_StreamContinues()
    {
        var (channel, guest) = await PairAsync();
        await using var _ = channel;
        await guest.WriteAsync(Frame(Ready), Ct);
        await guest.WriteAsync(Frame(Invalid), Ct);
        await guest.WriteAsync(Frame("not json"), Ct);
        await guest.WriteAsync(Frame(ReadyN(9)), Ct);
        guest.Dispose();

        var got = await DrainAsync(channel);
        Assert.Equal(2, got.Count);
        Assert.Equal(2, channel.Violations);
    }

    [Fact]
    public async Task OversizeLength_IsFatal_Completes()
    {
        var (channel, guest) = await PairAsync();
        await using var _ = channel;
        await guest.WriteAsync(Frame(Ready), Ct);
        await guest.WriteAsync(new byte[] { 0x00, 0x10, 0x00, 0x01 }, Ct);
        await guest.WriteAsync(Frame(ReadyN(9)), Ct); // never read; guest stays open

        var got = await DrainAsync(channel);
        Assert.Single(got);
        Assert.Equal(0, channel.Violations);
    }

    [Fact]
    public async Task TenViolations_Completes_AndViolationsIsTen()
    {
        var (channel, guest) = await PairAsync();
        await using var _ = channel;
        for (var i = 0; i < 12; i++) await guest.WriteAsync(Frame(Invalid), Ct);
        await guest.WriteAsync(Frame(Ready), Ct);

        var got = await DrainAsync(channel);
        Assert.Empty(got);
        Assert.Equal(10, channel.Violations);
    }

    [Fact]
    public async Task Send_WritesFrame_GuestReadsItBack()
    {
        var (channel, guest) = await PairAsync();
        await using var _ = channel;
        await channel.SendAsync(new OpenUrlCommand("c1", "http://example.test/"), Ct);

        var cmd = await ReadCommandAsync(guest);
        var open = Assert.IsType<OpenUrlCommand>(cmd);
        Assert.Equal("c1", open.CommandId);
        Assert.Equal("http://example.test/", open.Url);
    }

    [Fact]
    public async Task ConcurrentSends_DoNotInterleave()
    {
        var (channel, guest) = await PairAsync();
        await using var _ = channel;
        var big = new string('x', 300_000); // larger than a socket buffer, forces partial writes
        var guestRead = Task.Run(async () =>
        {
            var ids = new List<string>();
            for (var i = 0; i < 50; i++) ids.Add((await ReadCommandAsync(guest)).CommandId);
            return ids;
        }, Ct);

        await Task.WhenAll(Enumerable.Range(0, 50)
            .Select(i => Task.Run(() => channel.SendAsync(new OpenUrlCommand($"c{i}", "http://e.test/" + big), Ct), Ct)));

        var ids = await guestRead;
        Assert.Equal(50, ids.Distinct().Count());
    }

    [Fact]
    public async Task Dispose_EndsRead()
    {
        var (channel, guest) = await PairAsync();
        using var _g = guest;
        var read = Task.Run(() => DrainAsync(channel), Ct);
        await guest.WriteAsync(Frame(Ready), Ct);
        await Task.Delay(100, Ct); // let the reader block on the idle socket

        await channel.DisposeAsync();

        var got = await read.WaitAsync(Ct);
        Assert.Single(got);
    }

    [Fact]
    public async Task Factory_RetriesUntilSocketAppears_ThenTimesOut()
    {
        var vm = VmHandle.For(Guid.NewGuid());
        var vmDir = Path.Combine(_dir, vm.Name);
        Directory.CreateDirectory(vmDir);
        var factory = new VirtioSerialAgentChannelFactory(new StaticMonitor(new AgentChannelOptions
        {
            RunDir = _dir,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        }));

        // Socket absent: times out.
        var quick = new VirtioSerialAgentChannelFactory(new StaticMonitor(new AgentChannelOptions
        {
            RunDir = _dir,
            ConnectTimeout = TimeSpan.FromMilliseconds(600),
        }));
        await Assert.ThrowsAsync<TimeoutException>(() => quick.ConnectAsync(vm, Ct));

        // Socket appears late: connects.
        var connect = factory.ConnectAsync(vm, Ct);
        await Task.Delay(700, Ct);
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(Path.Combine(vmDir, "agent.sock")));
        listener.Listen(1);
        await using var channel = await connect;
        using var server = await listener.AcceptAsync(Ct);
        await server.SendAsync(Frame(Ready), Ct);

        await foreach (var m in channel.ReadAsync(Ct))
        {
            Assert.IsType<ReadyMessage>(m);
            break;
        }
    }

    private async Task<AgentCommand> ReadCommandAsync(Stream s)
    {
        var header = new byte[4];
        await s.ReadExactlyAsync(header, Ct);
        var len = (int)BinaryPrimitives.ReadUInt32BigEndian(header);
        var body = new byte[len];
        await s.ReadExactlyAsync(body, Ct);
        return JsonSerializer.Deserialize<AgentCommand>(body, AgentJson.Options)!;
    }

    private sealed class StaticMonitor(AgentChannelOptions value) : IOptionsMonitor<AgentChannelOptions>
    {
        public AgentChannelOptions CurrentValue => value;
        public AgentChannelOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<AgentChannelOptions, string?> listener) => null;
    }
}
