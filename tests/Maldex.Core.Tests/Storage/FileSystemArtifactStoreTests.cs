using Xunit;
using System.Text;
using Maldex.Core.Abstractions;
using Maldex.Infrastructure.Storage;
using Microsoft.Extensions.Options;

namespace Maldex.Core.Tests.Storage;

[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class FileSystemArtifactStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "maldex-store-" + Guid.NewGuid().ToString("N"));
    private readonly FileSystemArtifactStore _store;

    public FileSystemArtifactStoreTests()
    {
        _store = new FileSystemArtifactStore(Options.Create(new ArtifactStoreOptions { Root = _root }));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetUnixFileMode(f, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Directory.Delete(_root, true);
        }
    }

    private static MemoryStream Bytes(string s) => new(Encoding.UTF8.GetBytes(s));

    [Fact]
    public async Task Put_ComputesAllHashes()
    {
        var blob = await _store.PutAsync("samples", Bytes("hello"), 1024, CancellationToken.None);
        Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", blob.Sha256);
        Assert.Equal("5d41402abc4b2a76b9719d911017c592", blob.Md5);
        Assert.Equal("aaf4c61ddcc5e8a2dabede0f3b482cd9aea9434d", blob.Sha1);
        Assert.Equal(5, blob.Size);
        Assert.Equal("samples/2c/f2/2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", blob.Key);
        Assert.True(_store.Exists(blob.Key));
        await using var s = await _store.OpenReadAsync(blob.Key, CancellationToken.None);
        using var r = new StreamReader(s);
        Assert.Equal("hello", await r.ReadToEndAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Put_SameContentTwice_ReturnsSameKey_SingleFile()
    {
        var a = await _store.PutAsync("samples", Bytes("dup"), 1024, CancellationToken.None);
        var b = await _store.PutAsync("samples", Bytes("dup"), 1024, CancellationToken.None);
        Assert.Equal(a.Key, b.Key);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(_root, "samples"), "*", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "tmp")));
    }

    [Fact]
    public async Task Put_ConcurrentSameContent_AllSucceed_SingleFile()
    {
        var blobs = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => _store.PutAsync("samples", Bytes("race"), 1024, CancellationToken.None))));
        Assert.Single(blobs.Select(b => b.Key).Distinct());
        Assert.Single(Directory.EnumerateFiles(Path.Combine(_root, "samples"), "*", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "tmp")));
    }

    [Fact]
    public async Task Put_FileIsReadOnly0440()
    {
        var blob = await _store.PutAsync("samples", Bytes("mode"), 1024, CancellationToken.None);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.GroupRead, File.GetUnixFileMode(_store.GetPath(blob.Key)));
    }

    [Fact]
    public async Task Put_DirectoriesHaveNoOtherBits()
    {
        var blob = await _store.PutAsync("samples", Bytes("dirs"), 1024, CancellationToken.None);
        var shard = Path.GetDirectoryName(_store.GetPath(blob.Key))!;
        var dirs = new List<string> { _root, Path.Combine(_root, "tmp") };
        for (var d = shard; d != _root; d = Path.GetDirectoryName(d)!) dirs.Add(d);
        Assert.Equal(5, dirs.Count);
        foreach (var d in dirs)
        {
            var mode = File.GetUnixFileMode(d);
            Assert.Equal(UnixFileMode.None, mode & (UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute));
        }
    }

    [Fact]
    public async Task Put_TempFileIsOwnerOnlyWhileIngesting()
    {
        using var content = new PausedStream(new byte[] { 1, 2, 3 });
        var put = Task.Run(() => _store.PutAsync("samples", content, 1024, CancellationToken.None));
        await content.Reading.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var temp = Assert.Single(Directory.EnumerateFiles(Path.Combine(_root, "tmp")));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(temp));
        content.Release.SetResult();
        await put;
    }

    private sealed class PausedStream(byte[] data) : MemoryStream(data)
    {
        public TaskCompletionSource Reading { get; } = new();
        public TaskCompletionSource Release { get; } = new();
        private int _calls;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            // Pause on the second read, after the temp file exists and has been created.
            if (++_calls == 2)
            {
                Reading.TrySetResult();
                await Release.Task;
            }
            return await base.ReadAsync(buffer, ct);
        }
    }

    [Fact]
    public async Task Put_OverMax_ThrowsAndLeavesNoTempFile()
    {
        await Assert.ThrowsAsync<BlobTooLargeException>(() =>
            _store.PutAsync("samples", Bytes("0123456789"), 5, CancellationToken.None));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "tmp")));
        var samples = Path.Combine(_root, "samples");
        Assert.True(!Directory.Exists(samples) || !Directory.EnumerateFiles(samples, "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task Put_Cancelled_LeavesNoTempFile()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _store.PutAsync("samples", Bytes("x"), 5, cts.Token));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "tmp")));
    }

    [Fact]
    public async Task Put_RejectsBadCategory()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _store.PutAsync("../x", Bytes("x"), 5, CancellationToken.None));
    }

    [Theory]
    [InlineData("samples/../../etc/passwd")]
    [InlineData("/etc/passwd")]
    [InlineData("samples/2c/f2/2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824\n")]
    [InlineData("")]
    public async Task OpenRead_RejectsTraversalKey(string key)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _store.OpenReadAsync(key, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.DeleteAsync(key, CancellationToken.None));
        Assert.Throws<ArgumentException>(() => _store.Exists(key));
    }

    [Fact]
    public async Task OpenRead_Missing_ThrowsFileNotFound()
    {
        var key = "samples/00/00/" + new string('0', 64);
        await Assert.ThrowsAsync<FileNotFoundException>(() => _store.OpenReadAsync(key, CancellationToken.None));
    }

    [Fact]
    public async Task Delete_RemovesBlob_AndMissingIsNoOp()
    {
        var blob = await _store.PutAsync("samples", Bytes("del"), 1024, CancellationToken.None);
        await _store.DeleteAsync(blob.Key, CancellationToken.None);
        Assert.False(_store.Exists(blob.Key));
        await _store.DeleteAsync(blob.Key, CancellationToken.None);
    }
}
