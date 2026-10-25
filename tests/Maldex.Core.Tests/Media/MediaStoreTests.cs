using System.Security.Cryptography;
using Maldex.Core.Abstractions;
using Maldex.Infrastructure.Media;
using Microsoft.Extensions.Options;
using Xunit;

namespace Maldex.Core.Tests.Media;

[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class MediaStoreTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "maldex-media-" + Guid.NewGuid().ToString("N"));
    private readonly MediaStore _store;

    public MediaStoreTests()
    {
        _store = new MediaStore(Options.Create(new MediaOptions { Root = _root }), new FixedDisk(1234));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private sealed class FixedDisk(long mib) : IDiskSpaceProbe
    {
        public string? Path { get; private set; }
        public long FreeMiB(string path) { Path = path; return mib; }
    }

    private static string Sha(string s) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s)));

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("../../etc/passwd")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF")]   // upper case
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\n")]  // trailing newline
    public void FinalPath_RefusesAnythingButLowercaseSha256(string sha) =>
        Assert.Throws<ArgumentException>(() => _store.FinalPath(sha));

    [Fact]
    public void Paths_follow_the_layout()
    {
        var sha = Sha("x");
        var id = Guid.NewGuid();
        Assert.Equal(Path.Combine(_root, sha + ".iso"), _store.FinalPath(sha));
        Assert.Equal(Path.Combine(_root, "staging", id.ToString("N") + ".part"), _store.StagingPath(id));
    }

    [Fact]
    public void OpenStagingForAppend_CreatesOwnerOnlyAndTruncatesToKeepBytes()
    {
        var id = Guid.NewGuid();
        using (var s = _store.OpenStagingForAppend(id, 0)) s.Write("0123456789"u8);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_store.StagingPath(id)));

        using (var s = _store.OpenStagingForAppend(id, 4))
        {
            Assert.Equal(4, s.Length);
            Assert.Equal(4, s.Position);
            s.Write("XY"u8);
        }
        Assert.Equal("0123XY", File.ReadAllText(_store.StagingPath(id)));
    }

    [Fact]
    public void OpenStagingForAppend_RefusesToKeepMoreThanItHas()
    {
        var id = Guid.NewGuid();
        using (var s = _store.OpenStagingForAppend(id, 0)) s.Write("abc"u8);
        Assert.Throws<InvalidOperationException>(() => _store.OpenStagingForAppend(id, 10));
    }

    [Fact]
    public async Task RehashStagingAsync_HashesOnlyTheFirstLengthBytes_AndCanContinue()
    {
        var id = Guid.NewGuid();
        using (var s = _store.OpenStagingForAppend(id, 0)) s.Write("hello world, extra"u8);
        using var hash = await _store.RehashStagingAsync(id, 11, Ct);
        hash.AppendData(", extra"u8);
        Assert.Equal(Sha("hello world, extra"), Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    [Fact]
    public async Task RehashStagingAsync_FailsWhenTheFileIsShorter()
    {
        var id = Guid.NewGuid();
        using (var s = _store.OpenStagingForAppend(id, 0)) s.Write("abc"u8);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.RehashStagingAsync(id, 99, Ct));
    }

    [Fact]
    public void Finalize_MovesTheFileAndMakesItReadOnly()
    {
        var id = Guid.NewGuid();
        var sha = Sha("iso");
        using (var s = _store.OpenStagingForAppend(id, 0)) s.Write("iso"u8);

        var path = _store.Finalize(id, sha);

        Assert.Equal(_store.FinalPath(sha), path);
        Assert.Equal("iso", File.ReadAllText(path));
        Assert.False(File.Exists(_store.StagingPath(id)));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead, File.GetUnixFileMode(path));
    }

    [Fact]
    public void Finalize_OntoAnExistingFile_KeepsTheExistingOneAndDropsStaging()
    {
        var sha = Sha("same");
        var first = Guid.NewGuid();
        using (var s = _store.OpenStagingForAppend(first, 0)) s.Write("same"u8);
        var existing = _store.Finalize(first, sha);
        var inode = File.GetLastWriteTimeUtc(existing);

        var second = Guid.NewGuid();
        using (var s = _store.OpenStagingForAppend(second, 0)) s.Write("same"u8);
        var path = _store.Finalize(second, sha);

        Assert.Equal(existing, path);
        Assert.False(File.Exists(_store.StagingPath(second)));
        Assert.Equal(inode, File.GetLastWriteTimeUtc(existing));
    }

    [Fact]
    public void Finalize_RefusesAnInvalidSha_AndLeavesStagingAlone()
    {
        var id = Guid.NewGuid();
        using (var s = _store.OpenStagingForAppend(id, 0)) s.Write("x"u8);
        Assert.Throws<ArgumentException>(() => _store.Finalize(id, "../evil"));
        Assert.True(File.Exists(_store.StagingPath(id)));
    }

    [Fact]
    public void Delete_RemovesStagingAndReadOnlyFinalFiles_AndIgnoresMissingOnes()
    {
        var id = Guid.NewGuid();
        var sha = Sha("d");
        using (var s = _store.OpenStagingForAppend(id, 0)) s.Write("d"u8);
        _store.Finalize(id, sha);
        _store.DeleteFinal(sha);
        Assert.False(File.Exists(_store.FinalPath(sha)));
        _store.DeleteFinal(sha);

        var other = Guid.NewGuid();
        using (var s = _store.OpenStagingForAppend(other, 0)) s.Write("d"u8);
        _store.DeleteStaging(other);
        Assert.False(File.Exists(_store.StagingPath(other)));
        _store.DeleteStaging(other);
    }

    [Fact]
    public void StagingIds_ListsOnlyPartFilesNamedByAGuid()
    {
        Assert.Empty(_store.StagingIds());
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        using (_store.OpenStagingForAppend(a, 0)) { }
        using (_store.OpenStagingForAppend(b, 0)) { }
        File.WriteAllText(Path.Combine(_root, "staging", "notes.part"), "x");
        File.WriteAllText(Path.Combine(_root, "staging", a.ToString("N") + ".tmp"), "x");

        Assert.Equal(new[] { a, b }.Order(), _store.StagingIds().Order());
    }

    [Fact]
    public void FreeMiB_AsksTheProbeAboutTheMediaRoot()
    {
        var disk = new FixedDisk(777);
        var store = new MediaStore(Options.Create(new MediaOptions { Root = _root }), disk);
        Assert.Equal(777, store.FreeMiB());
        Assert.Equal(_root, disk.Path);
    }
}
