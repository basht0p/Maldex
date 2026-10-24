using System.Security.Cryptography;
using Maldex.Agent.Commands;
using Maldex.Agent.Windows;
using Maldex.Core.Agent;
using Xunit;

namespace Maldex.Agent.Tests;

public class ComponentCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mxcomp-" + Guid.NewGuid().ToString("N"));
    private string Staging => Path.Combine(_root, "staging");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    /// <summary>Records each step with the content of the file it was given at call time (the dispatcher may clean up afterwards).</summary>
    private sealed class FakeInstaller : IComponentInstaller
    {
        public List<(string Step, string Path, byte[] Content)> Calls { get; } = [];
        public string? FailAt { get; set; }

        private void Step(string name, string? path)
        {
            Calls.Add((name, path ?? "", path is null ? [] : File.ReadAllBytes(path)));
            if (FailAt == name) throw new InvalidOperationException(name + " exploded");
        }

        public void ImportCaCertificate(string derPath) => Step("import_ca", derPath);
        public void ApplySysmonConfig(string configPath) => Step("sysmon_config", configPath);
        public void SwapAgent(string newExePath) => Step("swap_agent", newExePath);
        public void ClearLogsAndTemp() => Step("clear_logs", null);
    }

    private sealed class FakePower : IPowerController
    {
        public int Calls;
        public bool Throw;
        public void PowerOff() { Calls++; if (Throw) throw new InvalidOperationException("no privilege"); }
    }

    private CommandDispatcher Make(FakeInstaller? installer = null, FakePower? power = null) =>
        new(new NoopSampleOpener(), new RecordingUrlOpener(), () => { }, Path.Combine(_root, "Desktop"), Staging, components: installer, power: power);

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static IEnumerable<PutFileChunkCommand> Chunks(string id, byte[] data)
    {
        var count = Math.Max(1, (data.Length + AgentLimits.ChunkBytes - 1) / AgentLimits.ChunkBytes);
        var sha = Sha(data);
        for (var i = 0; i < count; i++)
        {
            var part = data.AsSpan(i * AgentLimits.ChunkBytes, Math.Min(AgentLimits.ChunkBytes, data.Length - i * AgentLimits.ChunkBytes));
            yield return new PutFileChunkCommand("c" + i, id, i, count, data.Length, sha, Convert.ToBase64String(part));
        }
    }

    private static async Task DeliverAsync(CommandDispatcher d, string id, byte[] data)
    {
        foreach (var c in Chunks(id, data))
        {
            var r = await d.DispatchAsync(c, CancellationToken.None);
            Assert.True(r.Success, r.Error);
        }
    }

    private static readonly byte[] Agent = [1, 2, 3, 4];
    private static readonly byte[] Sysmon = "<Sysmon/>"u8.ToArray();
    private static readonly byte[] Ca = [9, 8, 7];

    private static async Task DeliverAllAsync(CommandDispatcher d)
    {
        await DeliverAsync(d, "components/agent/aaa", Agent);
        await DeliverAsync(d, "components/sysmon/bbb", Sysmon);
        await DeliverAsync(d, "components/ca/ccc", Ca);
    }

    private static ApplyComponentsCommand Apply(string agent = "components/agent/aaa", string sysmon = "components/sysmon/bbb", string ca = "components/ca/ccc") =>
        new("a1", agent, sysmon, ca);

    [Fact]
    public async Task Apply_RunsStepsInOrder_WithThePublishedFiles()
    {
        var installer = new FakeInstaller();
        using var d = Make(installer);
        await DeliverAllAsync(d);

        var res = await d.DispatchAsync(Apply(), CancellationToken.None);

        Assert.Equal(new CommandResultMessage("a1", true), res);
        Assert.Equal(["import_ca", "sysmon_config", "swap_agent", "clear_logs"], installer.Calls.Select(c => c.Step));
        Assert.Equal(Ca, installer.Calls[0].Content);
        Assert.Equal(Sysmon, installer.Calls[1].Content);
        Assert.Equal(Agent, installer.Calls[2].Content);
    }

    [Theory]
    [InlineData("import_ca", 1)]
    [InlineData("sysmon_config", 2)]
    [InlineData("swap_agent", 3)]
    [InlineData("clear_logs", 4)]
    public async Task Apply_FailingStep_StopsTheRest_AndNamesTheStep(string failAt, int expectedCalls)
    {
        var installer = new FakeInstaller { FailAt = failAt };
        using var d = Make(installer);
        await DeliverAllAsync(d);

        var res = await d.DispatchAsync(Apply(), CancellationToken.None);

        Assert.False(res.Success);
        Assert.Contains(failAt, res.Error);
        Assert.Contains("exploded", res.Error);
        Assert.Equal(expectedCalls, installer.Calls.Count);
    }

    [Theory]
    [InlineData("samples/agent", "components/sysmon/bbb", "components/ca/ccc", "samples/agent")]
    [InlineData("components/agent/aaa", "blob-1", "components/ca/ccc", "blob-1")]
    [InlineData("components/agent/aaa", "components/sysmon/bbb", "../components/ca", "../components/ca")]
    [InlineData("components/agent/aaa", "components/sysmon/bbb", "", "")]
    public async Task Apply_RefusesNonComponentIds_NamingTheBadId_AndRunsNothing(string agent, string sysmon, string ca, string bad)
    {
        var installer = new FakeInstaller();
        using var d = Make(installer);
        await DeliverAllAsync(d);

        var res = await d.DispatchAsync(Apply(agent, sysmon, ca), CancellationToken.None);

        Assert.False(res.Success);
        Assert.Contains($"'{bad}'", res.Error);
        Assert.Empty(installer.Calls);
    }

    [Fact]
    public async Task Apply_RefusesADeliveredSample_AsAComponent()
    {
        var installer = new FakeInstaller();
        using var d = Make(installer);
        await DeliverAsync(d, "components/sysmon/bbb", Sysmon);
        await DeliverAsync(d, "components/ca/ccc", Ca);
        await DeliverAsync(d, "samples/evil", [0x4D, 0x5A]); // fully delivered and verified, but not a component

        var res = await d.DispatchAsync(Apply(agent: "samples/evil"), CancellationToken.None);

        Assert.False(res.Success);
        Assert.Contains("'samples/evil'", res.Error);
        Assert.Empty(installer.Calls);
    }

    [Fact]
    public async Task Apply_UnknownTransfer_Fails_AndRunsNothing()
    {
        var installer = new FakeInstaller();
        using var d = Make(installer);
        await DeliverAsync(d, "components/agent/aaa", Agent);

        var res = await d.DispatchAsync(Apply(), CancellationToken.None);

        Assert.False(res.Success);
        Assert.Empty(installer.Calls);
    }

    [Fact]
    public async Task Apply_WithoutInstaller_Fails()
    {
        using var d = Make();
        var res = await d.DispatchAsync(Apply(), CancellationToken.None);
        Assert.False(res.Success);
        Assert.Contains("not available", res.Error);
    }

    [Fact]
    public async Task PowerOff_AcksFirst_ThenPowersOffAfterTheResult()
    {
        var power = new FakePower();
        using var d = Make(power: power);
        var cmd = new PowerOffCommand("p1");

        var res = await d.DispatchAsync(cmd, CancellationToken.None);
        Assert.Equal(new CommandResultMessage("p1", true), res);
        Assert.Equal(0, power.Calls); // nothing happens before the result frame is written

        d.AfterResultSent(cmd);
        Assert.Equal(1, power.Calls);
    }

    [Fact]
    public async Task PowerOff_OtherCommandsNeverPowerOff()
    {
        var power = new FakePower();
        using var d = Make(power: power);
        d.AfterResultSent(new ShutdownCommand("s1"));
        d.AfterResultSent(Apply());
        Assert.Equal(0, power.Calls);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task PowerOff_WithoutController_Fails()
    {
        using var d = Make();
        var res = await d.DispatchAsync(new PowerOffCommand("p2"), CancellationToken.None);
        Assert.False(res.Success);
    }

    [Fact]
    public void PowerOff_ControllerThrowing_DoesNotEscapeTheHostLoop()
    {
        using var d = Make(power: new FakePower { Throw = true });
        d.AfterResultSent(new PowerOffCommand("p3"));
    }

    [Fact]
    public async Task Components_AboveSampleCap_AreAccepted_UpToTheComponentCap()
    {
        using var d = Make();
        var big = new byte[(int)AgentLimits.MaxInboundBytes + 1_000_000]; // 33 MB, 66 chunks
        await DeliverAsync(d, "components/agent/big", big);

        var atCap = new byte[AgentLimits.MaxComponentBytes]; // exactly 160 MiB, 320 chunks
        await DeliverAsync(d, "components/agent/max", atCap);
    }

    [Fact]
    public async Task Components_AboveTheComponentCap_AreRefused()
    {
        using var d = Make();
        var first = new PutFileChunkCommand("c0", "components/agent/huge", 0, 2, AgentLimits.MaxComponentBytes + 1, new string('0', 64), Convert.ToBase64String(new byte[10]));
        var res = await d.DispatchAsync(first, CancellationToken.None);
        Assert.False(res.Success);
        Assert.Contains("exceeds", res.Error);

        var tooMany = new PutFileChunkCommand("c0", "components/agent/many", 0, AgentLimits.MaxComponentChunks + 1, 100, new string('0', 64), Convert.ToBase64String(new byte[10]));
        Assert.False((await d.DispatchAsync(tooMany, CancellationToken.None)).Success);
    }

    [Theory]
    [InlineData("samples/big")]
    [InlineData("blob-1")]
    [InlineData("Components/agent/x")]   // prefix is case sensitive
    [InlineData("xcomponents/agent/x")]
    public async Task NonComponentTransfers_KeepTheSampleCap(string id)
    {
        using var d = Make();
        var overCap = new PutFileChunkCommand("c0", id, 0, 2, AgentLimits.MaxInboundBytes + 1, new string('0', 64), Convert.ToBase64String(new byte[10]));
        var res = await d.DispatchAsync(overCap, CancellationToken.None);
        Assert.False(res.Success);
        Assert.Contains("exceeds", res.Error);

        var tooManyChunks = new PutFileChunkCommand("c0", id, 0, AgentLimits.MaxChunks + 1, 100, new string('0', 64), Convert.ToBase64String(new byte[10]));
        Assert.False((await d.DispatchAsync(tooManyChunks, CancellationToken.None)).Success);
    }

    [Fact]
    public void ComponentLimits_AreConsistent()
    {
        Assert.Equal(160L * 1024 * 1024, AgentLimits.MaxComponentBytes);
        Assert.Equal("components/", AgentLimits.ComponentTransferPrefix);
        Assert.Equal(AgentLimits.MaxComponentBytes, (long)AgentLimits.MaxComponentChunks * AgentLimits.ChunkBytes);
        Assert.True(AgentLimits.IsComponentTransfer("components/ca/x"));
        Assert.False(AgentLimits.IsComponentTransfer("samples/x"));
        Assert.False(AgentLimits.IsComponentTransfer(null));
    }

    [Fact]
    public void Swap_RenamesRunningExeToOld_AndMovesNewIntoPlace()
    {
        Directory.CreateDirectory(_root);
        var exe = Path.Combine(_root, "Maldex.Agent.exe");
        var fresh = Path.Combine(_root, "downloaded.bin");
        File.WriteAllText(exe, "old build");
        File.WriteAllText(fresh, "new build");

        AgentExeSwap.Swap(exe, fresh);

        Assert.Equal("new build", File.ReadAllText(exe));
        Assert.Equal("old build", File.ReadAllText(exe + ".old"));
        Assert.False(File.Exists(exe + ".new"));
        Assert.False(File.Exists(fresh));
    }

    [Fact]
    public void Swap_ReplacesAStaleOld_AndRestoresTheExeWhenItFails()
    {
        Directory.CreateDirectory(_root);
        var exe = Path.Combine(_root, "Maldex.Agent.exe");
        File.WriteAllText(exe, "current");
        File.WriteAllText(exe + ".old", "ancient");
        var fresh = Path.Combine(_root, "n.bin");
        File.WriteAllText(fresh, "next");
        AgentExeSwap.Swap(exe, fresh);
        Assert.Equal("current", File.ReadAllText(exe + ".old"));

        Assert.ThrowsAny<IOException>(() => AgentExeSwap.Swap(exe, Path.Combine(_root, "missing.bin")));
        Assert.Equal("next", File.ReadAllText(exe)); // untouched by the failed swap
    }

    [Fact]
    public void DeleteStale_RemovesOldAndNew_AndToleratesNothingToDelete()
    {
        Directory.CreateDirectory(_root);
        var exe = Path.Combine(_root, "Maldex.Agent.exe");
        File.WriteAllText(exe, "x");
        File.WriteAllText(exe + ".old", "x");
        File.WriteAllText(exe + ".new", "x");
        AgentExeSwap.DeleteStale(exe);
        AgentExeSwap.DeleteStale(exe);
        Assert.True(File.Exists(exe));
        Assert.False(File.Exists(exe + ".old"));
        Assert.False(File.Exists(exe + ".new"));
    }
}
