using Maldex.Core.Abstractions;
using Maldex.Core.Agent;
using Maldex.Core.Domain;
using Maldex.Infrastructure.Fakes;
using Maldex.Infrastructure.Isolation;
using Maldex.Infrastructure.Net;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Maldex.Integration.Tests.Fixtures;
using Maldex.Orchestrator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Maldex.Integration.Tests.Orchestrator;

/// <summary>The gate on its own: no database, a scripted guest.</summary>
public class SelfTestGateProtocolTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly IsolationProbePlan Plan =
        IsolationProbePlan.Build(new NetOptions { ControlPlaneIps = ["192.168.1.10"] }, new RunNetworkInfo("10.213.0.1", "10.213.0.2", NetworkMode.None));

    private static IEnumerable<AgentMessage> Answer(AgentCommand c, Func<ProbeTarget, bool> reachable, string? echo = null) =>
        c is SelfTestCommand s
            ? [new SelfTestResultMessage(DateTimeOffset.UnixEpoch, s.CommandId, s.Targets.Select(t => new ProbeResult(t.Name, reachable(t))).ToList(), echo, ["10.213.0.2"]),
               new CommandResultMessage(s.CommandId, true)]
            : [];

    [Fact]
    public async Task Gate_SendsSelfTestWithPlan_AndPassesWhenAllBlocked()
    {
        var inbox = new TransferInbox();
        var ch = new ScriptedChannel(inbox, c => Answer(c, _ => false));
        var verdict = await new SelfTestGate(new FakeTimeProvider()).RunAsync(ch, inbox, NetworkMode.None, null, Plan, Ct);
        Assert.True(verdict.Isolated);
        var sent = Assert.IsType<SelfTestCommand>(Assert.Single(ch.Sent));
        Assert.Equal(Plan.Targets, sent.Targets);
        Assert.Equal("http://api.ipify.org/", sent.EchoUrl);
    }

    [Fact]
    public async Task Gate_ReachableGateway_Fails()
    {
        var inbox = new TransferInbox();
        var ch = new ScriptedChannel(inbox, c => Answer(c, t => t.Name == "gateway"));
        var verdict = await new SelfTestGate(new FakeTimeProvider()).RunAsync(ch, inbox, NetworkMode.None, null, Plan, Ct);
        Assert.False(verdict.Isolated);
        Assert.Contains("gateway", verdict.Reason);
    }

    [Fact]
    public async Task Gate_GuestMustReportTheLeaseAddress()
    {
        var inbox = new TransferInbox();
        var ch = new ScriptedChannel(inbox, c => c is SelfTestCommand s
            ? [new SelfTestResultMessage(DateTimeOffset.UnixEpoch, s.CommandId, s.Targets.Select(t => new ProbeResult(t.Name, false)).ToList(), null, ["10.213.0.6"])]
            : []);
        var verdict = await new SelfTestGate(new FakeTimeProvider()).RunAsync(ch, inbox, NetworkMode.None, null, Plan, Ct);
        Assert.False(verdict.Isolated);
        Assert.Contains("10.213.0.2", verdict.Reason);
    }

    [Fact]
    public async Task SelfTest_Vpn_WrongEgressIp_Fails()
    {
        var inbox = new TransferInbox();
        var ch = new ScriptedChannel(inbox, c => Answer(c, _ => false, "198.51.100.9"));
        var verdict = await new SelfTestGate(new FakeTimeProvider()).RunAsync(ch, inbox, NetworkMode.LiveVpn, "203.0.113.5", Plan, Ct);
        Assert.False(verdict.Isolated);
    }

    [Fact]
    public async Task SelfTest_Vpn_MatchingEgressIp_Passes()
    {
        var inbox = new TransferInbox();
        var ch = new ScriptedChannel(inbox, c => Answer(c, _ => false, "203.0.113.5\n"));
        var verdict = await new SelfTestGate(new FakeTimeProvider()).RunAsync(ch, inbox, NetworkMode.LiveVpn, "203.0.113.5", Plan, Ct);
        Assert.True(verdict.Isolated);
    }

    private static readonly IsolationProbePlan DirectPlan =
        IsolationProbePlan.Build(new NetOptions { ControlPlaneIps = ["192.168.1.10"] }, new RunNetworkInfo("10.213.0.1", "10.213.0.2", NetworkMode.Direct));

    [Fact]
    public async Task SelfTest_Direct_ProbesDirectUrl_AndPassesWhenInternalBlockedAndPublicReachable()
    {
        var inbox = new TransferInbox();
        var ch = new ScriptedChannel(inbox, c => Answer(c, _ => false, ""));
        var verdict = await new SelfTestGate(new FakeTimeProvider()).RunAsync(ch, inbox, NetworkMode.Direct, null, DirectPlan, Ct);
        Assert.True(verdict.Isolated);
        Assert.Equal("http://connectivitycheck.gstatic.com/generate_204", Assert.IsType<SelfTestCommand>(Assert.Single(ch.Sent)).EchoUrl);
    }

    [Fact]
    public async Task SelfTest_Direct_NoInternet_Fails()
    {
        var inbox = new TransferInbox();
        var ch = new ScriptedChannel(inbox, c => Answer(c, _ => false, null));
        Assert.False((await new SelfTestGate(new FakeTimeProvider()).RunAsync(ch, inbox, NetworkMode.Direct, null, DirectPlan, Ct)).Isolated);
    }

    [Fact]
    public async Task SelfTest_Direct_ReachableControlPlane_Fails()
    {
        var inbox = new TransferInbox();
        var ch = new ScriptedChannel(inbox, c => Answer(c, t => t.Name == "control_plane_0", ""));
        var verdict = await new SelfTestGate(new FakeTimeProvider()).RunAsync(ch, inbox, NetworkMode.Direct, null, DirectPlan, Ct);
        Assert.False(verdict.Isolated);
        Assert.Contains("control_plane_0", verdict.Reason);
    }

    [Fact]
    public async Task Gate_AnswerForAnotherCommand_IsIgnored_ThenTimesOut()
    {
        var time = new FakeTimeProvider();
        var inbox = new TransferInbox();
        var ch = new ScriptedChannel(inbox, c => [new SelfTestResultMessage(DateTimeOffset.UnixEpoch, "someone-else", Plan.Targets.Select(t => new ProbeResult(t.Name, false)).ToList(), null)]);
        var run = new SelfTestGate(time).RunAsync(ch, inbox, NetworkMode.None, null, Plan, Ct);
        await Pump(time, run);
        var verdict = await run;
        Assert.False(verdict.Isolated);
        Assert.Contains("60 s", verdict.Reason);
    }

    [Fact]
    public async Task SelfTest_NoResult_IsFailure_AfterTimeout()
    {
        var time = new FakeTimeProvider();
        var inbox = new TransferInbox();
        var ch = new ScriptedChannel(inbox, _ => []);
        var run = new SelfTestGate(time).RunAsync(ch, inbox, NetworkMode.None, null, Plan, Ct);
        await Pump(time, run);
        var verdict = await run;
        Assert.False(verdict.Isolated);
        Assert.Equal("no self-test result within 60 s", verdict.Reason);
    }

    [Fact]
    public async Task Gate_AgentRefuses_FailsFast()
    {
        var inbox = new TransferInbox();
        var ch = new ScriptedChannel(inbox, c => [new CommandResultMessage(c.CommandId, false, "unsupported command")]);
        var verdict = await new SelfTestGate(new FakeTimeProvider()).RunAsync(ch, inbox, NetworkMode.None, null, Plan, Ct);
        Assert.False(verdict.Isolated);
        Assert.Contains("unsupported command", verdict.Reason);
    }

    [Fact]
    public async Task Gate_ChannelCloses_Fails()
    {
        var inbox = new TransferInbox();
        var ch = new ScriptedChannel(inbox, _ => []);
        var run = new SelfTestGate(new FakeTimeProvider()).RunAsync(ch, inbox, NetworkMode.None, null, Plan, Ct);
        await Task.Delay(50, Ct);
        inbox.Complete();
        var verdict = await run;
        Assert.False(verdict.Isolated);
        Assert.Contains("disconnected", verdict.Reason);
    }

    [Fact]
    public async Task Gate_Stop_Throws()
    {
        var time = new FakeTimeProvider();
        var inbox = new TransferInbox();
        var ch = new ScriptedChannel(inbox, _ => []);
        var run = new SelfTestGate(time).RunAsync(ch, inbox, NetworkMode.None, null, Plan, Ct, _ => Task.FromResult(true));
        await Assert.ThrowsAsync<SelfTestStoppedException>(() => run);
    }

    [Fact]
    public async Task Inbox_UnsolicitedSelfTestResult_IsSwallowed()
    {
        var inbox = new TransferInbox();
        Assert.True(await inbox.OfferAsync(new SelfTestResultMessage(DateTimeOffset.UnixEpoch, "x", [], null), Ct));
    }

    private static async Task Pump(FakeTimeProvider time, Task run)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!run.IsCompleted)
        {
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), "gate did not finish");
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(2, Ct);
        }
    }
}

