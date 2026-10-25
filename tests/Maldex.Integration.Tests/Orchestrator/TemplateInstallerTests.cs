using System.Text;
using System.Threading.Channels;
using Maldex.Core.Abstractions;
using Maldex.Core.Agent;
using Maldex.Core.Domain;
using Maldex.Infrastructure.Fakes;
using Maldex.Orchestrator.Templates;
using Maldex.Orchestrator.Templates.Build;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Maldex.Integration.Tests.Orchestrator;

/// <summary>The Installing and Provisioning phases of a Build against the fake hypervisor and guest, on a fake clock.</summary>
public class TemplateInstallerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Rig : IAsyncDisposable
    {
        public TemplateJobKit Kit { get; }
        public FakeTimeProvider Time { get; }
        public BuildOptions Build { get; }
        public VmHandle Vm { get; private set; } = null!;
        public Guid JobId { get; } = Guid.NewGuid();
        public List<(TemplateJobState State, string Note)> Phases { get; } = [];
        private readonly string _logRoot = Directory.CreateTempSubdirectory("maldex-installer-").FullName;

        public Rig(bool hypervisorSeesPowerOff = true)
        {
            Kit = new TemplateJobKit(hypervisorSeesPowerOff);
            Time = Kit.UseFakeClock();
            Build = new BuildOptions { JobLogDir = _logRoot };
        }

        public string LogPath => Path.Combine(_logRoot, JobId.ToString("N"), "provision.log");

        public async Task<Rig> StartAsync()
        {
            Vm = await Kit.Hypervisor.StartAsync(new VmSpec(Guid.NewGuid(), "win11-build", 4096, 2, "fake-net", Writable: true), Ct);
            return this;
        }

        public IAgentChannelFactory? Channels { get; set; }

        public TemplateInstaller Installer(IHypervisor? hv = null) => new(hv ?? Kit.Hypervisor, Channels ?? Kit.ChannelFactory,
            Microsoft.Extensions.Options.Options.Create(Kit.Options), Microsoft.Extensions.Options.Options.Create(Build), Time,
            NullLogger<TemplateInstaller>.Instance);

        public Task<ProvisionReport> InstallAsync(bool office = false, IHypervisor? hv = null, CancellationToken? ct = null) =>
            Installer(hv).InstallAsync(Vm, JobId, office, (state, note) =>
            {
                lock (Phases) Phases.Add((state, note));
                return Task.CompletedTask;
            }, ct ?? Ct);

        public IReadOnlyList<string> Keys => Kit.Hypervisor.KeysSent.Where(k => k.Vm == Vm).Select(k => k.Key).ToList();

        public IEnumerable<AgentCommand> Sent => Kit.Channels.SelectMany(c => c.SentCommands);

        public async ValueTask DisposeAsync()
        {
            await Kit.DisposeAsync();
            Directory.Delete(_logRoot, recursive: true);
        }
    }

    private static async Task<TemplateBuildException> FailsAsync(Rig rig, Task run, TimeSpan step) =>
        await Assert.ThrowsAsync<TemplateBuildException>(() => rig.Kit.PumpAsync(run, step));

    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    /// <summary>Advances the fake clock a second per real millisecond until <paramref name="done"/> holds (at most 30 s of real time).</summary>
    private static async Task AdvanceUntilAsync(Rig rig, Func<bool> done)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!done())
        {
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), "the expected state was not reached within 30 s of real time");
            rig.Time.Advance(Second);
            await Task.Delay(1, Ct);
        }
    }
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task Install_HappyPath_ReturnsTheReport_EntersProvisioningOnce_TapsKeys_WritesTheLog_PowersOff()
    {
        await using var rig = await new Rig().StartAsync();
        rig.Kit.ReadyDelay = TimeSpan.FromSeconds(5); // Windows Setup takes a while; the CD prompt is answered meanwhile

        var report = await rig.Kit.PumpAsync(rig.InstallAsync());

        Assert.Equal(new ProvisionReport("Windows 11 Pro", "26300.1", false, false), report);
        Assert.Equal([(TemplateJobState.Provisioning, "installing drivers, Sysmon, policies and applications")], rig.Phases);
        // The prompt is answered with spaces, and the tapping stops once the agent is up (well before all 25 taps).
        Assert.NotEmpty(rig.Keys);
        Assert.All(rig.Keys, k => Assert.Equal("KEY_SPACE", k));
        Assert.True(rig.Keys.Count < rig.Build.BootKeyTaps, $"{rig.Keys.Count} taps: tapping did not stop when the agent was ready");
        // The log is kept, private to the service account.
        Assert.True(rig.Kit.Guest.TryGetFile(FakeGuest.ProvisionLogPath, out var log));
        Assert.Equal(log.Data, await File.ReadAllBytesAsync(rig.LogPath, Ct));
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(rig.LogPath));
        // It ended with a clean power-off.
        Assert.Single(rig.Sent.OfType<PowerOffCommand>());
        Assert.False(await rig.Kit.Hypervisor.IsRunningAsync(rig.Vm, Ct));
    }

    [Fact]
    public void FakeGuest_ProvisioningPaths_AreTheBuildsGuestPaths()
    {
        Assert.Equal(BuildOptions.ProvisionResultGuestPath, FakeGuest.ProvisionResultPath);
        Assert.Equal(BuildOptions.ProvisionLogGuestPath, FakeGuest.ProvisionLogPath);
        Assert.True(new FakeGuest().TryGetFile(BuildOptions.ProvisionResultGuestPath, out var result));
        Assert.Equal("""{"ok":true,"failedSteps":[]}"""u8.ToArray(), result.Data); // Provision.ps1's shape, UTF-8 without BOM
    }

    [Fact]
    public async Task Install_VmStopsBeforeReady_FailsAtOnce()
    {
        await using var rig = await new Rig().StartAsync();
        rig.Kit.VerifyAgentReady = false;
        await rig.Kit.Hypervisor.DestroyAsync(rig.Vm, Ct); // Setup gave up (wrong edition, bad media)
        var started = rig.Time.GetUtcNow();

        var ex = await FailsAsync(rig, rig.InstallAsync(), Second);

        Assert.Equal("Windows Setup stopped before the Maldex agent started (check the media and edition)", ex.Message);
        Assert.True(rig.Time.GetUtcNow() - started < Minute, "it waited instead of failing at once");
        Assert.Empty(rig.Phases);
    }

    [Fact]
    public async Task Install_NoAgentWithin3h_Fails()
    {
        await using var rig = await new Rig().StartAsync();
        rig.Kit.VerifyAgentReady = false;
        var started = rig.Time.GetUtcNow();

        var ex = await FailsAsync(rig, rig.InstallAsync(), Minute);

        Assert.Equal("Windows was not installed within 3 h (the Maldex agent never started)", ex.Message);
        Assert.InRange(rig.Time.GetUtcNow() - started, TimeSpan.FromHours(3), TimeSpan.FromHours(3) + TimeSpan.FromMinutes(5));
        Assert.Equal(rig.Build.BootKeyTaps, rig.Keys.Count); // all taps used, then no more
        Assert.Empty(rig.Phases);
    }

    [Fact]
    public async Task Install_AgentNeverAnswersAfterReady_Fails()
    {
        await using var rig = await new Rig().StartAsync();
        rig.Kit.Guest.SilentProvenance = true;

        var ex = await FailsAsync(rig, rig.InstallAsync(), Second);

        Assert.StartsWith("the guest agent did not answer", ex.Message);
        Assert.Empty(rig.Phases);
    }

    [Fact]
    public async Task Provision_ResultFailed_NamesTheSteps_AndKeepsTheLog()
    {
        await using var rig = await new Rig().StartAsync();
        rig.Kit.Guest.SetProvisionResult("""{"ok":false,"failedSteps":["virtio drivers","office\u0007"]}""");

        var ex = await FailsAsync(rig, rig.InstallAsync(), Second);

        Assert.Equal("provisioning failed in the guest: virtio drivers, office; see the provisioning log", ex.Message);
        Assert.True(File.Exists(rig.LogPath));
        Assert.Empty(rig.Sent.OfType<PowerOffCommand>());
    }

    [Fact]
    public async Task Provision_LongLog_KeepsItsLastPart()
    {
        await using var rig = await new Rig().StartAsync();
        rig.Build.ProvisionLogMaxBytes = 1000;
        var log = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 400).Select(i => $"line {i}\n")));
        rig.Kit.Guest.AddFile(FakeGuest.ProvisionLogPath, log);

        await rig.Kit.PumpAsync(rig.InstallAsync());

        Assert.Equal(log[^1000..], await File.ReadAllBytesAsync(rig.LogPath, Ct));
    }

    [Fact]
    public async Task Provision_NoLog_StillSucceeds()
    {
        await using var rig = await new Rig().StartAsync();
        rig.Kit.Guest.RemoveFile(FakeGuest.ProvisionLogPath);

        await rig.Kit.PumpAsync(rig.InstallAsync());

        Assert.False(File.Exists(rig.LogPath));
    }

    [Fact]
    public async Task Provision_NoResultWithin2h_Fails()
    {
        await using var rig = await new Rig().StartAsync();
        rig.Kit.Guest.SetProvisionResult(null);
        var started = rig.Time.GetUtcNow();

        var ex = await FailsAsync(rig, rig.InstallAsync(), Minute);

        Assert.Equal("provisioning did not finish within 2 h", ex.Message);
        Assert.True(File.Exists(rig.LogPath), "the provisioning log was not kept");
        Assert.InRange(rig.Time.GetUtcNow() - started, TimeSpan.FromHours(2), TimeSpan.FromHours(2) + TimeSpan.FromMinutes(5));
        // Polled about every 15 s, not in a tight loop.
        var polls = rig.Sent.OfType<CollectFileCommand>().Count(c => c.Path == BuildOptions.ProvisionResultGuestPath);
        Assert.InRange(polls, 100, 2 * 60 * 4 + 1);
    }

    [Fact]
    public async Task Provision_ResultAppearsLater_IsPicked_Up()
    {
        await using var rig = await new Rig().StartAsync();
        rig.Kit.Guest.SetProvisionResult(null);
        var run = rig.InstallAsync();
        await AdvanceUntilAsync(rig, () => rig.Sent.OfType<CollectFileCommand>().Count() >= 3); // a few polls with nothing there yet
        rig.Kit.Guest.SetProvisionResult(FakeGuest.ProvisionSucceeded);

        var report = await rig.Kit.PumpAsync(run);

        Assert.Equal("Windows 11 Pro", report.WindowsEdition);
    }

    [Fact]
    public async Task Provision_VmStops_FailsAtOnce()
    {
        await using var rig = await new Rig().StartAsync();
        rig.Kit.Guest.SetProvisionResult(null);
        var run = rig.InstallAsync();
        await AdvanceUntilAsync(rig, () => rig.Phases.Count > 0);
        await rig.Kit.Hypervisor.DestroyAsync(rig.Vm, Ct);
        var stopped = rig.Time.GetUtcNow();

        var ex = await FailsAsync(rig, run, Second);

        Assert.Equal("the guest stopped before provisioning finished", ex.Message);
        Assert.True(File.Exists(rig.LogPath), "the provisioning log was not kept"); // the fake guest still serves it
        Assert.True(rig.Time.GetUtcNow() - stopped < Minute, "it waited instead of failing at once");
    }

    public static TheoryData<string> MalformedResults => new()
    {
        "not json",
        "[]",
        """{"ok":"yes","failedSteps":[]}""",
        """{"failedSteps":[]}""",
        """{"ok":true}""",
        """{"ok":false,"failedSteps":"office"}""",
        """{"ok":false,"failedSteps":[1]}""",
        """{"ok":false,"failedSteps":[""" + string.Join(",", Enumerable.Range(0, 21).Select(i => $"\"step {i}\"")) + "]}",
        """{"ok":true,"failedSteps":[]} trailing""",
        """{"ok":true,"failedSteps":["office"]}""", // a success that names a failed step contradicts itself
        new string(' ', 5000) + """{"ok":true,"failedSteps":[]}""", // over the 4096-byte limit
    };

    [Theory]
    [MemberData(nameof(MalformedResults))]
    public async Task Provision_MalformedOrOversizeResult_IsRejected(string marker)
    {
        await using var rig = await new Rig().StartAsync();
        rig.Kit.Guest.SetProvisionResult(marker);

        var ex = await FailsAsync(rig, rig.InstallAsync(), Second);

        Assert.Equal("the provisioning result could not be read", ex.Message);
        Assert.True(File.Exists(rig.LogPath), "the provisioning log was not kept");
        Assert.Empty(rig.Sent.OfType<PowerOffCommand>());
    }

    [Fact]
    public async Task Provision_TwentySteps_AreAccepted_AndLongNamesCut()
    {
        await using var rig = await new Rig().StartAsync();
        var steps = Enumerable.Range(0, 20).Select(i => i == 0 ? new string('x', 300) : $"s{i}").ToList();
        rig.Kit.Guest.SetProvisionResult(System.Text.Json.JsonSerializer.Serialize(new { ok = false, failedSteps = steps }));

        var ex = await FailsAsync(rig, rig.InstallAsync(), Second);

        Assert.StartsWith("provisioning failed in the guest: " + new string('x', 100) + "…[truncated], s1, s2", ex.Message);
        Assert.EndsWith("s19; see the provisioning log", ex.Message);
    }

    [Fact]
    public async Task Provision_OfficeSelectedButAbsent_Fails()
    {
        await using var rig = await new Rig().StartAsync();
        rig.Kit.Guest.ProvenanceOfficePresent = false;

        var ex = await FailsAsync(rig, rig.InstallAsync(office: true), Second);

        Assert.Equal("Office was selected but is not installed; see the provisioning log", ex.Message);
        Assert.True(File.Exists(rig.LogPath));
    }

    [Fact]
    public async Task Provision_OfficeSelectedAndUnknown_Fails()
    {
        await using var rig = await new Rig().StartAsync();
        rig.Kit.Guest.ProvenanceOfficePresent = null;

        var ex = await FailsAsync(rig, rig.InstallAsync(office: true), Second);

        Assert.Equal("Office was selected but is not installed; see the provisioning log", ex.Message);
    }

    [Fact]
    public async Task Provision_OfficeSelectedAndPresent_Succeeds()
    {
        await using var rig = await new Rig().StartAsync();
        rig.Kit.Guest.ProvenanceOfficePresent = true;

        var report = await rig.Kit.PumpAsync(rig.InstallAsync(office: true));

        Assert.True(report.OfficePresent);
    }

    [Fact]
    public async Task Provision_NoPowerOff_Fails()
    {
        await using var rig = await new Rig(hypervisorSeesPowerOff: false).StartAsync(); // still running after power_off
        rig.Kit.Options.PowerOffTimeout = TimeSpan.FromMinutes(3);

        var ex = await FailsAsync(rig, rig.InstallAsync(), Second);

        Assert.Equal("the guest did not power off after provisioning", ex.Message);
        Assert.Single(rig.Sent.OfType<PowerOffCommand>());
    }

    /// <summary>The kit's channels, noting when (on the fake clock) each collect_file was sent.</summary>
    private sealed class TimedCollects(Rig rig) : IAgentChannelFactory
    {
        public List<(string Path, DateTimeOffset At)> Collects { get; } = [];

        public async Task<IAgentChannel> ConnectAsync(VmHandle vm, CancellationToken ct) =>
            new Timed(await rig.Kit.ChannelFactory.ConnectAsync(vm, ct), this);

        private sealed class Timed(IAgentChannel inner, TimedCollects owner) : IAgentChannel
        {
            public int Violations => inner.Violations;
            public IAsyncEnumerable<AgentMessage> ReadAsync(CancellationToken ct) => inner.ReadAsync(ct);
            public ValueTask DisposeAsync() => inner.DisposeAsync();

            public Task SendAsync(AgentCommand command, CancellationToken ct)
            {
                if (command is CollectFileCommand c) lock (owner.Collects) owner.Collects.Add((c.Path, owner._time.GetUtcNow()));
                return inner.SendAsync(command, ct);
            }
        }

        private readonly TimeProvider _time = rig.Time;
    }

    [Fact]
    public async Task Provision_LogIsReadOneIntervalAfterTheMarker_AndRetriedOnce()
    {
        await using var rig = await new Rig().StartAsync();
        var timed = new TimedCollects(rig);
        rig.Channels = timed;
        rig.Kit.Guest.RemoveFile(FakeGuest.ProvisionLogPath); // the transcript is not readable yet
        int LogReads() { lock (timed.Collects) return timed.Collects.Count(c => c.Path == BuildOptions.ProvisionLogGuestPath); }
        var run = rig.InstallAsync();
        await AdvanceUntilAsync(rig, () => LogReads() == 1);
        var log = "**********************\nWindows PowerShell transcript end\n"u8.ToArray();
        rig.Kit.Guest.AddFile(FakeGuest.ProvisionLogPath, log);

        await rig.Kit.PumpAsync(run);

        var marker = timed.Collects.Last(c => c.Path == BuildOptions.ProvisionResultGuestPath).At;
        var reads = timed.Collects.Where(c => c.Path == BuildOptions.ProvisionLogGuestPath).Select(c => c.At).ToList();
        Assert.Equal(2, reads.Count); // the first found nothing, the one retry found the log
        Assert.True(reads[0] - marker >= rig.Build.ProvisionPollInterval, $"log read {reads[0] - marker} after the marker: Provision.ps1 may still be writing it");
        Assert.True(reads[1] - reads[0] >= rig.Build.ProvisionPollInterval, $"retried after {reads[1] - reads[0]}");
        Assert.Equal(log, await File.ReadAllBytesAsync(rig.LogPath, Ct));
    }

    /// <summary>IsRunningAsync (or the agent connection) failing with a cancellation nobody asked for: an internal timeout.</summary>
    private sealed class InternalTimeoutHypervisor(IHypervisor inner, Func<bool> failWhen) : IHypervisor
    {
        public Task<VmHandle> StartAsync(VmSpec spec, CancellationToken ct) => inner.StartAsync(spec, ct);
        public Task DestroyAsync(VmHandle vm, CancellationToken ct) => inner.DestroyAsync(vm, ct);
        public Task<IReadOnlyList<VmHandle>> ListAsync(CancellationToken ct) => inner.ListAsync(ct);
        public Task SendKeyAsync(VmHandle vm, string key, CancellationToken ct) => inner.SendKeyAsync(vm, key, ct);
        public Task<bool> IsRunningAsync(VmHandle vm, CancellationToken ct) =>
            failWhen() ? throw new OperationCanceledException() : inner.IsRunningAsync(vm, ct);
    }

    private sealed class TimesOutFactory : IAgentChannelFactory
    {
        public Task<IAgentChannel> ConnectAsync(VmHandle vm, CancellationToken ct) => throw new TaskCanceledException();
    }

    [Theory]
    [InlineData("connect", "could not connect to the guest agent (it timed out)")]
    [InlineData("install", "could not check whether the guest is running: it timed out")]
    [InlineData("provision", "could not check whether the guest is running: it timed out")]
    public async Task Install_InternalTimeout_IsABuildFailure_NotACancellation(string where, string expected)
    {
        await using var rig = await new Rig().StartAsync();
        rig.Kit.VerifyAgentReady = where != "install";
        rig.Kit.Guest.SetProvisionResult(null);
        if (where == "connect") rig.Channels = new TimesOutFactory();
        var hv = new InternalTimeoutHypervisor(rig.Kit.Hypervisor, () => where == "install" || (where == "provision" && rig.Phases.Count > 0));

        var ex = await FailsAsync(rig, rig.InstallAsync(hv: hv), Second);

        Assert.Equal(expected, ex.Message);
        Assert.DoesNotContain("operation was canceled", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>IsRunningAsync that, once <paramref name="blockWhen"/> holds, waits for its token and then fails with a non-cancellation error (as SqlClient or virsh do).</summary>
    private sealed class ThrowsOnCancelHypervisor(IHypervisor inner, Func<bool> blockWhen, TaskCompletionSource blocked) : IHypervisor
    {
        public Task<VmHandle> StartAsync(VmSpec spec, CancellationToken ct) => inner.StartAsync(spec, ct);
        public Task DestroyAsync(VmHandle vm, CancellationToken ct) => inner.DestroyAsync(vm, ct);
        public Task<IReadOnlyList<VmHandle>> ListAsync(CancellationToken ct) => inner.ListAsync(ct);
        public Task SendKeyAsync(VmHandle vm, string key, CancellationToken ct) => inner.SendKeyAsync(vm, key, ct);

        public async Task<bool> IsRunningAsync(VmHandle vm, CancellationToken ct)
        {
            if (!blockWhen()) return await inner.IsRunningAsync(vm, ct);
            blocked.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { }
            throw new InvalidOperationException("virsh domstate failed (1): interrupted");
        }
    }

    [Theory]
    [InlineData(false)] // waiting for the agent
    [InlineData(true)] // waiting for the provisioning result
    public async Task Install_CancelledMidQuery_ThrowsAsCancellation(bool duringProvisioning)
    {
        await using var rig = await new Rig().StartAsync();
        rig.Kit.VerifyAgentReady = duringProvisioning;
        rig.Kit.Guest.SetProvisionResult(null);
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hv = new ThrowsOnCancelHypervisor(rig.Kit.Hypervisor, () => !duringProvisioning || rig.Phases.Count > 0, blocked);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var run = rig.InstallAsync(hv: hv, ct: cts.Token);
        await AdvanceUntilAsync(rig, () => blocked.Task.IsCompleted);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(30), Ct));
    }
}

