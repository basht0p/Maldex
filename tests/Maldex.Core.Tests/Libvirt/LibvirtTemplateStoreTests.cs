using System.Runtime.Versioning;
using Maldex.Infrastructure.Libvirt;
using Microsoft.Extensions.Options;
using Xunit;

namespace Maldex.Core.Tests.Libvirt;

public sealed class LibvirtTemplateStoreTests : IDisposable
{
    private const string Stem = "0123456789abcdef0123456789abcdef";
    private const UnixFileMode ReadOnly = UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mx-ts-" + Guid.NewGuid().ToString("N"));

    public LibvirtTemplateStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private LibvirtTemplateStore Store() => new(Options.Create(new LibvirtOptions { BaseImageDir = _dir }));
    private string Qcow => Path.Combine(_dir, Stem + ".qcow2");
    private string Vars => Path.Combine(_dir, Stem + ".vars.fd");

    [Fact]
    public void Exists_RequiresBothFiles()
    {
        var s = Store();
        Assert.False(s.Exists(Stem));
        File.WriteAllBytes(Qcow, [1, 2, 3]);
        Assert.False(s.Exists(Stem));
        File.WriteAllBytes(Vars, [1]);
        Assert.True(s.Exists(Stem));
        File.Delete(Qcow);
        Assert.False(s.Exists(Stem));
    }

    [Fact]
    public void SizeOf_ReturnsQcowSize()
    {
        var s = Store();
        Assert.Null(s.SizeOf(Stem));
        File.WriteAllBytes(Qcow, new byte[1234]);
        Assert.Equal(1234, s.SizeOf(Stem));
    }

    [Fact]
    public async Task Delete_RemovesBoth_AndIsIdempotent()
    {
        var s = Store();
        File.WriteAllBytes(Qcow, [1]);
        File.WriteAllBytes(Vars, [1]);
        await s.DeleteAsync(Stem, CancellationToken.None);
        Assert.False(File.Exists(Qcow));
        Assert.False(File.Exists(Vars));
        await s.DeleteAsync(Stem, CancellationToken.None);
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task Delete_ReadOnlyFiles_StillDeleted()
    {
        var s = Store();
        File.WriteAllBytes(Qcow, [1]);
        File.WriteAllBytes(Vars, [1]);
        File.SetUnixFileMode(Qcow, ReadOnly);
        File.SetUnixFileMode(Vars, ReadOnly);
        await s.DeleteAsync(Stem, CancellationToken.None);
        Assert.False(File.Exists(Qcow));
        Assert.False(File.Exists(Vars));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData("UPPER")]
    [InlineData("")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 65 chars
    public async Task InvalidStem_Throws(string stem)
    {
        var s = Store();
        Assert.Throws<ArgumentException>(() => s.Exists(stem));
        Assert.Throws<ArgumentException>(() => s.SizeOf(stem));
        await Assert.ThrowsAsync<ArgumentException>(() => s.DeleteAsync(stem, CancellationToken.None));
    }
}
