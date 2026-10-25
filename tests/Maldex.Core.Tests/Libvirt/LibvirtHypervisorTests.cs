using Maldex.Core.Abstractions;
using Maldex.Infrastructure.Libvirt;
using Maldex.Infrastructure.Execution;
using Microsoft.Extensions.Options;
using Xunit;

namespace Maldex.Core.Tests.Libvirt;

public sealed class LibvirtHypervisorTests : IDisposable
{
    private sealed class Recorder : IProcessRunner
    {
        public readonly List<(string File, string[] Args)> Calls = [];
        public Func<string, string[], ProcessResult> Handler { get; set; } = (_, _) => new(0, "", "");
        public Task<ProcessResult> RunAsync(string file, IReadOnlyList<string> args, CancellationToken ct, string? stdin = null, TimeSpan? timeout = null)
        {
            var a = args.ToArray();
            Calls.Add((file, a));
            return Task.FromResult(Handler(file, a));
        }
    }

    private static readonly Guid Id = Guid.Parse("0123456789abcdef0123456789abcdef");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mx-lv-" + Guid.NewGuid().ToString("N"));
    private readonly Recorder _runner = new();
    private readonly LibvirtOptions _opts;

    public LibvirtHypervisorTests()
    {
        _opts = new LibvirtOptions { BaseImageDir = Path.Combine(_root, "base"), RunDir = Path.Combine(_root, "run") };
        Directory.CreateDirectory(_opts.BaseImageDir);
        Directory.CreateDirectory(_opts.RunDir);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private LibvirtHypervisor Hv() => new(Options.Create(_opts), _runner);
    private VmSpec Spec() => new(Id, "win11-26h2", 4096, 2, "mxt" + Id.ToString("N")[..12]);
    private string RunDirOf(VmHandle h) => Path.Combine(_opts.RunDir, h.Name);

    private void SeedBase()
    {
        File.WriteAllText(Path.Combine(_opts.BaseImageDir, "win11-26h2.qcow2"), "q");
        File.WriteAllText(Path.Combine(_opts.BaseImageDir, "win11-26h2.vars.fd"), "vars");
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task Start_CreatesOverlayFromBase_ThenCreatesTransientDomain()
    {
        SeedBase();
        var h = await Hv().StartAsync(Spec(), CancellationToken.None);
        var dir = RunDirOf(h);
        Assert.Equal(VmHandle.For(Id), h);
        Assert.Equal(2, _runner.Calls.Count);
        Assert.Equal("qemu-img", _runner.Calls[0].File);
        Assert.Equal(["create", "-f", "qcow2", "-F", "qcow2", "-b", Path.Combine(_opts.BaseImageDir, "win11-26h2.qcow2"), Path.Combine(dir, "overlay.qcow2")], _runner.Calls[0].Args);
        Assert.Equal("virsh", _runner.Calls[1].File);
        Assert.Equal(["-c", "qemu:///session", "create", Path.Combine(dir, "domain.xml")], _runner.Calls[1].Args);

        Assert.Equal("vars", File.ReadAllText(Path.Combine(dir, "nvram.fd")));
        var pass = File.ReadAllText(Path.Combine(dir, "vnc.pass"));
        Assert.Matches("^[A-Za-z0-9]{8}$", pass);
        var xml = File.ReadAllText(Path.Combine(dir, "domain.xml"));
        Assert.Contains(Path.Combine(dir, "agent.sock"), xml);
        Assert.Contains($"passwd=\"{pass}\"", xml);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(dir));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(dir, "vnc.pass")));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(dir, "domain.xml")));
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task Start_Writable_BootsTheDiskDirectly_NoOverlay()
    {
        SeedBase();
        var h = await Hv().StartAsync(Spec() with { Writable = true }, CancellationToken.None);
        var dir = RunDirOf(h);
        var call = Assert.Single(_runner.Calls);
        Assert.Equal("virsh", call.File);
        var xml = File.ReadAllText(Path.Combine(dir, "domain.xml"));
        Assert.Contains(Path.Combine(_opts.BaseImageDir, "win11-26h2.qcow2"), xml);
        Assert.Contains(Path.Combine(_opts.BaseImageDir, "win11-26h2.vars.fd"), xml);
        Assert.DoesNotContain("overlay.qcow2", xml);
        Assert.False(File.Exists(Path.Combine(dir, "overlay.qcow2")));
        Assert.False(File.Exists(Path.Combine(dir, "nvram.fd")));
        Assert.True(File.Exists(Path.Combine(dir, "vnc.pass")));
        Assert.True(File.Exists(Path.Combine(dir, "domain.xml")));
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task Start_Writable_RefusesADiskThatIsNotOwnerWritable_ASealedVersion()
    {
        SeedBase();
        // A sealed template version is 0444: QEMU running as root would otherwise write straight through the mode.
        var disk = Path.Combine(_opts.BaseImageDir, "win11-26h2.qcow2");
        File.SetUnixFileMode(disk, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        var ex = await Assert.ThrowsAsync<VmStartException>(() => Hv().StartAsync(Spec() with { Writable = true }, CancellationToken.None));

        Assert.Contains("win11-26h2", ex.Message);
        Assert.Contains("sealed", ex.Message);
        Assert.Empty(_runner.Calls);
        Assert.False(Directory.Exists(RunDirOf(VmHandle.For(Id))));
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task Start_NotWritable_StillBootsASealedDisk_ThroughAnOverlay()
    {
        SeedBase();
        File.SetUnixFileMode(Path.Combine(_opts.BaseImageDir, "win11-26h2.qcow2"), UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        await Hv().StartAsync(Spec(), CancellationToken.None);
        Assert.Equal("qemu-img", _runner.Calls[0].File);
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task Start_NvramIsWritable_EvenThoughTheVarsTemplateIsReadOnly()
    {
        SeedBase();
        // sealing finalises the template 0444; QEMU must still be able to write the run's copy (UEFI variable store).
        var template = Path.Combine(_opts.BaseImageDir, "win11-26h2.vars.fd");
        File.SetUnixFileMode(template, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        var dir = RunDirOf(await Hv().StartAsync(Spec(), CancellationToken.None));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(dir, "nvram.fd")));
        Assert.Equal("vars", File.ReadAllText(Path.Combine(dir, "nvram.fd")));
    }

    [Fact]
    public async Task Start_MissingBaseImage_ThrowsVmStart_NoVirshCall()
    {
        var ex = await Assert.ThrowsAsync<VmStartException>(() => Hv().StartAsync(Spec(), CancellationToken.None));
        Assert.Contains("not found", ex.Message);
        Assert.DoesNotContain(_runner.Calls, c => c.File == "virsh" && c.Args.Contains("create"));
    }

    [Fact]
    public async Task Start_MissingVarsTemplate_ThrowsVmStart()
    {
        File.WriteAllText(Path.Combine(_opts.BaseImageDir, "win11-26h2.qcow2"), "q");
        await Assert.ThrowsAsync<VmStartException>(() => Hv().StartAsync(Spec(), CancellationToken.None));
        Assert.DoesNotContain(_runner.Calls, c => c.File == "virsh" && c.Args.Contains("create"));
    }

    [Fact]
    public async Task StartFailure_ThrowsVmStart_AndRemovesRunDir()
    {
        SeedBase();
        _runner.Handler = (f, a) => f == "virsh" && a.Contains("create") ? new(1, "", "error: boom") : new(0, "", "");
        var ex = await Assert.ThrowsAsync<VmStartException>(() => Hv().StartAsync(Spec(), CancellationToken.None));
        Assert.Contains("boom", ex.Message);
        Assert.False(Directory.Exists(RunDirOf(VmHandle.For(Id))));
        Assert.Contains(_runner.Calls, c => c.File == "virsh" && c.Args.Contains("destroy"));
    }

    [Fact]
    public async Task StartFailure_DestroyAlsoFails_StillRemovesRunDir()
    {
        SeedBase();
        _runner.Handler = (f, a) => a.Contains("create") ? new(1, "", "error: boom")
            : a.Contains("destroy") ? new(1, "", "error: cannot connect") : new(0, "", "");
        await Assert.ThrowsAsync<VmStartException>(() => Hv().StartAsync(Spec(), CancellationToken.None));
        Assert.False(Directory.Exists(RunDirOf(VmHandle.For(Id))));
    }

    [Fact]
    public async Task Start_AgentSocketPathTooLong_ThrowsVmStart()
    {
        SeedBase();
        _opts.RunDir = Path.Combine(_root, new string('d', 90));
        Directory.CreateDirectory(_opts.RunDir);
        var ex = await Assert.ThrowsAsync<VmStartException>(() => Hv().StartAsync(Spec(), CancellationToken.None));
        Assert.Contains("too long", ex.Message);
        Assert.DoesNotContain(_runner.Calls, c => c.Args.Contains("create") && c.File == "virsh");
    }

    [Fact]
    public async Task Destroy_Idempotent_IgnoresNotFound()
    {
        var h = VmHandle.For(Id);
        Directory.CreateDirectory(RunDirOf(h));
        File.WriteAllText(Path.Combine(RunDirOf(h), "x"), "x");
        _runner.Handler = (_, _) => new(1, "", "error: failed to get domain 'maldex-x'");
        var hv = Hv();
        await hv.DestroyAsync(h, CancellationToken.None);
        await hv.DestroyAsync(h, CancellationToken.None);
        Assert.False(Directory.Exists(RunDirOf(h)));
        Assert.Equal(["-c", "qemu:///session", "destroy", h.Name], _runner.Calls[0].Args);

        _runner.Handler = (_, _) => new(1, "", "error: Requested operation is not valid: domain is not running");
        await hv.DestroyAsync(h, CancellationToken.None);
    }

    [Fact]
    public async Task Destroy_OtherError_Throws()
    {
        _runner.Handler = (_, _) => new(1, "", "error: cannot connect");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Hv().DestroyAsync(VmHandle.For(Id), CancellationToken.None));
    }

    [Fact]
    public async Task List_FiltersMaldexNames_AndIncludesOrphanRunDirs()
    {
        var running = Guid.NewGuid();
        var orphan = Guid.NewGuid();
        Directory.CreateDirectory(Path.Combine(_opts.RunDir, VmHandle.For(orphan).Name));
        Directory.CreateDirectory(Path.Combine(_opts.RunDir, VmHandle.For(running).Name));
        Directory.CreateDirectory(Path.Combine(_opts.RunDir, "maldex-notaguid"));
        _runner.Handler = (_, _) => new(0, $"{VmHandle.For(running).Name}\nother-vm\nmaldex-zzz\n\n", "");
        var list = await Hv().ListAsync(CancellationToken.None);
        Assert.Equal(["-c", "qemu:///session", "list", "--all", "--name"], _runner.Calls[0].Args);
        Assert.Equal(2, list.Count);
        Assert.Contains(VmHandle.For(running), list);
        Assert.Contains(VmHandle.For(orphan), list);
    }

    [Theory]
    [InlineData("running", true)]
    [InlineData("paused", true)]
    [InlineData("in shutdown", true)]
    [InlineData("blocked", true)]
    [InlineData("shut off", false)]
    [InlineData("crashed", false)]
    [InlineData("pmsuspended", false)]
    public async Task IsRunning_MapsDomstate(string state, bool expected)
    {
        _runner.Handler = (_, _) => new(0, state + "\n\n", "");
        var vm = VmHandle.For(Id);
        Directory.CreateDirectory(RunDirOf(vm)); // the run dir lingers after shutdown; it must not count

        Assert.Equal(expected, await Hv().IsRunningAsync(vm, CancellationToken.None));
        Assert.Equal(["-c", "qemu:///session", "domstate", vm.Name], _runner.Calls[0].Args);
    }

    [Fact]
    public async Task IsRunning_UnknownDomain_IsFalse()
    {
        _runner.Handler = (_, _) => new(1, "", "error: failed to get domain 'maldex-x'\nerror: Domain not found: no domain with matching name\n");

        Assert.False(await Hv().IsRunningAsync(VmHandle.For(Id), CancellationToken.None));
    }

    [Fact]
    public async Task IsRunning_OtherVirshFailure_Throws()
    {
        _runner.Handler = (_, _) => new(1, "", "error: failed to connect to the hypervisor");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Hv().IsRunningAsync(VmHandle.For(Id), CancellationToken.None));
    }

    [Fact]
    public async Task VncLocator_ParsesDisplay_AndReadsPassword()
    {
        var h = VmHandle.For(Id);
        Directory.CreateDirectory(RunDirOf(h));
        File.WriteAllText(Path.Combine(RunDirOf(h), "vnc.pass"), "Ab3dEf9h");
        _runner.Handler = (_, _) => new(0, "127.0.0.1:7\n", "");
        var loc = new VncConsoleLocator(Options.Create(_opts), _runner);
        var ep = await loc.GetAsync(h, CancellationToken.None);
        Assert.Equal(new VncEndpoint("127.0.0.1", 5907, "Ab3dEf9h"), ep);
        Assert.Equal(["-c", "qemu:///session", "vncdisplay", h.Name], _runner.Calls[0].Args);

        _runner.Handler = (_, _) => new(1, "", "error: Domain not found");
        Assert.Null(await loc.GetAsync(h, CancellationToken.None));
    }

    private string Iso(string name)
    {
        var p = Path.Combine(_root, name);
        File.WriteAllText(p, "iso");
        return p;
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task Start_InstallBoot_AttachesTheIsosReadOnly_NoOverlay()
    {
        SeedBase();
        var isos = new[] { Iso("a.iso"), Iso("b.iso") };
        var h = await Hv().StartAsync(Spec() with { Writable = true, CdRoms = isos, BootFromCdRom = true }, CancellationToken.None);
        var call = Assert.Single(_runner.Calls);
        Assert.Equal("virsh", call.File);
        var xml = File.ReadAllText(Path.Combine(RunDirOf(h), "domain.xml"));
        Assert.Contains(isos[0], xml);
        Assert.Contains(isos[1], xml);
        Assert.Contains("<readonly />", xml);
        Assert.Contains("device=\"cdrom\"", xml);
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task Start_CdRomsOnOverlayBoot_Refused()
    {
        SeedBase();
        var ex = await Assert.ThrowsAsync<VmStartException>(() => Hv().StartAsync(Spec() with { CdRoms = [Iso("a.iso")] }, CancellationToken.None));
        Assert.Contains("CD-ROM", ex.Message);
        Assert.Empty(_runner.Calls);
        Assert.False(Directory.Exists(RunDirOf(VmHandle.For(Id))));
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task Start_CdRomMissingFile_Refused()
    {
        SeedBase();
        var missing = Path.Combine(_root, "nope.iso");
        var ex = await Assert.ThrowsAsync<VmStartException>(() => Hv().StartAsync(Spec() with { Writable = true, CdRoms = [Iso("a.iso"), missing] }, CancellationToken.None));
        Assert.Contains(missing, ex.Message);
        Assert.Empty(_runner.Calls);
        Assert.False(Directory.Exists(RunDirOf(VmHandle.For(Id))));
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task Start_TooManyOrBadCdRoms_Refused()
    {
        SeedBase();
        var five = Enumerable.Range(0, 5).Select(i => Iso($"{i}.iso")).ToArray();
        await Assert.ThrowsAsync<VmStartException>(() => Hv().StartAsync(Spec() with { Writable = true, CdRoms = five }, CancellationToken.None));
        await Assert.ThrowsAsync<VmStartException>(() => Hv().StartAsync(Spec() with { Writable = true, CdRoms = ["rel/a.iso"] }, CancellationToken.None));
        var notIso = Path.Combine(_root, "a.img");
        File.WriteAllText(notIso, "x");
        await Assert.ThrowsAsync<VmStartException>(() => Hv().StartAsync(Spec() with { Writable = true, CdRoms = [notIso] }, CancellationToken.None));
        await Assert.ThrowsAsync<VmStartException>(() => Hv().StartAsync(Spec() with { Writable = true, BootFromCdRom = true }, CancellationToken.None));
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task Start_SealedDiskGuard_StaysFirst_EvenWithBadCdRoms()
    {
        SeedBase();
        File.SetUnixFileMode(Path.Combine(_opts.BaseImageDir, "win11-26h2.qcow2"), UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        var ex = await Assert.ThrowsAsync<VmStartException>(() =>
            Hv().StartAsync(Spec() with { Writable = true, CdRoms = [Path.Combine(_root, "nope.iso")] }, CancellationToken.None));
        Assert.Contains("sealed", ex.Message);
    }

    [Fact]
    public async Task SendKey_RunsVirshSendKey()
    {
        var vm = VmHandle.For(Id);
        await Hv().SendKeyAsync(vm, "KEY_ENTER", CancellationToken.None);
        var call = Assert.Single(_runner.Calls);
        Assert.Equal("virsh", call.File);
        Assert.Equal(["-c", "qemu:///session", "send-key", vm.Name, "KEY_ENTER"], call.Args);
    }

    [Theory]
    [InlineData("ENTER")]
    [InlineData("key_enter")]
    [InlineData("KEY_")]
    [InlineData("KEY_ENTER\n")]
    [InlineData("KEY_ENTER KEY_A")]
    [InlineData("--codeset")]
    [InlineData("KEY_AAAAAAAAAAAAAAAAAAAAA")]
    public async Task SendKey_InvalidKey_Refused(string key)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Hv().SendKeyAsync(VmHandle.For(Id), key, CancellationToken.None));
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public async Task SendKey_VirshFailure_Throws()
    {
        _runner.Handler = (_, _) => new(1, "", "error: failed to get domain");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Hv().SendKeyAsync(VmHandle.For(Id), "KEY_ENTER", CancellationToken.None));
    }
}
