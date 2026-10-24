using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Maldex.Agent;
using Maldex.Core.Abstractions;
using Maldex.Core.Agent;
using Maldex.Core.Domain;
using Maldex.Infrastructure.Fakes;
using Maldex.Orchestrator;
using Maldex.Orchestrator.Templates;
using Microsoft.Extensions.Options;
using Xunit;

namespace Maldex.Integration.Tests.Orchestrator;

/// <summary>A fake hypervisor/guest/store trio plus a bundle, as the template jobs see them. No database.</summary>
internal sealed class TemplateJobKit : IAsyncDisposable
{
    public FakeGuest Guest { get; } = new();
    public FakeHypervisor Hypervisor { get; }
    public SpyTemplateStore Store { get; }
    public TemplateJobOptions Options { get; } = new()
    {
        AgentReadyTimeout = TimeSpan.FromSeconds(10), // generous: the timeout tests shorten it; a loaded machine must not trip the passing ones
        PowerOffTimeout = TimeSpan.FromSeconds(10),
        VerifyTimeout = TimeSpan.FromSeconds(10),
        ApplyComponentsTimeout = TimeSpan.FromSeconds(10),
        ChunkTimeout = TimeSpan.FromSeconds(10),
        PollInterval = TimeSpan.FromMilliseconds(10),
    };
    public ComponentBundle Bundle { get; } = MakeBundle();
    public List<ReplayAgentChannel> Channels { get; } = [];

    public TemplateJobKit(bool hypervisorSeesPowerOff = true)
    {
        Hypervisor = new FakeHypervisor(Microsoft.Extensions.Options.Options.Create(new FakeHypervisorOptions()), hypervisorSeesPowerOff ? Guest : null);
        Store = new SpyTemplateStore(Hypervisor);
    }

    public TemplateSealer Sealer => new(Hypervisor, Store, Microsoft.Extensions.Options.Options.Create(Options), TimeProvider.System);

    public TemplateVerifier Verifier => new(Hypervisor, new Factory(this), new NoopEgressRouter(Microsoft.Extensions.Options.Options.Create(new FakeEgressOptions()), Guest),
        Microsoft.Extensions.Options.Options.Create(new OrchestratorOptions()), Microsoft.Extensions.Options.Options.Create(Options), TimeProvider.System);

    /// <summary>A channel to a guest that is ready at once, or never (<paramref name="ready"/> false).</summary>
    public ReplayAgentChannel NewChannel(Guid taskId, bool ready = true)
    {
        string[] lines = ready ? ["""{"delayMs":0,"message":{"type":"ready","agentVersion":"0.1.0-fake+000000000000","hostname":"h"}}"""] : [];
        var ch = new ReplayAgentChannel(lines, 1, holdOpen: true, TimeProvider.System, Guest, taskId);
        lock (Channels) Channels.Add(ch);
        return ch;
    }

    public async Task<(ReplayAgentChannel Channel, VmHandle Vm)> StartTemplateVmAsync(bool ready = true)
    {
        var id = Guid.NewGuid();
        var vm = await Hypervisor.StartAsync(new VmSpec(id, "win11-edit", 4096, 2, "fake-net", Writable: true), CancellationToken.None);
        return (NewChannel(id, ready), vm);
    }

    public static ComponentBundle MakeBundle()
    {
        var agent = new byte[1_258_291]; // three chunks
        new Random(7).NextBytes(agent);
        var sysmon = "<Sysmon schemaversion=\"4.90\"/>"u8.ToArray();
        var der = new byte[] { 1, 2, 3, 4, 5 };
        static string Hex(byte[] d) => Convert.ToHexString(SHA256.HashData(d)).ToLowerInvariant();
        return new ComponentBundle
        {
            AgentExe = agent,
            AgentBuildId = AgentHost.Version + "+" + Hex(agent)[..12],
            SysmonConfig = sysmon,
            SysmonConfigSha256 = Hex(sysmon),
            CaDer = der,
            MitmCaSha256 = Hex(der),
        };
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var c in Channels) await c.DisposeAsync();
    }

    private sealed class Factory(TemplateJobKit kit) : IAgentChannelFactory
    {
        public Task<IAgentChannel> ConnectAsync(VmHandle vm, CancellationToken ct) =>
            kit.ConnectFails ? throw new IOException("the agent socket is not there") : Task.FromResult<IAgentChannel>(kit.NewChannel(vm.TaskId, kit.VerifyAgentReady));
    }

    public bool VerifyAgentReady { get; set; } = true;
    public bool ConnectFails { get; set; }
}

