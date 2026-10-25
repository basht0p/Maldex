using Maldex.Core.Abstractions;
using Maldex.Core.Agent;
using Maldex.Orchestrator;
using Maldex.Orchestrator.Templates;
using Xunit;

namespace Maldex.Integration.Tests.Orchestrator;

/// <summary>What a scripted guest reports for real-time protection at one reading (<see cref="DefenderScriptChannel"/>).</summary>
internal enum DefenderReading { Off, On, Unknown, Refused, Silent }

/// <summary>
/// A channel whose provenance answers report real-time protection as <paramref name="script"/> says for the time since its first
/// provenance answer (on <paramref name="time"/>; so the readings are scripted, not how long the boot took), as a guest whose Defender
/// is still settling after boot; <c>Refused</c> turns the answer into a refusal, <c>Silent</c> drops it (a hung agent). Every
/// answer's time is recorded.
/// </summary>
internal sealed class DefenderScriptChannel(IAgentChannel inner, TimeProvider time, Func<TimeSpan, DefenderReading> script) : IAgentChannel
{
    private DateTimeOffset? _first;
    private readonly List<TimeSpan> _readings = [];
    private readonly HashSet<string> _silenced = [];

    /// <summary>When (since the first one) each provenance answer was read.</summary>
    public IReadOnlyList<TimeSpan> Readings { get { lock (_readings) return _readings.ToList(); } }

    public int Violations => inner.Violations;
    public Task SendAsync(AgentCommand command, CancellationToken ct) => inner.SendAsync(command, ct);
    public ValueTask DisposeAsync() => inner.DisposeAsync();

    public async IAsyncEnumerable<AgentMessage> ReadAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var m in inner.ReadAsync(ct))
        {
            if (m is CommandResultMessage r && _silenced.Contains(r.CommandId)) continue; // a dropped answer's trailing result
            if (m is not ProvenanceMessage p)
            {
                yield return m;
                continue;
            }
            TimeSpan at;
            lock (_readings)
            {
                _first ??= time.GetUtcNow();
                at = time.GetUtcNow() - _first.Value;
                _readings.Add(at);
            }
            var reading = script(at);
            if (reading == DefenderReading.Silent)
            {
                _silenced.Add(p.CommandId);
                continue;
            }
            yield return reading switch
            {
                DefenderReading.Off => p with { RealTimeProtectionEnabled = false },
                DefenderReading.On => p with { RealTimeProtectionEnabled = true },
                DefenderReading.Unknown => p with { RealTimeProtectionEnabled = null },
                _ => new CommandResultMessage(p.CommandId, false, "the Defender query failed"),
            };
        }
    }
}

