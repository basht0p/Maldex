using System.Runtime.Versioning;
using Maldex.Core.Domain;
using Maldex.Infrastructure.Execution;
using Maldex.Infrastructure.Media;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Maldex.Integration.Tests.Media;

/// <summary>
/// Real loop mount of a tiny ISO built in a temp directory (wimcapture + xorriso); never touches real media. Needs MALDEX_REAL_VM=1,
/// passwordless sudo for mount/umount, wiminfo, wimcapture and xorriso.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class RealMediaInspectorTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "maldex-realmedia-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static async Task Run(IProcessRunner runner, string file, params string[] args)
    {
        var r = await runner.RunAsync(file, args, Ct, timeout: TimeSpan.FromMinutes(2));
        Assert.True(r.ExitCode == 0, $"{file} failed: {r.Stderr}");
    }

    [Fact]
    public async Task Inspect_AJunkFile_ReportsTheMountError_AndLeavesNothingBehind()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MALDEX_REAL_VM") == "1",
            "Real media inspector test skipped: set MALDEX_REAL_VM=1 (needs sudo mount/umount).");
        var runner = new ProcessRunner();
        var root = Path.Combine(_dir, "media");
        Directory.CreateDirectory(root);
        var junk = Path.Combine(root, "junk.iso");
        await File.WriteAllBytesAsync(junk, new byte[64 * 1024], Ct);

        var inspector = new LoopMountMediaInspector(runner, Options.Create(new MediaOptions { Root = root }), NullLogger<LoopMountMediaInspector>.Instance);
        var result = await inspector.InspectAsync(junk, MediaKind.OsInstaller, Ct);

        Assert.Equal("the file could not be mounted read-only as an ISO (udf/iso9660)", result.Error);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(root, "mnt")));
        var findmnt = await runner.RunAsync("findmnt", ["-rn", "-o", "TARGET"], Ct);
        Assert.DoesNotContain(root, findmnt.Stdout);
    }

    [Fact]
    public async Task Inspect_ATinyInstallerIso_FindsOneImage_AndLeavesNothingMounted()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MALDEX_REAL_VM") == "1",
            "Real media inspector test skipped: set MALDEX_REAL_VM=1 (needs sudo mount/umount, wiminfo, wimcapture, xorriso).");
        var runner = new ProcessRunner();

        var content = Path.Combine(_dir, "content");
        var tree = Path.Combine(_dir, "tree");
        Directory.CreateDirectory(content);
        Directory.CreateDirectory(Path.Combine(tree, "sources"));
        File.WriteAllText(Path.Combine(content, "hello.txt"), "hello");
        await Run(runner, "wimcapture", content, Path.Combine(tree, "sources", "install.wim"), "Windows 11 Pro", "--no-acls",
            "--image-property", "WINDOWS/ARCH=9"); // real Windows images carry an architecture; the parser requires it
        var root = Path.Combine(_dir, "media");
        Directory.CreateDirectory(root);
        var iso = Path.Combine(root, "tiny.iso");
        await Run(runner, "xorriso", "-as", "mkisofs", "-iso-level", "4", "-J", "-o", iso, tree);

        var inspector = new LoopMountMediaInspector(runner, Options.Create(new MediaOptions { Root = root }), NullLogger<LoopMountMediaInspector>.Instance);
        var result = await inspector.InspectAsync(iso, MediaKind.OsInstaller, Ct);

        Assert.Null(result.Error);
        var image = Assert.Single(result.Contents!.Images);
        Assert.Equal("Windows 11 Pro", image.Name);

        var findmnt = await runner.RunAsync("findmnt", ["-rn", "-o", "TARGET"], Ct);
        Assert.DoesNotContain(root, findmnt.Stdout);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(root, "mnt")));
    }
}
