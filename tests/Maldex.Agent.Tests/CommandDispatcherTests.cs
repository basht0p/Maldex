using Maldex.Agent.Commands;
using Maldex.Core.Agent;
using Xunit;

namespace Maldex.Agent.Tests;

public class CommandDispatcherTests
{
    private sealed record UnknownCommand(string CommandId) : AgentCommand(CommandId);

    private static CommandDispatcher Make(RecordingUrlOpener url, Action? stop = null) =>
        new(new NoopSampleOpener(), url, stop ?? (() => { }));

    [Fact]
    public async Task Dispatcher_OpenUrl_InvokesOpener_AndReportsSuccess()
    {
        var url = new RecordingUrlOpener();
        var res = await Make(url).DispatchAsync(new OpenUrlCommand("c1", "http://example.test/"), CancellationToken.None);
        Assert.Equal(new CommandResultMessage("c1", true), res);
        Assert.Equal(["http://example.test/"], url.Opened);
    }

    [Fact]
    public async Task Dispatcher_OpenerThrows_ReportsFailure_NotCrash()
    {
        var url = new RecordingUrlOpener { Throw = new InvalidOperationException("boom") };
        var res = await Make(url).DispatchAsync(new OpenUrlCommand("c2", "http://x/"), CancellationToken.None);
        Assert.Equal("c2", res.CommandId);
        Assert.False(res.Success);
        Assert.Contains("boom", res.Error);
    }

    [Fact]
    public async Task Dispatcher_UnknownCommand_ReportsFailure_NotCrash()
    {
        var res = await Make(new RecordingUrlOpener()).DispatchAsync(new UnknownCommand("c3"), CancellationToken.None);
        Assert.Equal(new CommandResultMessage("c3", false, "unsupported command"), res);
    }

    [Fact]
    public async Task Dispatcher_Shutdown_ReportsSuccess_AndRequestsStop()
    {
        var stopped = 0;
        var d = Make(new RecordingUrlOpener(), () => stopped++);
        var cmd = new ShutdownCommand("c4");
        var res = await d.DispatchAsync(cmd, CancellationToken.None);
        Assert.True(res.Success);
        Assert.Equal(0, stopped); // stop is requested only after the result has been sent
        d.AfterResultSent(cmd);
        Assert.Equal(1, stopped);
    }

    [Fact]
    public async Task Dispatcher_OpenSample_WithoutDeliveredTransfer_Fails()
    {
        var res = await Make(new RecordingUrlOpener()).DispatchAsync(new OpenSampleCommand("c5", "a.exe", "k"), CancellationToken.None);
        Assert.False(res.Success);
        Assert.Contains("incomplete transfer", res.Error);
    }

    private sealed class FakeProbe(ProvenanceReport report) : IProvenanceProbe
    {
        public int Calls;
        public ProvenanceReport Probe() { Calls++; return report; }
    }

    [Fact]
    public async Task Dispatcher_Provenance_EmitsProbeValuesThenSucceeds()
    {
        var probe = new FakeProbe(new ProvenanceReport("Windows 11 Pro", "26300.1", true, null));
        var d = new CommandDispatcher(new NoopSampleOpener(), new RecordingUrlOpener(), () => { }, provenance: probe);
        var emitted = new List<AgentMessage>();
        var res = await d.DispatchAsync(new ProvenanceCommand("p1"), CancellationToken.None, (m, _) => { emitted.Add(m); return Task.CompletedTask; });
        Assert.Equal(new CommandResultMessage("p1", true), res);
        Assert.Equal(new ProvenanceMessage("p1", "Windows 11 Pro", "26300.1", AgentHost.BuildId, true, null), Assert.Single(emitted));
        Assert.Equal(1, probe.Calls);
    }

    [Fact]
    public async Task Dispatcher_Provenance_WithoutEmitOrProbe_Fails()
    {
        var withProbe = new CommandDispatcher(new NoopSampleOpener(), new RecordingUrlOpener(), () => { }, provenance: new FakeProbe(new ProvenanceReport("e", "b", null, null)));
        Assert.False((await withProbe.DispatchAsync(new ProvenanceCommand("p2"), CancellationToken.None)).Success);
        var noProbe = Make(new RecordingUrlOpener());
        Assert.False((await noProbe.DispatchAsync(new ProvenanceCommand("p3"), CancellationToken.None, (_, _) => Task.CompletedTask)).Success);
    }

    [Fact]
    public void AgentBuildId_HasVersionPlusHash()
    {
        Assert.Matches("^" + System.Text.RegularExpressions.Regex.Escape(AgentHost.Version) + @"\+[0-9a-f]{12}$", AgentHost.BuildId);
    }
}
