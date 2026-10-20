using Maldex.Core.Agent;
using Maldex.Core.Domain;
using Maldex.Infrastructure.Isolation;
using Maldex.Infrastructure.Net;
using Xunit;

namespace Maldex.Core.Tests.Isolation;

public class IsolationVerifierTests
{
    private static readonly string[] Names = ["gateway", "control_plane_0", "rfc1918_10", "rfc1918_172", "rfc1918_192", "link_local"];

    private static SelfTestResultMessage Result(string? echo, IEnumerable<string>? reachable = null, IEnumerable<string>? names = null)
    {
        var r = new HashSet<string>(reachable ?? []);
        return new SelfTestResultMessage(DateTimeOffset.UnixEpoch, "c1", (names ?? Names).Select(n => new ProbeResult(n, r.Contains(n))).ToList(), echo);
    }

    [Fact]
    public void SelfTest_AllBlocked_None_NoEcho_Passes()
    {
        var v = IsolationVerifier.Verify(NetworkMode.None, Names, null, Result(null));
        Assert.True(v.Isolated);
        Assert.Null(v.Reason);
    }

    [Fact]
    public void SelfTest_None_AnyEcho_Fails() =>
        Assert.False(IsolationVerifier.Verify(NetworkMode.None, Names, null, Result("203.0.113.9")).Isolated);

    [Theory]
    [InlineData("gateway")]
    [InlineData("control_plane_0")]
    [InlineData("rfc1918_192")]
    [InlineData("link_local")]
    public void SelfTest_InternalReachable_Fails(string probe)
    {
        var v = IsolationVerifier.Verify(NetworkMode.None, Names, null, Result(null, [probe]));
        Assert.False(v.Isolated);
        Assert.Contains(probe, v.Reason);
    }

    [Fact]
    public void SelfTest_Simulated_ParseablePublicIp_Fails()
    {
        Assert.False(IsolationVerifier.Verify(NetworkMode.Simulated, Names, null, Result("203.0.113.77\n")).Isolated);
        Assert.False(IsolationVerifier.Verify(NetworkMode.Simulated, Names, null, Result("8.8.8.8")).Isolated);
        Assert.False(IsolationVerifier.Verify(NetworkMode.Simulated, Names, null, Result("2606:4700:4700::1111")).Isolated);
    }

    [Fact]
    public void SelfTest_Simulated_InetSimPage_Passes()
    {
        Assert.True(IsolationVerifier.Verify(NetworkMode.Simulated, Names, null, Result("<html><body>This is the default HTML page for INetSim HTTP server fake mode.</body></html>")).Isolated);
        Assert.True(IsolationVerifier.Verify(NetworkMode.Simulated, Names, null, Result(null)).Isolated);
        Assert.True(IsolationVerifier.Verify(NetworkMode.Simulated, Names, null, Result("198.18.0.10")).Isolated); // the sinkhole itself
    }

    [Fact]
    public void SelfTest_Vpn_MatchingEgressIp_Passes() =>
        Assert.True(IsolationVerifier.Verify(NetworkMode.LiveVpn, Names, "203.0.113.5", Result(" 203.0.113.5\r\n")).Isolated);

    [Theory]
    [InlineData("203.0.113.6")]
    [InlineData(null)]
    [InlineData("not an ip")]
    [InlineData("")]
    public void SelfTest_Vpn_WrongEgressIp_Fails(string? echo) =>
        Assert.False(IsolationVerifier.Verify(NetworkMode.LiveVpn, Names, "203.0.113.5", Result(echo)).Isolated);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    public void SelfTest_Vpn_NoExpectedEgress_Fails(string? expected) =>
        Assert.False(IsolationVerifier.Verify(NetworkMode.LiveVpn, Names, expected, Result("203.0.113.5")).Isolated);

    [Fact]
    public void SelfTest_LiveTor_FailsClosed() =>
        Assert.False(IsolationVerifier.Verify(NetworkMode.LiveTor, Names, null, Result("203.0.113.5")).Isolated);

    [Fact]
    public void SelfTest_MissingProbe_IsFailure() =>
        Assert.False(IsolationVerifier.Verify(NetworkMode.None, Names, null, Result(null, names: Names.Skip(1))).Isolated);