/// <summary>Records the seal calls and whether the guest was already gone when each happened.</summary>
internal sealed class SpyTemplateStore(IHypervisor hypervisor) : ITemplateStore
{
    public List<(string Stem, int VmsListed)> Seals { get; } = [];
    public bool Exists(string fileStem) => true;
    public long? SizeOf(string fileStem) => null;
    public async Task SealAsync(string fileStem, CancellationToken ct) =>
        Seals.Add((fileStem, (await hypervisor.ListAsync(ct)).Count));
    public Task DeleteAsync(string fileStem, CancellationToken ct) => Task.CompletedTask;
}

public class TemplateSealerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Seal_DeliversThenAppliesThenPowersOffThenSeals_InThatOrder()
    {
        await using var kit = new TemplateJobKit();
        var (ch, vm) = await kit.StartTemplateVmAsync();

        await kit.Sealer.SealAsync(ch, vm, "win11-edit", kit.Bundle, Ct);

        var types = ch.SentCommands.Select(c => c.GetType()).ToList();
        // 3 agent chunks + 1 sysmon + 1 CA, then apply, then power off.
        Assert.Equal([.. Enumerable.Repeat(typeof(PutFileChunkCommand), 5), typeof(ApplyComponentsCommand), typeof(PowerOffCommand)], types);
        // Sealed once, after the guest was gone.
        Assert.Equal([("win11-edit", 0)], kit.Store.Seals);
        Assert.Empty(await kit.Hypervisor.ListAsync(Ct));
    }

    [Fact]
    public async Task Seal_SendsComponentFilesWithHashesAndPrefixedIds()
    {
        await using var kit = new TemplateJobKit();
        var (ch, vm) = await kit.StartTemplateVmAsync();
        var b = kit.Bundle;

        await kit.Sealer.SealAsync(ch, vm, "win11-edit", b, Ct);

        var chunks = ch.SentCommands.OfType<PutFileChunkCommand>().ToList();
        var agent = chunks.Where(c => c.TransferId == $"components/agent/{b.AgentSha256}").ToList();
        Assert.Equal(3, agent.Count);
        Assert.Equal(b.AgentExe, agent.SelectMany(c => Convert.FromBase64String(c.DataBase64)).ToArray());
        Assert.All(agent, c => Assert.Equal((b.AgentExe.Length, b.AgentSha256), (c.TotalBytes, c.Sha256)));
        var sysmon = Assert.Single(chunks, c => c.TransferId == $"components/sysmon/{b.SysmonConfigSha256}");
        Assert.Equal(b.SysmonConfig, Convert.FromBase64String(sysmon.DataBase64));
        var ca = Assert.Single(chunks, c => c.TransferId == $"components/ca/{b.MitmCaSha256}");
        Assert.Equal(b.CaDer, Convert.FromBase64String(ca.DataBase64));

        var apply = Assert.Single(ch.SentCommands.OfType<ApplyComponentsCommand>());
        Assert.Equal($"components/agent/{b.AgentSha256}", apply.AgentTransferId);
        Assert.Equal($"components/sysmon/{b.SysmonConfigSha256}", apply.SysmonConfigTransferId);
        Assert.Equal($"components/ca/{b.MitmCaSha256}", apply.CaCertTransferId);
        // The fake guest refuses ids without the prefix, so an accepted apply proves the prefix.
        Assert.Equal([apply.AgentTransferId, apply.SysmonConfigTransferId, apply.CaCertTransferId], kit.Guest.AppliedComponentIds);
    }

    [Fact]
    public async Task Seal_AgentNeverReady_FailsWithoutSealing()
    {
        await using var kit = new TemplateJobKit();
        kit.Options.AgentReadyTimeout = TimeSpan.FromMilliseconds(300);
        var (ch, vm) = await kit.StartTemplateVmAsync(ready: false);

        var ex = await Assert.ThrowsAsync<TemplateSealException>(() => kit.Sealer.SealAsync(ch, vm, "win11-edit", kit.Bundle, Ct));

        Assert.Contains("agent", ex.Message);
        Assert.Empty(kit.Store.Seals);
        Assert.Empty(ch.SentCommands);
    }

    [Fact]
    public async Task Seal_NoPowerOff_FailsAndDoesNotSeal()
    {
        await using var kit = new TemplateJobKit(hypervisorSeesPowerOff: false); // the VM stays running after power_off
        kit.Options.PowerOffTimeout = TimeSpan.FromMilliseconds(300);
        var (ch, vm) = await kit.StartTemplateVmAsync();

        var ex = await Assert.ThrowsAsync<TemplateSealException>(() => kit.Sealer.SealAsync(ch, vm, "win11-edit", kit.Bundle, Ct));

        Assert.Equal("the guest did not power off; not sealing a possibly dirty disk", ex.Message);
        Assert.Empty(kit.Store.Seals);
        Assert.Single(ch.SentCommands.OfType<PowerOffCommand>());
        Assert.True(await kit.Hypervisor.IsRunningAsync(vm, Ct)); // still running...
        Assert.Contains(vm, await kit.Hypervisor.ListAsync(Ct)); // ...and never destroyed
    }

    [Fact]
    public async Task Seal_VmStillListedAfterPowerOff_StillSeals()
    {
        // Like libvirt: the run directory keeps the VM in ListAsync after it shut off, until DestroyAsync.
        await using var kit = new TemplateJobKit();
        var (ch, vm) = await kit.StartTemplateVmAsync();
        var listedWhenOff = false;
        kit.Guest.PoweredOff += _ => listedWhenOff = !kit.Hypervisor.IsRunningAsync(vm, Ct).Result && kit.Hypervisor.ListAsync(Ct).Result.Contains(vm);

        await kit.Sealer.SealAsync(ch, vm, "win11-edit", kit.Bundle, Ct);

        Assert.True(listedWhenOff);
        Assert.Equal([("win11-edit", 0)], kit.Store.Seals); // destroyed (cleaned up) before the seal
    }

    [Fact]
    public async Task Seal_DisconnectInsteadOfPowerOffAck_CountsAsAcknowledged()
    {
        await using var kit = new TemplateJobKit();
        var (inner, vm) = await kit.StartTemplateVmAsync();
        var ch = new DropsPowerOffAck(inner, swallow: false);

        await kit.Sealer.SealAsync(ch, vm, "win11-edit", kit.Bundle, Ct);

        Assert.Single(kit.Store.Seals);
    }

    [Fact]
    public async Task Seal_DisconnectButStillRunning_FailsWithoutSealing()
    {
        await using var kit = new TemplateJobKit(hypervisorSeesPowerOff: false);
        kit.Options.PowerOffTimeout = TimeSpan.FromMilliseconds(300);
        var (inner, vm) = await kit.StartTemplateVmAsync();
        var ch = new DropsPowerOffAck(inner, swallow: false);

        var ex = await Assert.ThrowsAsync<TemplateSealException>(() => kit.Sealer.SealAsync(ch, vm, "win11-edit", kit.Bundle, Ct));

        Assert.Contains("did not power off", ex.Message);
        Assert.Empty(kit.Store.Seals);
    }

    [Fact]
    public async Task Seal_PowerOffNeverAnswered_OneDeadlineCoversAckAndWait()
    {
        await using var kit = new TemplateJobKit(hypervisorSeesPowerOff: false);
        kit.Options.PowerOffTimeout = TimeSpan.FromMilliseconds(500);
        var (inner, vm) = await kit.StartTemplateVmAsync();
        var ch = new DropsPowerOffAck(inner, swallow: true); // channel stays open, nobody answers
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<TemplateSealException>(() => kit.Sealer.SealAsync(ch, vm, "win11-edit", kit.Bundle, Ct));

        Assert.Contains("did not power off", ex.Message);
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(900), $"took {sw.Elapsed}: the ack wait and the running wait each used a full timeout");
        Assert.Empty(kit.Store.Seals);
    }

    [Fact]
    public async Task DeliverComponent_NonComponentId_UsesTheSharedRefusalText()
    {
        var inbox = new TransferInbox();
        var channel = new ScriptedChannel(inbox, c => [new CommandResultMessage(c.CommandId, true)]);

        var ex = await Assert.ThrowsAsync<TemplateSealException>(() => new SampleDelivery(TimeProvider.System)
            .DeliverComponentAsync(channel, inbox, "samples/ab/cd", [1], "00", m => new TemplateSealException(m), Ct));

        Assert.Equal(AgentLimits.NotComponentTransferMessage("samples/ab/cd"), ex.Message);
    }

    [Fact]
    public async Task Seal_ApplyRefused_FailsNamingTheStep_WithoutPowerOffOrSeal()
    {
        await using var kit = new TemplateJobKit();
        kit.Guest.RejectApplyComponents = true;
        var (ch, vm) = await kit.StartTemplateVmAsync();

        var ex = await Assert.ThrowsAsync<TemplateSealException>(() => kit.Sealer.SealAsync(ch, vm, "win11-edit", kit.Bundle, Ct));

        Assert.Contains("import_ca failed", ex.Message);
        Assert.Empty(ch.SentCommands.OfType<PowerOffCommand>());
        Assert.Empty(kit.Store.Seals);
    }

    [Fact]
    public async Task Seal_ChunkRefused_FailsWithoutApplyOrSeal()
    {
        await using var kit = new TemplateJobKit();
        kit.Guest.RejectSampleChunkIndex = 1;
        var (ch, vm) = await kit.StartTemplateVmAsync();

        var ex = await Assert.ThrowsAsync<TemplateSealException>(() => kit.Sealer.SealAsync(ch, vm, "win11-edit", kit.Bundle, Ct));

        Assert.Contains("refused", ex.Message);
        Assert.Empty(ch.SentCommands.OfType<ApplyComponentsCommand>());
        Assert.Empty(kit.Store.Seals);
    }

    [Fact]
    public async Task ReplayGuest_RefusesApplyIdsWithoutTheComponentsPrefix_LikeTheRealAgent()
    {
        await using var kit = new TemplateJobKit();
        var (ch, _) = await kit.StartTemplateVmAsync();
        var reader = ch.ReadAsync(Ct).GetAsyncEnumerator(Ct);
        Assert.True(await reader.MoveNextAsync());
        Assert.IsType<ReadyMessage>(reader.Current);

        await ch.SendAsync(new ApplyComponentsCommand("c1", "components/agent/a", "samples/ab/cd", "components/ca/c"), Ct);

        Assert.True(await reader.MoveNextAsync());
        var result = Assert.IsType<CommandResultMessage>(reader.Current);
        Assert.False(result.Success);
        Assert.Equal(AgentLimits.NotComponentTransferMessage("samples/ab/cd"), result.Error);
        Assert.Empty(kit.Guest.AppliedComponentIds);
        await reader.DisposeAsync();
    }
}