[Collection("sql")]
public class SelfTestGateTests(SqlServerFixture sql)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(AnalysisTask Task, List<TaskStatusHistory> History, List<AuditLog> Audit)> LoadAsync(OrchestratorHarness h, Guid id)
    {
        await using var db = h.NewContext();
        var task = await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == id, Ct);
        var history = (await db.TaskStatusHistory.Where(x => x.TaskId == id).ToListAsync(Ct)).OrderBy(x => x.Id).ToList();
        var audit = await db.AuditLogs.Where(a => a.Target == id.ToString() && a.Action.StartsWith("isolation.")).ToListAsync(Ct);
        return (task, history, audit);
    }

    private static List<AgentCommand> Sent(OrchestratorHarness h) => h.Channels.Channels.SelectMany(c => c.SentCommands).ToList();

    [Fact]
    public async Task SelfTest_Pass_ProceedsToRunning()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql);
        var id = await h.SeedPreparingTaskAsync();
        await h.RunAsync(id);

        var (task, history, audit) = await LoadAsync(h, id);
        Assert.Equal(RunStatus.Processing, task.Status);
        Assert.Contains(history, x => x.To == RunStatus.Running && x.Reason == "agent ready; isolation self-test passed");
        var row = Assert.Single(audit);
        Assert.Equal("isolation.selftest.passed", row.Action);
        var sent = Sent(h);
        Assert.IsType<SelfTestCommand>(sent[0]);
        Assert.IsType<PutFileChunkCommand>(sent[1]);
        Assert.Contains(sent, c => c is OpenSampleCommand);
    }

    [Fact]
    public async Task SelfTest_InternalReachable_FailsInPreparing()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql);
        h.Get<FakeGuest>().SetReachable("gateway");
        var id = await h.SeedPreparingTaskAsync();
        await h.RunAsync(id);

        var (task, history, audit) = await LoadAsync(h, id);
        Assert.Equal(RunStatus.Failed, task.Status);
        Assert.Equal("isolation self-test failed: gateway is reachable from the guest", task.FailureReason);
        Assert.Equal(RunStatus.Preparing, history.Last().From);
        Assert.Equal(1, task.Attempt); // not retried
        Assert.DoesNotContain(history, x => x.To == RunStatus.Running);
        Assert.Equal("isolation.selftest.failed", Assert.Single(audit).Action);
        var sent = Sent(h);
        Assert.Single(sent);
        Assert.IsType<SelfTestCommand>(sent[0]);
        Assert.DoesNotContain(sent, c => c is PutFileChunkCommand or OpenSampleCommand or OpenUrlCommand);
        Assert.Empty(await h.Get<IHypervisor>().ListAsync(Ct));
        Assert.Empty(await h.Get<IEgressRouter>().ListAsync(Ct));
    }

    [Fact]
    public async Task SelfTest_Simulated_PublicIpEcho_FailsInPreparing()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql);
        h.Get<FakeGuest>().SelfTestEchoBody = "8.8.8.8";
        var id = await h.SeedPreparingTaskAsync(NetworkMode.Simulated);
        await h.RunAsync(id);

        var (task, _, _) = await LoadAsync(h, id);
        Assert.Equal(RunStatus.Failed, task.Status);
        Assert.StartsWith("isolation self-test failed:", task.FailureReason);
        Assert.DoesNotContain(Sent(h), c => c is PutFileChunkCommand);
    }

    [Fact]
    public async Task SelfTest_None_AnyEcho_FailsInPreparing()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql);
        h.Get<FakeGuest>().SelfTestEchoBody = "<html>hi</html>";
        var id = await h.SeedPreparingTaskAsync(NetworkMode.None);
        await h.RunAsync(id);

        Assert.Equal(RunStatus.Failed, (await LoadAsync(h, id)).Task.Status);
    }

    [Fact]
    public async Task SelfTest_FakeSimulated_InetSimEcho_Passes()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql);
        var id = await h.SeedPreparingTaskAsync(NetworkMode.Simulated);
        await h.RunAsync(id);

        Assert.Equal(RunStatus.Processing, (await LoadAsync(h, id)).Task.Status);
    }

    [Fact]
    public async Task SelfTest_Simulated_NoInetSimAnswer_FailsInPreparing()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql);
        h.Get<FakeGuest>().SelfTestEchoBody = "198.18.0.10"; // an IP echo service answered, not INetSim
        var id = await h.SeedPreparingTaskAsync(NetworkMode.Simulated);
        await h.RunAsync(id);

        var (task, _, _) = await LoadAsync(h, id);
        Assert.Equal(RunStatus.Failed, task.Status);
        Assert.Contains("not INetSim", task.FailureReason);
    }

    [Fact]
    public async Task SelfTest_GuestWithoutLeaseAddress_FailsInPreparing()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql);
        h.Get<FakeGuest>().SelfTestGuestAddresses = ["169.254.3.3"]; // APIPA: the guest never got the run's DHCP lease
        var id = await h.SeedPreparingTaskAsync(NetworkMode.None);
        await h.RunAsync(id);

        var (task, _, _) = await LoadAsync(h, id);
        Assert.Equal(RunStatus.Failed, task.Status);
        Assert.Contains("does not hold its lease address 10.213.0.2", task.FailureReason);
        Assert.DoesNotContain(Sent(h), c => c is PutFileChunkCommand or OpenSampleCommand or OpenUrlCommand);
    }

    [Fact]
    public async Task SelfTest_FakeLiveVpn_EchoesConfiguredExit_Passes()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql, new Dictionary<string, string?> { ["Maldex:Fake:Egress:LiveAvailable"] = "true" });
        var id = await h.SeedPreparingTaskAsync(NetworkMode.LiveVpn);
        await h.RunAsync(id);

        Assert.Equal(RunStatus.Processing, (await LoadAsync(h, id)).Task.Status);
    }

    [Fact]
    public async Task SelfTest_FakeLiveVpn_WrongEcho_Fails()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql, new Dictionary<string, string?> { ["Maldex:Fake:Egress:LiveAvailable"] = "true" });
        h.Get<FakeGuest>().SelfTestEchoBody = "198.51.100.200";
        var id = await h.SeedPreparingTaskAsync(NetworkMode.LiveVpn);
        await h.RunAsync(id);

        var (task, _, _) = await LoadAsync(h, id);
        Assert.Equal(RunStatus.Failed, task.Status);
        Assert.Contains("is not the VPN exit", task.FailureReason);
    }

    [Fact]
    public async Task SelfTest_GuestNeverAnswers_FailsClosedAfter60Seconds()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql);
        h.Get<FakeGuest>().SilentSelfTest = true;
        var id = await h.SeedPreparingTaskAsync();
        await h.RunAsync(id);

        var (task, _, _) = await LoadAsync(h, id);
        Assert.Equal(RunStatus.Failed, task.Status);
        Assert.Equal("isolation self-test failed: no self-test result within 60 s", task.FailureReason);
        Assert.DoesNotContain(Sent(h), c => c is PutFileChunkCommand);
    }

    [Fact]
    public async Task SelfTest_StopRequested_CancelsFromPreparing()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql);
        h.Get<FakeGuest>().SilentSelfTest = true;
        var id = await h.SeedPreparingTaskAsync();

        await h.RunAsync(id, async () =>
        {
            // Deterministic: stop only once the self-test is actually waiting for its answer.
            while (!h.Channels.Channels.Any(c => c.SentCommands.Any(x => x is SelfTestCommand))) await Task.Delay(2, Ct);
            await using var api = h.NewContext();
            var t = await api.Tasks.SingleAsync(x => x.Id == id, Ct);
            t.StopRequestedAt = h.Time.GetUtcNow();
            await api.SaveChangesAsync(Ct);
        });

        var (task, history, _) = await LoadAsync(h, id);
        Assert.Equal(RunStatus.Cancelled, task.Status);
        Assert.Equal(RunStatus.Preparing, history.Last().From);
        Assert.Equal("stopped by analyst", history.Last().Reason);
        Assert.Empty(await h.Get<IHypervisor>().ListAsync(Ct));
    }

    private sealed class StubNetworkInfo(Func<RunNetworkInfo?> answer) : IRunNetworkInfo
    {
        public Task<RunNetworkInfo?> GetAsync(NetworkLease lease, CancellationToken ct) => Task.FromResult(answer());
    }

    private static Action<IServiceCollection> RequireControlPlane(bool value) =>
        services => services.PostConfigure<SelfTestOptions>(o => o.RequireControlPlaneProbes = value);

    [Fact]
    public async Task SelfTest_RequireControlPlane_NoControlPlaneIps_FailsClosed_AndIsAudited()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql, configure: RequireControlPlane(true));
        var id = await h.SeedPreparingTaskAsync();
        await h.RunAsync(id);

        var (task, _, audit) = await LoadAsync(h, id);
        Assert.Equal(RunStatus.Failed, task.Status);
        Assert.Equal("isolation self-test failed: no control-plane probe targets configured", task.FailureReason);
        Assert.Equal("isolation.selftest.failed", Assert.Single(audit).Action);
        Assert.DoesNotContain(Sent(h), c => c is PutFileChunkCommand or OpenSampleCommand);
    }

    [Fact]
    public async Task SelfTest_RequireControlPlane_WithControlPlaneIps_Passes()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql,
            new Dictionary<string, string?> { ["Maldex:Real:Net:ControlPlaneIps:0"] = "192.168.1.10" }, RequireControlPlane(true));
        var id = await h.SeedPreparingTaskAsync();
        await h.RunAsync(id);

        Assert.Equal(RunStatus.Processing, (await LoadAsync(h, id)).Task.Status);
        var self = Sent(h).OfType<SelfTestCommand>().Single();
        Assert.Contains(self.Targets, t => t.Name == "control_plane_0" && t.Host == "192.168.1.10");
    }

    [Fact]
    public async Task SelfTest_NotRequired_NoControlPlaneIps_Passes()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql, configure: RequireControlPlane(false));
        var id = await h.SeedPreparingTaskAsync();
        await h.RunAsync(id);

        Assert.Equal(RunStatus.Processing, (await LoadAsync(h, id)).Task.Status);
    }

    [Fact]
    public async Task SelfTest_LeaseModeDiffersFromTaskMode_Fails()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql, configure: services =>
            services.AddSingleton<IRunNetworkInfo>(new StubNetworkInfo(() => new RunNetworkInfo("10.213.0.1", "10.213.0.2", NetworkMode.LiveVpn))));
        var id = await h.SeedPreparingTaskAsync(NetworkMode.None);
        await h.RunAsync(id);

        var (task, _, audit) = await LoadAsync(h, id);
        Assert.Equal(RunStatus.Failed, task.Status);
        Assert.Equal("isolation self-test failed: network mode mismatch: lease LiveVpn, task None", task.FailureReason);
        Assert.Equal("isolation.selftest.failed", Assert.Single(audit).Action);
        Assert.Empty(Sent(h)); // not even the probes were sent
    }

    [Fact]
    public async Task SelfTest_UnknownLease_Fails()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql, configure: services =>
            services.AddSingleton<IRunNetworkInfo>(new StubNetworkInfo(() => null)));
        var id = await h.SeedPreparingTaskAsync();
        await h.RunAsync(id);

        Assert.Equal(RunStatus.Failed, (await LoadAsync(h, id)).Task.Status);
    }

    [Fact]
    public async Task SelfTest_UnexpectedException_FailsAndIsAudited()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql, configure: services =>
            services.AddSingleton<IRunNetworkInfo>(new StubNetworkInfo(() => throw new InvalidOperationException("boom"))));
        var id = await h.SeedPreparingTaskAsync();
        await h.RunAsync(id);

        var (task, _, audit) = await LoadAsync(h, id);
        Assert.Equal(RunStatus.Failed, task.Status);
        Assert.Contains("boom", task.FailureReason);
        Assert.Equal("isolation.selftest.failed", Assert.Single(audit).Action);
    }
}