    [Fact]
    public void SelfTest_ExtraProbe_IsFailure() =>
        Assert.False(IsolationVerifier.Verify(NetworkMode.None, Names, null, Result(null, names: Names.Append("surprise"))).Isolated);

    [Fact]
    public void SelfTest_NoResult_IsFailure()
    {
        var v = IsolationVerifier.Verify(NetworkMode.None, Names, null, null);
        Assert.False(v.Isolated);
        Assert.NotNull(v.Reason);
    }

    [Fact]
    public void SelfTest_DuplicateProbeName_IsFailure()
    {
        // The duplicate hides a reachable entry behind an unreachable one of the same name.
        var probes = Names.Select(n => new ProbeResult(n, false)).Append(new ProbeResult("gateway", true)).ToList();
        Assert.False(IsolationVerifier.Verify(NetworkMode.None, Names, null, new SelfTestResultMessage(DateTimeOffset.UnixEpoch, "c1", probes, null)).Isolated);
        var dupOnlyUnreachable = Names.Select(n => new ProbeResult(n, false)).Append(new ProbeResult("gateway", false)).ToList();
        Assert.False(IsolationVerifier.Verify(NetworkMode.None, Names, null, new SelfTestResultMessage(DateTimeOffset.UnixEpoch, "c1", dupOnlyUnreachable, null)).Isolated);
    }

    [Fact]
    public void SelfTest_EmptyExpectedList_IsFailure() =>
        Assert.False(IsolationVerifier.Verify(NetworkMode.None, [], null, Result(null, names: [])).Isolated);

    [Fact]
    public void Validator_RejectsOversizeResult()
    {
        var ok = Result(null);
        Assert.Null(AgentMessageValidator.Validate(ok));
        Assert.NotNull(AgentMessageValidator.Validate(ok with { EchoBody = new string('x', 257) }));
        Assert.NotNull(AgentMessageValidator.Validate(ok with { Probes = Enumerable.Range(0, 65).Select(i => new ProbeResult("p" + i, false)).ToList() }));
        Assert.NotNull(AgentMessageValidator.Validate(ok with { Probes = [new ProbeResult(new string('n', 65), false)] }));
        Assert.NotNull(AgentMessageValidator.Validate(ok with { CommandId = null! }));
    }

    [Fact]
    public void Protocol_RoundTrips_SelfTestMessages()
    {
        var cmd = new SelfTestCommand("c1", [new ProbeTarget("gateway", "10.213.0.1", [22, 80])], "http://api.ipify.org/");
        var json = System.Text.Json.JsonSerializer.Serialize<AgentCommand>(cmd, AgentJson.Options);
        Assert.Contains("\"type\":\"self_test\"", json);
        Assert.Equal(cmd.Targets[0].Host, ((SelfTestCommand)System.Text.Json.JsonSerializer.Deserialize<AgentCommand>(json, AgentJson.Options)!).Targets[0].Host);
        var msg = System.Text.Json.JsonSerializer.Serialize<AgentMessage>(Result("1.2.3.4"), AgentJson.Options);
        Assert.Contains("\"type\":\"self_test_result\"", msg);
        Assert.IsType<SelfTestResultMessage>(System.Text.Json.JsonSerializer.Deserialize<AgentMessage>(msg, AgentJson.Options));
    }

    [Fact]
    public void ProbePlan_UsesGatewayAndControlPlanes()
    {
        var net = new NetOptions { ControlPlaneIps = ["192.168.1.10", "192.168.1.11"] };
        var plan = IsolationProbePlan.Build(net, new RunNetworkInfo("10.213.0.5", "10.213.0.6", NetworkMode.None));
        Assert.Equal(["gateway", "control_plane_0", "control_plane_1", "rfc1918_10", "rfc1918_172", "rfc1918_192", "link_local"], plan.Targets.Select(t => t.Name));
        Assert.Equal("10.213.0.5", plan.Targets[0].Host);
        Assert.Equal([22, 80, 135, 139, 443, 445, 3389, 5000, 5900, 8080, 8443], plan.Targets[0].Ports);
        Assert.Equal("192.168.1.11", plan.Targets[2].Host);
        Assert.Equal([22, 80, 443, 445, 3389, 5000, 5001, 8080], plan.Targets[1].Ports);
        Assert.Equal([80, 443], plan.Targets[^1].Ports);
        Assert.Equal("169.254.169.254", plan.Targets[^1].Host);
        Assert.Equal(plan.Targets.Select(t => t.Name), plan.ExpectedNames);
        Assert.Equal("http://api.ipify.org/", plan.EchoUrl);
    }