/// <summary>Wraps a replay channel: the power_off answer never reaches the host (the guest vanishes, or never answers).</summary>
internal sealed class DropsPowerOffAck(ReplayAgentChannel inner, bool swallow) : IAgentChannel
{
    private string? _powerOffId;
    public int Violations => inner.Violations;
    public IReadOnlyList<AgentCommand> SentCommands => inner.SentCommands;

    public Task SendAsync(AgentCommand command, CancellationToken ct)
    {
        if (command is PowerOffCommand p)
        {
            _powerOffId = p.CommandId;
            if (swallow) return Task.CompletedTask;
        }
        return inner.SendAsync(command, ct);
    }

    public async IAsyncEnumerable<AgentMessage> ReadAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var m in inner.ReadAsync(ct))
            if (m is not CommandResultMessage r || r.CommandId != _powerOffId) yield return m;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public class ComponentBundleTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _dir = Directory.CreateTempSubdirectory("maldex-bundle-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private TemplateJobOptions Options(bool agent = true, bool sysmon = true, bool ca = true, string? version = "7.8.9\n")
    {
        var o = new TemplateJobOptions
        {
            AgentExePath = Path.Combine(_dir, "Maldex.Agent.exe"),
            AgentVersionPath = Path.Combine(_dir, "Maldex.Agent.version"),
            SysmonConfigPath = Path.Combine(_dir, "sysmon-config.xml"),
            MitmCaPemPath = Path.Combine(_dir, "ca.pem"),
        };
        if (agent) File.WriteAllBytes(o.AgentExePath, [.. "MZ fake agent bytes"u8]);
        if (version is not null) File.WriteAllText(o.AgentVersionPath, version);
        if (sysmon) File.WriteAllText(o.SysmonConfigPath, "<Sysmon/>");
        if (ca) File.WriteAllText(o.MitmCaPemPath, NewCert().ExportCertificatePem());
        return o;
    }

    private static X509Certificate2 NewCert()
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=mitmproxy test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    [Fact]
    public async Task Load_HashesFiles_AndConvertsTheCaFromPemToDer()
    {
        var o = Options();
        var pem = await File.ReadAllTextAsync(o.MitmCaPemPath, Ct);

        var b = await ComponentBundle.LoadAsync(o, Ct);

        var der = X509Certificate2.CreateFromPem(pem).RawData;
        Assert.Equal(der, b.CaDer);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(der)).ToLowerInvariant(), b.MitmCaSha256);
        Assert.Equal(Convert.ToHexString(SHA256.HashData("<Sysmon/>"u8)).ToLowerInvariant(), b.SysmonConfigSha256);
        Assert.Equal("<Sysmon/>"u8.ToArray(), b.SysmonConfig);
        Assert.Equal(await File.ReadAllBytesAsync(o.AgentExePath, Ct), b.AgentExe);
    }

    [Fact]
    public async Task Load_AgentBuildId_MatchesWhatTheAgentReportsForTheSameBytes()
    {
        var o = Options();
        File.WriteAllText(o.AgentVersionPath, AgentHost.Version); // what ./build.sh agent records for the agent it compiled

        var b = await ComponentBundle.LoadAsync(o, Ct);

        Assert.Equal(AgentHost.ComputeBuildId(AgentHost.Version, o.AgentExePath), b.AgentBuildId);
        Assert.Matches(@"^.+\+[0-9a-f]{12}$", b.AgentBuildId);
    }

    [Fact]
    public async Task Load_AgentVersion_ComesFromTheBuildRecord_NotFromThisServer()
    {
        var b = await ComponentBundle.LoadAsync(Options(version: "7.8.9+meta\n"), Ct);

        Assert.StartsWith("7.8.9+", b.AgentBuildId);
    }

    [Fact]
    public async Task Load_MissingVersionRecord_NamesTheFix()
    {
        var o = Options(version: null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ComponentBundle.LoadAsync(o, Ct));

        Assert.Contains(o.AgentVersionPath, ex.Message);
        Assert.Contains("./build.sh agent", ex.Message);
    }

    [Fact]
    public async Task Load_MissingAgent_NamesTheFix()
    {
        var o = Options(agent: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ComponentBundle.LoadAsync(o, Ct));

        Assert.Contains(o.AgentExePath, ex.Message);
        Assert.Contains("./build.sh agent", ex.Message);
    }

    [Fact]
    public async Task Load_MissingCa_NamesTheFix()
    {
        var o = Options(ca: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ComponentBundle.LoadAsync(o, Ct));

        Assert.Contains(o.MitmCaPemPath, ex.Message);
        Assert.Contains("./build.sh real-setup", ex.Message);
    }

    [Fact]
    public async Task Load_MissingSysmonConfig_NamesTheFile()
    {
        var o = Options(sysmon: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ComponentBundle.LoadAsync(o, Ct));

        Assert.Contains(o.SysmonConfigPath, ex.Message);
    }

    [Fact]
    public async Task Load_GarbageCa_FailsClearly()
    {
        var o = Options();
        await File.WriteAllTextAsync(o.MitmCaPemPath, "not a certificate", Ct);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ComponentBundle.LoadAsync(o, Ct));

        Assert.Contains("not a valid PEM certificate", ex.Message);
    }
}
