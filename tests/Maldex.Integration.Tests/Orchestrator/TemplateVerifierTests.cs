using Maldex.Core.Abstractions;
using Maldex.Core.Agent;
using Maldex.Orchestrator;
using Maldex.Orchestrator.Templates;
using Xunit;

namespace Maldex.Integration.Tests.Orchestrator;

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

    private static TemplateVerifier VerifierWith(TemplateJobKit kit, IHypervisor hypervisor, IAgentChannelFactory channels, IEgressRouter egress) =>
        new(hypervisor, channels, egress, Microsoft.Extensions.Options.Options.Create(new OrchestratorOptions()), Microsoft.Extensions.Options.Options.Create(kit.Options),
            Microsoft.Extensions.Options.Options.Create(new Maldex.Infrastructure.Net.NetOptions { RunDir = kit.RunDir }), TimeProvider.System);

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