    [Fact]
    public async Task RunNetworkInfo_ReadsLeaseJson_AndRejectsGarbage()
    {
        var dir = Path.Combine(Path.GetTempPath(), "maldex-rni-" + Guid.NewGuid().ToString("N"));
        try
        {
            var taskId = Guid.NewGuid();
            var names = RunNetNames.For(taskId);
            var lease = new Maldex.Core.Abstractions.NetworkLease(taskId, names.Tap);
            var info = new LeaseFileRunNetworkInfo(Microsoft.Extensions.Options.Options.Create(new NetOptions { RunDir = dir }));
            Assert.Null(await info.GetAsync(lease, TestContext.Current.CancellationToken)); // no lease.json: unknown

            var nsDir = Path.Combine(dir, "net", names.Namespace);
            Directory.CreateDirectory(nsDir);
            await File.WriteAllTextAsync(Path.Combine(nsDir, "lease.json"),
                $$"""{"TaskId":"{{taskId}}","Index":65,"Mode":"LiveVpn","ExitCountry":"NL","Bridge":"{{names.Bridge}}","Tap":"{{names.Tap}}","VethHost":"x","VethNs":"y","Namespace":"{{names.Namespace}}","WgIface":"z"}""",
                TestContext.Current.CancellationToken);
            var got = await info.GetAsync(lease, TestContext.Current.CancellationToken);
            Assert.Equal(new RunNetworkInfo("10.213.1.5", "10.213.1.6", NetworkMode.LiveVpn), got);

            await File.WriteAllTextAsync(Path.Combine(nsDir, "lease.json"), "{ not json", TestContext.Current.CancellationToken);
            Assert.Null(await info.GetAsync(lease, TestContext.Current.CancellationToken));
            await File.WriteAllTextAsync(Path.Combine(nsDir, "lease.json"), """{"Index":999999,"Mode":"None"}""", TestContext.Current.CancellationToken);
            Assert.Null(await info.GetAsync(lease, TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task WireGuardExpectedEgress_ReadsComment_MissingIsNull()
    {
        var dir = Path.Combine(Path.GetTempPath(), "maldex-wg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var p = new WireGuardExpectedEgress(Microsoft.Extensions.Options.Options.Create(new NetOptions { WireGuardConfigDir = dir }));
            var ct = TestContext.Current.CancellationToken;
            Assert.Null(await p.GetAsync(NetworkMode.LiveVpn, "NL", ct));
            await File.WriteAllTextAsync(Path.Combine(dir, "NL.conf"),
                "# MaldexEgressIp = 203.0.113.77\n[Interface]\nPrivateKey = aaaa\nAddress = 10.2.0.2/32\nDNS = 10.2.0.1\n[Peer]\nPublicKey = bbbb\nEndpoint = 1.2.3.4:51820\nAllowedIPs = 0.0.0.0/0\n", ct);
            Assert.Equal("203.0.113.77", await p.GetAsync(NetworkMode.LiveVpn, "NL", ct));
            Assert.Null(await p.GetAsync(NetworkMode.LiveVpn, "../NL", ct));
            Assert.Null(await p.GetAsync(NetworkMode.LiveVpn, null, ct));
            Assert.Null(await p.GetAsync(NetworkMode.Simulated, "NL", ct));
            await File.WriteAllTextAsync(Path.Combine(dir, "DE.conf"), "[Interface]\nAddress = 10.2.0.2/32\nDNS = 10.2.0.1\n", ct);
            Assert.Null(await p.GetAsync(NetworkMode.LiveVpn, "DE", ct));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
