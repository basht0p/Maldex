using System.Net;
using System.Net.Sockets;
using Maldex.Agent.Probes;
using Maldex.Core.Agent;
using Xunit;

namespace Maldex.Agent.Tests;

public class SelfTestProbeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static int FreeClosedPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public async Task Probes_ClosedPort_NotReachable()
    {
        var result = await new SelfTestProbes().RunAsync(new SelfTestCommand("c1", [new ProbeTarget("t", "127.0.0.1", [FreeClosedPort()])], ""), Ct);
        Assert.Equal("c1", result.CommandId);
        Assert.Equal([new ProbeResult("t", false)], result.Probes);
        Assert.Null(result.EchoBody);
    }

    [Fact]
    public async Task Probes_ListeningPort_Reachable()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var open = ((IPEndPoint)l.LocalEndpoint).Port;
        var result = await new SelfTestProbes().RunAsync(new SelfTestCommand("c1",
            [new ProbeTarget("any_open", "127.0.0.1", [FreeClosedPort(), open]), new ProbeTarget("none_open", "127.0.0.1", [FreeClosedPort()])], ""), Ct);
        Assert.Equal([new ProbeResult("any_open", true), new ProbeResult("none_open", false)], result.Probes);
    }

    [Fact]
    public async Task Probes_InvalidHostOrNoPorts_NotReachable()
    {
        var result = await new SelfTestProbes().RunAsync(new SelfTestCommand("c1",
            [new ProbeTarget("bad", "not a host!", [80]), new ProbeTarget("noports", "127.0.0.1", [])], ""), Ct);
        Assert.Equal(2, result.Probes.Count);
        Assert.All(result.Probes, p => Assert.False(p.Reachable));
    }

    [Fact]
    public async Task Probes_Timeout_NotReachable_AndBounded()
    {
        var probes = new SelfTestProbes(connectTimeout: TimeSpan.FromMilliseconds(100), connect: async (h, p, ct) => { await Task.Delay(Timeout.Infinite, ct); });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await probes.RunAsync(new SelfTestCommand("c1", [new ProbeTarget("t", "10.0.0.1", [80, 443])], ""), Ct);
        Assert.False(result.Probes[0].Reachable);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Probes_ConcurrencyIsBounded()
    {
        int current = 0, max = 0;
        var probes = new SelfTestProbes(connect: async (h, p, ct) =>
        {
            var now = Interlocked.Increment(ref current);
            int seen;
            while ((seen = Volatile.Read(ref max)) < now && Interlocked.CompareExchange(ref max, now, seen) != seen) { }
            await Task.Delay(30, ct);
            Interlocked.Decrement(ref current);
            throw new IOException("refused");
        });
        var ports = Enumerable.Range(1, 64).ToList();
        var targets = Enumerable.Range(0, 5).Select(i => new ProbeTarget("t" + i, "10.0.0." + (i + 1), ports)).ToList();
        var result = await probes.RunAsync(new SelfTestCommand("c1", targets, ""), Ct);
        Assert.All(result.Probes, p => Assert.False(p.Reachable));
        Assert.InRange(max, 1, 64);
    }

    [Fact]
    public async Task Echo_Success_TruncatesTo256()
    {
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        var serve = Task.Run(async () =>
        {
            using var c = await server.AcceptTcpClientAsync(Ct);
            var s = c.GetStream();
            var buf = new byte[4096];
            _ = await s.ReadAsync(buf, Ct);
            var body = new string('a', 1000);
            await s.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"), Ct);
        }, Ct);
        var result = await new SelfTestProbes().RunAsync(new SelfTestCommand("c1", [], $"http://127.0.0.1:{port}/"), Ct);
        await serve;
        Assert.Equal(new string('a', 256), result.EchoBody);
    }

    [Fact]
    public async Task Echo_Failure_IsNull()
    {
        var result = await new SelfTestProbes().RunAsync(new SelfTestCommand("c1", [], $"http://127.0.0.1:{FreeClosedPort()}/"), Ct);
        Assert.Null(result.EchoBody);
    }

    [Fact]
    public async Task Echo_NonHttpUrl_IsNull_NoThrow()
    {
        var result = await new SelfTestProbes().RunAsync(new SelfTestCommand("c1", [], "file:///c:/windows/win.ini"), Ct);
        Assert.Null(result.EchoBody);
    }

    private static readonly SelfTestCommand Empty = new("c1", [], "");

    [Fact]
    public async Task Addresses_ReportsTheAddressesOfTheGatewayInterface()
    {
        var probes = new SelfTestProbes(addresses: () => [new("169.254.7.7", false), new("10.213.0.2", true), new("fe80::5", false)]);
        var result = await probes.RunAsync(Empty, Ct);
        Assert.Equal(["10.213.0.2"], result.GuestAddresses!);
        Assert.Null(AgentMessageValidator.Validate(result));
    }

    [Fact]
    public async Task Addresses_WaitsForADefaultGateway()
    {
        var calls = 0;
        var probes = new SelfTestProbes(addressPoll: TimeSpan.FromMilliseconds(5), addresses: () =>
            Interlocked.Increment(ref calls) < 4 ? [new("169.254.7.7", false)] : [new("10.213.0.2", true)]);
        var result = await probes.RunAsync(Empty, Ct);
        Assert.Equal(["10.213.0.2"], result.GuestAddresses!);
        Assert.True(calls >= 4);
    }

    [Fact]
    public async Task Addresses_NoGatewayInTime_ReportsWhatItHas_Bounded()
    {
        var probes = new SelfTestProbes(addressWait: TimeSpan.FromMilliseconds(150), addressPoll: TimeSpan.FromMilliseconds(10),
            addresses: () => [new("169.254.7.7", false)]);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await probes.RunAsync(Empty, Ct);
        Assert.Equal(["169.254.7.7"], result.GuestAddresses!);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Addresses_AreCappedAndNeverThrow()
    {
        var many = Enumerable.Range(1, 40).Select(i => new GuestAddress("10.1.0." + i, true)).ToList();
        Assert.Equal(16, (await new SelfTestProbes(addresses: () => many).RunAsync(Empty, Ct)).GuestAddresses!.Count);
        var broken = await new SelfTestProbes(addressWait: TimeSpan.FromMilliseconds(50), addressPoll: TimeSpan.FromMilliseconds(10),
            addresses: () => throw new System.Net.NetworkInformation.NetworkInformationException()).RunAsync(Empty, Ct);
        Assert.Empty(broken.GuestAddresses!);
    }

    [Fact]
    public void Addresses_DefaultSource_ReadsThisHost()
    {
        // the real source on the dev host: some non-loopback address is visible and every entry parses
        var all = SelfTestProbes.ReadInterfaceAddresses();
        Assert.All(all, a => Assert.True(IPAddress.TryParse(a.Address, out _)));
        Assert.DoesNotContain(all, a => IPAddress.IsLoopback(IPAddress.Parse(a.Address)));
    }

    [Fact]
    public async Task Dispatcher_SelfTest_EmitsResultThenAcknowledges()
    {
        var emitted = new List<AgentMessage>();
        using var d = new Maldex.Agent.Commands.CommandDispatcher(new NoopSampleOpener(), new RecordingUrlOpener(), () => { });
        var ack = await d.DispatchAsync(new SelfTestCommand("c9", [new ProbeTarget("t", "127.0.0.1", [FreeClosedPort()])], ""), Ct,
            (m, _) => { emitted.Add(m); return Task.CompletedTask; });
        Assert.True(ack.Success);
        var r = Assert.IsType<SelfTestResultMessage>(Assert.Single(emitted));
        Assert.Equal("c9", r.CommandId);
        Assert.Null(AgentMessageValidator.Validate(r));
    }
}
