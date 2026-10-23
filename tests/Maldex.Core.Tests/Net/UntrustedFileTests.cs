using System.Diagnostics;
using Maldex.Infrastructure.Net;
using Xunit;

namespace Maldex.Core.Tests.Net;

public sealed class UntrustedFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mx-uf-" + Guid.NewGuid().ToString("N"));

    public UntrustedFileTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static CancellationToken Ten() => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;
    private string P(string n) => Path.Combine(_dir, n);

    [Fact]
    public async Task RegularFile_IsCopied()
    {
        File.WriteAllText(P("src"), "hello\nworld\n");
        var r = await UntrustedFile.CopyRegularFileAsync(P("src"), P("dst"), 1024, Ten());
        Assert.Equal(new UntrustedCopyResult(12, false), r);
        Assert.Equal("hello\nworld\n", File.ReadAllText(P("dst")));
    }

    [Fact]
    public async Task Symlink_IsRefused_AndNothingIsWritten()
    {
        File.WriteAllText(P("secret"), "TOP SECRET");
        File.CreateSymbolicLink(P("src"), P("secret"));
        await Assert.ThrowsAsync<IOException>(() => UntrustedFile.CopyRegularFileAsync(P("src"), P("dst"), 1024, Ten()));
        Assert.False(File.Exists(P("dst")));
    }

    [Fact]
    public async Task Fifo_IsRefused_WithoutBlocking()
    {
        using (var p = Process.Start("mkfifo", P("src"))!) await p.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(File.Exists(P("src")));
        var t = UntrustedFile.CopyRegularFileAsync(P("src"), P("dst"), 1024, Ten());
        await Assert.ThrowsAsync<IOException>(() => t); // would hang forever on a blocking open/read
        Assert.False(File.Exists(P("dst")));
    }

    [Fact]
    public async Task DeviceFile_IsRefused()
    {
        await Assert.ThrowsAsync<IOException>(() => UntrustedFile.CopyRegularFileAsync("/dev/zero", P("dst"), 1024, Ten()));
    }

    [Fact]
    public async Task HardLinkedFile_IsRefused()
    {
        File.WriteAllText(P("orig"), "x");
        using (var p = Process.Start("ln", [P("orig"), P("src")])!) await p.WaitForExitAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<IOException>(() => UntrustedFile.CopyRegularFileAsync(P("src"), P("dst"), 1024, Ten()));
    }

    [Fact]
    public async Task OversizeFile_IsTruncatedAtCap_AndFlagged()
    {
        await File.WriteAllBytesAsync(P("src"), new byte[300_000], TestContext.Current.CancellationToken);
        var r = await UntrustedFile.CopyRegularFileAsync(P("src"), P("dst"), 100_000, Ten());
        Assert.Equal(new UntrustedCopyResult(100_000, true), r);
        Assert.Equal(100_000, new FileInfo(P("dst")).Length);
    }

    [Fact]
    public async Task ExactlyCapSized_IsNotFlagged()
    {
        await File.WriteAllBytesAsync(P("src"), new byte[5000], TestContext.Current.CancellationToken);
        Assert.Equal(new UntrustedCopyResult(5000, false), await UntrustedFile.CopyRegularFileAsync(P("src"), P("dst"), 5000, Ten()));
    }

    [Fact]
    public async Task Cancelled_Throws()
    {
        File.WriteAllText(P("src"), "x");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UntrustedFile.CopyRegularFileAsync(P("src"), P("dst"), 10, cts.Token));
    }

    [Fact]
    public async Task Missing_Throws()
    {
        // a distinct type so callers can treat "nothing there" as "nothing to keep"
        await Assert.ThrowsAsync<FileNotFoundException>(() => UntrustedFile.CopyRegularFileAsync(P("nope"), P("dst"), 10, Ten()));
        Assert.False(File.Exists(P("dst")));
    }
}
