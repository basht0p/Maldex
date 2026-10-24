using Maldex.Core.Abstractions;
using Maldex.Core.Agent;
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

        var ex = await Assert.ThrowsAsync<TemplateVerifyException>(() => kit.Verifier.VerifyAsync("win11-v3", kit.Bundle, Ct));

        Assert.Contains("did not become ready", ex.Message);
        Assert.Empty(await kit.Hypervisor.ListAsync(Ct));
    }

    [Fact]
    public async Task Verify_NoProvenanceAnswer_TimesOut()
    {
        await using var kit = Kit();
        kit.Guest.SilentProvenance = true;

        var ex = await Assert.ThrowsAsync<TemplateVerifyException>(() => kit.Verifier.VerifyAsync("win11-v3", kit.Bundle, Ct));

        Assert.Contains("provenance", ex.Message);
        Assert.Empty(await kit.Hypervisor.ListAsync(Ct));
    }
}
