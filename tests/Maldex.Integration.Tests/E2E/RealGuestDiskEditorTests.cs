using System.Runtime.Versioning;
using System.Security.Cryptography;
using Maldex.Infrastructure.Execution;
using Maldex.Infrastructure.Libvirt;
using Maldex.Infrastructure.Real;
using Microsoft.Extensions.Options;
using Xunit;

namespace Maldex.Integration.Tests.E2E;

/// <summary>
/// Real libguestfs against a throwaway qcow2 overlay of the win11-26h2 image. The image itself is only ever the overlay's read-only
/// backing file: the test checks its size and mtime did not change. Skipped unless MALDEX_REAL_TEMPLATES=1 (./build.sh e2e-templates), MALDEX_REAL_VM=1, the image exists
/// (<c>MALDEX_REAL_BASE_IMAGE</c> overrides the repository's <c>.data/vm/base/win11-26h2.qcow2</c>) and the fixed appliance from
/// <c>./build.sh real-setup</c> is installed.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class RealGuestDiskEditorTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Appliance = "/var/lib/maldex/guestfs-appliance";
    private const string Stem = "agent-install-test";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "maldex-gfs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static string BaseImage =>
        Environment.GetEnvironmentVariable("MALDEX_REAL_BASE_IMAGE")
        ?? Path.Combine(RealPaths.FindRoot(AppContext.BaseDirectory), ".data", "vm", "base", "win11-26h2.qcow2");

    [Fact]
    public async Task InstallAgent_WritesTheExeIntoAnOverlay_AndLeavesTheBaseImageUntouched()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MALDEX_REAL_TEMPLATES") == "1",
            "Real guest disk editor test skipped: it also needs MALDEX_REAL_TEMPLATES=1. Run ./build.sh e2e-templates.");
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MALDEX_REAL_VM") == "1" && File.Exists(BaseImage) && Directory.Exists(Appliance),
            $"Real guest disk editor test skipped: set MALDEX_REAL_VM=1, have the image ({BaseImage}) and the libguestfs appliance ({Appliance}; ./build.sh real-setup).");

        var baseImage = Path.GetFullPath(BaseImage);
        var before = new FileInfo(baseImage);
        var (mtime, length) = (before.LastWriteTimeUtc, before.Length);

        Directory.CreateDirectory(_dir);
        var runner = new ProcessRunner();
        var options = new LibvirtOptions { BaseImageDir = _dir, GuestfsAppliancePath = Appliance };
        var overlay = Path.Combine(_dir, Stem + ".qcow2");
        await new QemuImageCli(runner, options.QemuImgBinary).CreateOverlayAsync(baseImage, overlay, Ct);

        var exe = Path.Combine(_dir, "Maldex.Agent.exe");
        var payload = RandomNumberGenerator.GetBytes(300_000);
        await File.WriteAllBytesAsync(exe, payload, Ct);

        await new GuestfishDiskEditor(runner, Options.Create(options)).InstallAgentAsync(Stem, exe, Ct);

        // Read it back out of the overlay independently of the editor's own size check.
        var readBack = Path.Combine(_dir, "readback.exe");
        var download = await runner.RunAsync("env",
            ["LIBGUESTFS_BACKEND=direct", "LIBGUESTFS_PATH=" + Appliance, "guestfish", "--ro", "--format=qcow2", "-a", overlay, "-i", "--",
                "download", GuestfishDiskEditor.GuestAgentPath, readBack], Ct, timeout: TimeSpan.FromMinutes(5));
        Assert.True(download.ExitCode == 0, download.Stderr);
        Assert.Equal(SHA256.HashData(payload), SHA256.HashData(await File.ReadAllBytesAsync(readBack, Ct)));

        File.Delete(overlay);
        var after = new FileInfo(baseImage);
        Assert.Equal((mtime, length), (after.LastWriteTimeUtc, after.Length));
    }
}