public class TemplateVerifierTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TemplateJobKit Kit(Action<Maldex.Infrastructure.Fakes.FakeGuest, ComponentBundle>? tune = null)
    {
        var kit = new TemplateJobKit();
        kit.Guest.ProvenanceAgentVersion = kit.Bundle.AgentBuildId;
        kit.Guest.ProvenanceRealTimeProtection = false;
        tune?.Invoke(kit.Guest, kit.Bundle);
        return kit;
    }

    [Fact]
    public async Task Verify_Passes_AndReturnsTheProvenance_AndCleansUp()
    {
        await using var kit = Kit((g, _) => { g.ProvenanceOfficePresent = true; g.ProvenanceEdition = "Windows 11 Enterprise"; g.ProvenanceBuild = "26300.2"; });

        var p = await kit.Verifier.VerifyAsync("win11-v3", kit.Bundle, Ct);

        Assert.Equal(new VerifiedProvenance("Windows 11 Enterprise", "26300.2", kit.Bundle.AgentBuildId, true, false, null), p);
        Assert.Empty(await kit.Hypervisor.ListAsync(Ct)); // the verification VM is destroyed
    }

    [Fact]
    public async Task Verify_RealTimeProtectionUnknown_PassesWithANote()
    {
        await using var kit = Kit((g, _) => g.ProvenanceRealTimeProtection = null);

        var p = await kit.Verifier.VerifyAsync("win11-v3", kit.Bundle, Ct);

        Assert.Null(p.RealTimeProtectionEnabled);
        Assert.Equal("Defender state could not be read", p.Note);
    }

    [Fact]
    public async Task Verify_RealTimeProtectionOn_Fails()
    {
        await using var kit = Kit((g, _) => g.ProvenanceRealTimeProtection = true);

        var ex = await Assert.ThrowsAsync<TemplateVerifyException>(() => kit.Verifier.VerifyAsync("win11-v3", kit.Bundle, Ct));

        Assert.Equal("Defender real-time protection is on; turn it off in an Edit session", ex.Message);
        Assert.Empty(await kit.Hypervisor.ListAsync(Ct));
    }

    [Fact]
    public async Task Verify_WrongAgentBuild_Fails()
    {
        await using var kit = Kit((g, _) => g.ProvenanceAgentVersion = "1.0.0+deadbeef0000");

        var ex = await Assert.ThrowsAsync<TemplateVerifyException>(() => kit.Verifier.VerifyAsync("win11-v3", kit.Bundle, Ct));

        Assert.Equal("the sealed agent is not the current build", ex.Message);
        Assert.Empty(await kit.Hypervisor.ListAsync(Ct));
    }

    [Fact]
    public async Task Verify_AgentNeverReady_TimesOut()
    {
        await using var kit = Kit();
        kit.VerifyAgentReady = false;
        kit.Options.VerifyTimeout = TimeSpan.FromMilliseconds(400);

        var ex = await Assert.ThrowsAsync<TemplateVerifyException>(() => kit.Verifier.VerifyAsync("win11-v3", kit.Bundle, Ct));

        Assert.Contains("did not become ready", ex.Message);
        Assert.Empty(await kit.Hypervisor.ListAsync(Ct));
    }

    [Fact]
    public async Task Verify_NoProvenanceAnswer_TimesOut()
    {
        await using var kit = Kit();
        kit.Guest.SilentProvenance = true;
        kit.Options.VerifyTimeout = TimeSpan.FromMilliseconds(400);

        var ex = await Assert.ThrowsAsync<TemplateVerifyException>(() => kit.Verifier.VerifyAsync("win11-v3", kit.Bundle, Ct));

        Assert.Contains("provenance", ex.Message);
        Assert.Empty(await kit.Hypervisor.ListAsync(Ct));
    }

    [Fact]
    public async Task Verify_AProvenanceRequestLostInTheReconnectGap_IsResent()
    {
        await using var kit = Kit();
        kit.DropProvenanceRequests = 1; // the first request is lost; only a resend is answered
        kit.Options.ProvenanceRetryInterval = TimeSpan.FromMilliseconds(100);

        var p = await kit.Verifier.VerifyAsync("win11-v3", kit.Bundle, Ct).WaitAsync(TimeSpan.FromSeconds(8), Ct); // well inside VerifyTimeout (10 s)

        Assert.Equal(kit.Bundle.AgentBuildId, p.AgentVersion);
    }

    private sealed class FailingDestroy(IHypervisor inner) : IHypervisor
    {
        public Task SendKeyAsync(VmHandle vm, string key, CancellationToken ct) => throw new NotSupportedException();
        public Task<VmHandle> StartAsync(VmSpec spec, CancellationToken ct) => inner.StartAsync(spec, ct);
        public Task DestroyAsync(VmHandle handle, CancellationToken ct) => throw new IOException("virsh destroy failed");
        public Task<bool> IsRunningAsync(VmHandle vm, CancellationToken ct) => inner.IsRunningAsync(vm, ct);
        public Task<IReadOnlyList<VmHandle>> ListAsync(CancellationToken ct) => inner.ListAsync(ct);
    }

    private sealed class FailingDisposeChannels(IAgentChannelFactory inner) : IAgentChannelFactory
    {
        public async Task<IAgentChannel> ConnectAsync(VmHandle vm, CancellationToken ct) => new FailingDispose(await inner.ConnectAsync(vm, ct));

        private sealed class FailingDispose(IAgentChannel inner) : IAgentChannel
        {
            public int Violations => inner.Violations;
            public IAsyncEnumerable<AgentMessage> ReadAsync(CancellationToken ct) => inner.ReadAsync(ct);
            public Task SendAsync(AgentCommand command, CancellationToken ct) => inner.SendAsync(command, ct);
            public async ValueTask DisposeAsync()
            {
                await inner.DisposeAsync();
                throw new IOException("the socket was already gone");
            }
        }
    }

    private static TemplateVerifier VerifierWith(TemplateJobKit kit, IHypervisor hypervisor, IAgentChannelFactory channels, IEgressRouter egress, TimeProvider? time = null) =>
        new(hypervisor, channels, egress, Microsoft.Extensions.Options.Options.Create(new OrchestratorOptions()), Microsoft.Extensions.Options.Options.Create(kit.Options),
            Microsoft.Extensions.Options.Options.Create(new Maldex.Infrastructure.Net.NetOptions { RunDir = kit.RunDir }), time ?? TimeProvider.System);

    /// <summary>Opens the kit's channels through a <see cref="DefenderScriptChannel"/> and remembers the last one.</summary>
    private sealed class ScriptedChannels(TemplateJobKit kit, Func<TimeSpan, DefenderReading> script) : IAgentChannelFactory
    {
        public DefenderScriptChannel? Last { get; private set; }

        public async Task<IAgentChannel> ConnectAsync(VmHandle vm, CancellationToken ct) =>
            Last = new DefenderScriptChannel(await kit.ChannelFactory.ConnectAsync(vm, ct), kit.Time, script);
    }

    /// <summary>A verification on the fake clock with the default settle window (60 s, a reading every 5 s) and a scripted Defender.</summary>
    private static (TemplateJobKit Kit, ScriptedChannels Channels, Task<VerifiedProvenance> Run) VerifyScripted(Func<TimeSpan, DefenderReading> script,
        Action<TemplateJobOptions>? tune = null)
    {
        var kit = Kit();
        kit.UseFakeClock();
        var defaults = new TemplateJobOptions();
        kit.Options.DefenderSettleWindow = defaults.DefenderSettleWindow;
        kit.Options.DefenderSettleInterval = defaults.DefenderSettleInterval;
        kit.Options.DefenderSettleMinReadings = defaults.DefenderSettleMinReadings;
        tune?.Invoke(kit.Options);
        var channels = new ScriptedChannels(kit, script);
        var egress = new Maldex.Infrastructure.Fakes.NoopEgressRouter(Microsoft.Extensions.Options.Options.Create(new Maldex.Infrastructure.Fakes.FakeEgressOptions()), kit.Guest);
        var run = VerifierWith(kit, kit.Hypervisor, channels, egress, kit.Time).VerifyAsync("win11-v3", kit.Bundle, Ct);
        return (kit, channels, run);
    }

    [Fact]
    public async Task Verify_RealTimeProtectionTurningOnWithinTheSettleWindow_Fails()
    {
        // Right after boot Defender reads off, off, then on: the first reading alone would pass an image whose Defender is on.
        var (kit, _, run) = VerifyScripted(at => at < TimeSpan.FromSeconds(8) ? DefenderReading.Off : DefenderReading.On);
        await using var _ = kit;

        var ex = await Assert.ThrowsAsync<TemplateVerifyException>(() => kit.PumpAsync(run));

        Assert.Equal("Defender real-time protection is on; turn it off in an Edit session", ex.Message);
        Assert.Empty(await kit.Hypervisor.ListAsync(Ct));
    }

    [Fact]
    public async Task Verify_RealTimeProtectionOffThroughoutTheWindow_Passes_AfterReadingEveryFiveSecondsForAMinute()
    {
        var defaults = new TemplateJobOptions();
        Assert.Equal((TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(5), 8), (defaults.DefenderSettleWindow, defaults.DefenderSettleInterval, defaults.DefenderSettleMinReadings));
        var (kit, channels, run) = VerifyScripted(_ => DefenderReading.Off);
        await using var _ = kit;

        var p = await kit.PumpAsync(run);

        Assert.Equal((false, (string?)null), (p.RealTimeProtectionEnabled, p.Note));
        var readings = channels.Last!.Readings;
        Assert.True(readings.Count >= 10, $"only {readings.Count} readings");
        Assert.InRange(readings[^1], TimeSpan.FromSeconds(55), TimeSpan.FromSeconds(61)); // the window is read to its end, and no further
    }

    [Fact]
    public async Task Verify_AFailedReadingMidWindow_IsSkipped_AndTheLaterReadingsStillCount()
    {
        // Off, then a failed reading, then on: the failure neither fails the check nor ends the readings.
        var (kit, _, run) = VerifyScripted(at =>
            at < TimeSpan.FromSeconds(4) ? DefenderReading.Off : at < TimeSpan.FromSeconds(8) ? DefenderReading.Refused : DefenderReading.On);
        await using var _ = kit;

        var ex = await Assert.ThrowsAsync<TemplateVerifyException>(() => kit.PumpAsync(run));

        Assert.Equal("Defender real-time protection is on; turn it off in an Edit session", ex.Message);
    }

    [Fact]
    public async Task Verify_AFailedFirstReading_IsSkipped_AndTheOtherChecksUseTheFirstSuccessfulOne()
    {
        var (kit, _, run) = VerifyScripted(at => at < TimeSpan.FromSeconds(4) ? DefenderReading.Refused : DefenderReading.Off);
        await using var _ = kit;

        var p = await kit.PumpAsync(run);

        Assert.Equal((kit.Bundle.AgentBuildId, (bool?)false, (string?)null), (p.AgentVersion, p.RealTimeProtectionEnabled, p.Note));
    }

    [Fact]
    public async Task Verify_UnreadableAtFirst_ThenOff_IsOff_WithoutTheNote()
    {
        var (kit, _, run) = VerifyScripted(at => at < TimeSpan.FromSeconds(8) ? DefenderReading.Unknown : DefenderReading.Off);
        await using var _ = kit;

        var p = await kit.PumpAsync(run);

        Assert.Equal(((bool?)false, (string?)null), (p.RealTimeProtectionEnabled, p.Note));
    }

    [Fact]
    public async Task Verify_OffThenEveryLaterReadingFails_DidNotSettle_Fails()
    {
        // One "off", then the agent refuses: one reading is not a settled state.
        var (kit, _, run) = VerifyScripted(at => at < TimeSpan.FromSeconds(4) ? DefenderReading.Off : DefenderReading.Refused);
        await using var _ = kit;

        var ex = await Assert.ThrowsAsync<TemplateVerifyException>(() => kit.PumpAsync(run));

        Assert.Equal("Defender state did not settle; not publishing", ex.Message);
        Assert.Empty(await kit.Hypervisor.ListAsync(Ct));
    }

    [Fact]
    public async Task Verify_OffThenAHang_DidNotSettle_Fails_EachReadingBoundedByTheProvenanceTimeout()
    {
        // VerifyTimeout is an hour here (the fake clock): only ProvenanceTimeout bounds a hung reading.
        var (kit, _, run) = VerifyScripted(at => at < TimeSpan.FromSeconds(4) ? DefenderReading.Off : DefenderReading.Silent,
            o => o.ProvenanceTimeout = TimeSpan.FromSeconds(10));
        await using var _ = kit;

        var ex = await Assert.ThrowsAsync<TemplateVerifyException>(() => kit.PumpAsync(run));

        Assert.Equal("Defender state did not settle; not publishing", ex.Message);
    }

    [Fact]
    public async Task Verify_FewerOffReadingsThanTheMinimum_DidNotSettle_Fails()
    {
        // A 60 s window read every 5 s gives at most 13 readings.
        var (kit, _, run) = VerifyScripted(_ => DefenderReading.Off, o => o.DefenderSettleMinReadings = 14);
        await using var _ = kit;

        var ex = await Assert.ThrowsAsync<TemplateVerifyException>(() => kit.PumpAsync(run));

        Assert.Equal("Defender state did not settle; not publishing", ex.Message);
    }

    [Fact]
    public async Task Verify_EveryReadingFails_FailsTheVerification()
    {
        var (kit, _, run) = VerifyScripted(_ => DefenderReading.Refused);
        await using var _ = kit;

        var ex = await Assert.ThrowsAsync<TemplateVerifyException>(() => kit.PumpAsync(run));

        Assert.StartsWith("provenance check failed", ex.Message);
        Assert.Empty(await kit.Hypervisor.ListAsync(Ct));
    }

    [Fact]
    public async Task Verify_AFailingChannelClose_StillDestroysTheVm_AndReleasesTheLease_AndKeepsTheResult()
    {
        await using var kit = Kit();
        var egress = new Maldex.Infrastructure.Fakes.NoopEgressRouter(Microsoft.Extensions.Options.Options.Create(new Maldex.Infrastructure.Fakes.FakeEgressOptions()), kit.Guest);

        var p = await VerifierWith(kit, kit.Hypervisor, new FailingDisposeChannels(kit.ChannelFactory), egress).VerifyAsync("win11-v3", kit.Bundle, Ct);

        Assert.Equal(kit.Bundle.AgentBuildId, p.AgentVersion);
        Assert.Empty(await kit.Hypervisor.ListAsync(Ct));
        Assert.Empty(await egress.ListAsync(Ct));
    }

    [Fact]
    public async Task Verify_AFailingDestroy_StillReleasesTheLease()
    {
        await using var kit = Kit();
        var egress = new Maldex.Infrastructure.Fakes.NoopEgressRouter(Microsoft.Extensions.Options.Options.Create(new Maldex.Infrastructure.Fakes.FakeEgressOptions()), kit.Guest);

        await VerifierWith(kit, new FailingDestroy(kit.Hypervisor), kit.ChannelFactory, egress).VerifyAsync("win11-v3", kit.Bundle, Ct);

        Assert.Empty(await egress.ListAsync(Ct));
    }

    [Fact]
    public async Task Verify_ConnectFails_BecomesAVerifyException_AndCleansUp()
    {
        await using var kit = Kit();
        kit.ConnectFails = true;

        var ex = await Assert.ThrowsAsync<TemplateVerifyException>(() => kit.Verifier.VerifyAsync("win11-v3", kit.Bundle, Ct));

        Assert.Contains("agent socket", ex.Message);
        Assert.Empty(await kit.Hypervisor.ListAsync(Ct));
    }
}