/// <summary>The power-off step the sealer and the installer share.</summary>
public class GuestPowerOffTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Refuses power_off, and lets the deadline pass the moment the refusal has reached the host's inbox: the refusal and the
    /// deadline arrive together.
    /// </summary>
    private sealed class RefusesPowerOffAtTheDeadline : IAgentChannel
    {
        private readonly ReplayAgentChannel _inner;
        private readonly FakeTimeProvider _time;
        private readonly TimeSpan _deadline;
        private readonly Channel<AgentMessage> _incoming = Channel.CreateUnbounded<AgentMessage>();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _copy;
        private readonly TaskCompletionSource _delivered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string? _powerOffId;

        public RefusesPowerOffAtTheDeadline(ReplayAgentChannel inner, FakeTimeProvider time, TimeSpan deadline)
        {
            _inner = inner;
            _time = time;
            _deadline = deadline;
            _copy = Task.Run(async () =>
            {
                try { await foreach (var m in inner.ReadAsync(_stop.Token)) await _incoming.Writer.WriteAsync(m); }
                catch (OperationCanceledException) { }
            });
        }

        public int Violations => _inner.Violations;

        public async Task SendAsync(AgentCommand command, CancellationToken ct)
        {
            if (command is not PowerOffCommand p)
            {
                await _inner.SendAsync(command, ct);
                return;
            }
            _powerOffId = p.CommandId;
            _incoming.Writer.TryWrite(new CommandResultMessage(p.CommandId, false, "shutdown is blocked by policy"));
            await _delivered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            _time.Advance(_deadline);
        }

        public async IAsyncEnumerable<AgentMessage> ReadAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var m in _incoming.Reader.ReadAllAsync(ct))
            {
                yield return m;
                // Runs when the reader asks for the next frame, i.e. once this one has been handed on.
                if (m is CommandResultMessage r && r.CommandId == _powerOffId) _delivered.TrySetResult();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _copy;
        }
    }

    [Fact]
    public async Task Seal_PowerOffRefusedExactlyAtTheDeadline_IsReportedAsARefusal()
    {
        await using var kit = new TemplateJobKit(hypervisorSeesPowerOff: false);
        var time = kit.UseFakeClock();
        kit.Options.PowerOffTimeout = TimeSpan.FromMinutes(3);
        var (inner, vm) = await kit.StartTemplateVmAsync();
        await using var ch = new RefusesPowerOffAtTheDeadline(inner, time, kit.Options.PowerOffTimeout);

        var ex = await Assert.ThrowsAsync<TemplateSealException>(() => kit.Sealer.SealAsync(ch, vm, "win11-edit", kit.Bundle, Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct));

        Assert.Equal("powering off failed: the agent refused it (shutdown is blocked by policy)", ex.Message);
        Assert.Empty(kit.Store.Seals);
    }
}
