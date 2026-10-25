using System.Runtime.CompilerServices;
using Maldex.Core.Domain;
using Maldex.Infrastructure.Execution;
using Maldex.Infrastructure.Media;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Maldex.Core.Tests.Media;

[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class LoopMountMediaInspectorTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "maldex-inspect-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static string Fixture(string name, [CallerFilePath] string here = "") =>
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(here)!, "wiminfo", name));

    /// <summary>Records every call; on <c>mount</c> it lays out the "ISO" content in the mount point.</summary>
    private sealed class FakeRunner(Action<string> populate) : IProcessRunner
    {
        public List<(string File, string[] Args)> Calls { get; } = [];
        public int MountExit { get; set; }
        public string WiminfoOut { get; set; } = "";
        public int WiminfoExit { get; set; }
        public bool WiminfoTimesOut { get; set; }
        public int UmountExit { get; set; }
        public string? MountPointSeenByWiminfo { get; private set; }

        public Task<ProcessResult> RunAsync(string file, IReadOnlyList<string> args, CancellationToken ct, string? stdin = null, TimeSpan? timeout = null)
        {
            var a = args.ToArray();
            Calls.Add((file, a));
            if (file == "sudo" && a.Contains("mount"))
            {
                if (MountExit == 0) populate(a[^1]);
                return Task.FromResult(new ProcessResult(MountExit, "", MountExit == 0 ? "" : "mount: wrong fs type"));
            }
            if (file == "sudo" && a.Contains("umount"))
            {
                var exit = a.Contains("-l") ? 0 : UmountExit;
                // A real umount uncovers the empty mount point.
                if (exit == 0) foreach (var e in Directory.EnumerateFileSystemEntries(a[^1])) { if (Directory.Exists(e)) Directory.Delete(e, true); else File.Delete(e); }
                return Task.FromResult(new ProcessResult(exit, "", ""));
            }
            if (file == "wiminfo")
            {
                MountPointSeenByWiminfo = a[0];
                if (WiminfoTimesOut) throw new TimeoutException();
                return Task.FromResult(new ProcessResult(WiminfoExit, WiminfoOut, ""));
            }
            throw new InvalidOperationException("unexpected call " + file);
        }
    }

    private LoopMountMediaInspector Inspector(FakeRunner runner) =>
        new(runner, Options.Create(new MediaOptions { Root = _root }), NullLogger<LoopMountMediaInspector>.Instance);

    private static void Layout(string mnt, params string[] files)
    {
        foreach (var f in files)
        {
            var path = Path.Combine(mnt, f);
            if (f.EndsWith('/')) Directory.CreateDirectory(path);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "x");
            }
        }
    }

    private static FakeRunner Installer(params string[] files) =>
        new(mnt => Layout(mnt, files)) { WiminfoOut = Fixture("consumer-multi.txt") };

    private static bool MountDirs(string root) => Directory.Exists(Path.Combine(root, "mnt")) && Directory.EnumerateDirectories(Path.Combine(root, "mnt")).Any();

    [Fact]
    public async Task Mount_UsesExactlyTheReadOnlyNoExecArgv()
    {
        var runner = Installer("sources/install.wim");
        await Inspector(runner).InspectAsync("/media/x.iso", MediaKind.OsInstaller, Ct);

        var mount = runner.Calls[0];
        Assert.Equal("sudo", mount.File);
        Assert.Equal(["-n", "mount", "-o", "loop,ro,nosuid,nodev,noexec", "-t", "udf,iso9660", "/media/x.iso"], mount.Args[..^1]);
        var mnt = mount.Args[^1];
        Assert.StartsWith(Path.Combine(_root, "mnt") + Path.DirectorySeparatorChar, mnt);
        Assert.Equal(["-n", "umount", mnt], runner.Calls[^1].Args);
    }

    [Fact]
    public async Task OsInstaller_ReturnsTheImagesFromWiminfo_OnTheMountedFile()
    {
        var runner = Installer("SOURCES/Install.WIM");
        var result = await Inspector(runner).InspectAsync("/media/x.iso", MediaKind.OsInstaller, Ct);

        Assert.Null(result.Error);
        Assert.Equal(8, result.Contents!.Images.Count);
        Assert.Equal("Windows 11 Pro", result.Contents.Images[5].Name);
        Assert.Null(result.Contents.Application);
        Assert.EndsWith("SOURCES/Install.WIM", runner.MountPointSeenByWiminfo);
    }

    [Fact]
    public async Task OsInstaller_FindsInstallEsd()
    {
        var runner = Installer("sources/install.esd");
        var result = await Inspector(runner).InspectAsync("/media/x.iso", MediaKind.OsInstaller, Ct);
        Assert.Null(result.Error);
        Assert.EndsWith("sources/install.esd", runner.MountPointSeenByWiminfo);
    }

    [Fact]
    public async Task OsInstaller_SplitImage_IsNotSupported()
    {
        var runner = Installer("sources/install.swm");
        var result = await Inspector(runner).InspectAsync("/media/x.iso", MediaKind.OsInstaller, Ct);
        Assert.Equal("split install images (install.swm) are not supported", result.Error);
        Assert.DoesNotContain(runner.Calls, c => c.File == "wiminfo");
    }

    [Theory]
    [InlineData("readme.txt")]
    [InlineData("sources/boot.wim")]
    public async Task OsInstaller_WithoutAnInstallImage_IsNotAnInstaller(string file)
    {
        var runner = Installer(file);
        var result = await Inspector(runner).InspectAsync("/media/x.iso", MediaKind.OsInstaller, Ct);
        Assert.Equal("not a Windows installer: no sources/install.wim or install.esd", result.Error);
        Assert.Null(result.Contents);
    }

    [Fact]
    public async Task OsInstaller_WithZeroImages_IsNotAnInstaller()
    {
        var runner = Installer("sources/install.wim");
        runner.WiminfoOut = "WIM Information:\nImage Count: 0\n";
        var result = await Inspector(runner).InspectAsync("/media/x.iso", MediaKind.OsInstaller, Ct);
        Assert.Equal("not a Windows installer: no sources/install.wim or install.esd", result.Error);
    }

    [Fact]
    public async Task OsInstaller_IgnoresASymlinkedInstallImage()
    {
        var runner = new FakeRunner(mnt =>
        {
            Directory.CreateDirectory(Path.Combine(mnt, "sources"));
            File.CreateSymbolicLink(Path.Combine(mnt, "sources", "install.wim"), "/etc/passwd");
        }) { WiminfoOut = Fixture("consumer-multi.txt") };
        var result = await Inspector(runner).InspectAsync("/media/x.iso", MediaKind.OsInstaller, Ct);
        Assert.Equal("not a Windows installer: no sources/install.wim or install.esd", result.Error);
        Assert.DoesNotContain(runner.Calls, c => c.File == "wiminfo");
    }

    [Fact]
    public async Task MountFailure_ReportsTheReason_AndRunsNothingElseButUmount()
    {
        var runner = Installer("sources/install.wim");
        runner.MountExit = 32;
        var result = await Inspector(runner).InspectAsync("/media/x.iso", MediaKind.OsInstaller, Ct);

        Assert.Equal("the file could not be mounted read-only as an ISO (udf/iso9660)", result.Error);
        Assert.DoesNotContain(runner.Calls, c => c.File == "wiminfo");
        Assert.False(MountDirs(_root));
    }

    [Fact]
    public async Task Umount_RunsEvenWhenWiminfoFails()
    {
        var runner = Installer("sources/install.wim");
        runner.WiminfoExit = 1;
        var result = await Inspector(runner).InspectAsync("/media/x.iso", MediaKind.OsInstaller, Ct);

        Assert.NotNull(result.Error);
        Assert.Contains(runner.Calls, c => c.File == "sudo" && c.Args.Contains("umount"));
        Assert.False(MountDirs(_root));
    }

    [Fact]
    public async Task Umount_RunsEvenWhenWiminfoTimesOut()
    {
        var runner = Installer("sources/install.wim");
        runner.WiminfoTimesOut = true;
        var result = await Inspector(runner).InspectAsync("/media/x.iso", MediaKind.OsInstaller, Ct);

        Assert.Equal("reading the install image timed out", result.Error);
        Assert.Equal("umount", runner.Calls[^1].Args[1]);
        Assert.False(MountDirs(_root));
    }

    [Fact]
    public async Task Umount_FallsBackToLazyUnmount_WhenPlainUmountFails()
    {
        var runner = Installer("sources/install.wim");
        runner.UmountExit = 32;
        var result = await Inspector(runner).InspectAsync("/media/x.iso", MediaKind.OsInstaller, Ct);

        Assert.Null(result.Error);
        Assert.Equal(["-n", "umount"], runner.Calls[^2].Args[..2]);
        Assert.Equal(["-n", "umount", "-l"], runner.Calls[^1].Args[..3]);
        Assert.False(MountDirs(_root));
    }

    [Fact]
    public async Task Umount_FailingEverywhere_LeavesTheMountPointRatherThanDeletingThroughIt()
    {
        var runner = new FakeRunner(mnt => Layout(mnt, "sources/install.wim")) { WiminfoOut = Fixture("consumer-multi.txt") };
        var failing = new FailingUmount(runner);
        var inspector = new LoopMountMediaInspector(failing, Options.Create(new MediaOptions { Root = _root }), NullLogger<LoopMountMediaInspector>.Instance);
        await inspector.InspectAsync("/media/x.iso", MediaKind.OsInstaller, Ct);
        Assert.True(MountDirs(_root));
    }

    private sealed class FailingUmount(FakeRunner inner) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string file, IReadOnlyList<string> args, CancellationToken ct, string? stdin = null, TimeSpan? timeout = null) =>
            file == "sudo" && args.Contains("umount") ? Task.FromResult(new ProcessResult(32, "", "busy")) : inner.RunAsync(file, args, ct, stdin, timeout);
    }

    [Fact]
    public async Task MountPoint_IsOwnerOnly()
    {
        UnixFileMode? mode = null;
        var runner = new FakeRunner(mnt => mode = File.GetUnixFileMode(mnt)) { WiminfoOut = "" };
        await Inspector(runner).InspectAsync("/media/x.iso", MediaKind.Application, Ct);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, mode);
    }

    [Theory]
    [InlineData(true, "Office")]
    [InlineData(false, null)]
    public async Task Application_RecognisesOfficeBySetupExeAndOfficeData(bool office, string? expected)
    {
        var runner = new FakeRunner(mnt => Layout(mnt, office ? ["setup.exe", "Office/Data/"] : ["setup.exe", "Office/"]));
        var result = await Inspector(runner).InspectAsync("/media/x.iso", MediaKind.Application, Ct);

        Assert.Null(result.Error);
        Assert.Equal(expected, result.Contents!.Application);
        Assert.Empty(result.Contents.Images);
        Assert.DoesNotContain(runner.Calls, c => c.File == "wiminfo");
    }

    [Fact]
    public async Task Application_WithoutSetupExe_IsNotOffice()
    {
        var runner = new FakeRunner(mnt => Layout(mnt, "Office/Data/"));
        var result = await Inspector(runner).InspectAsync("/media/x.iso", MediaKind.Application, Ct);
        Assert.Null(result.Contents!.Application);
    }
}
