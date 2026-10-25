using System.Runtime.Versioning;
using Maldex.Infrastructure.Execution;
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

    private readonly BlankRunner _runner = new();

    /// <summary>Stands in for qemu-img: records the argv and (like the real tool) writes the output file unless told to fail.</summary>
    private sealed class BlankRunner : IProcessRunner
    {
        public readonly List<(string File, string[] Args)> Calls = [];
        public bool Fail { get; set; }
        public Task<ProcessResult> RunAsync(string file, IReadOnlyList<string> args, CancellationToken ct, string? stdin = null, TimeSpan? timeout = null)
        {
            Calls.Add((file, args.ToArray()));
            if (Fail)
            {
                File.WriteAllBytes(args[3], [0]); // a half-written output, as a failing qemu-img may leave
                return Task.FromResult(new ProcessResult(1, "", "boom"));
            }
            File.WriteAllBytes(args[3], [7, 7]);
            return Task.FromResult(new ProcessResult(0, "", ""));
        }
    }

    private LibvirtTemplateStore Store() => new(Options.Create(new LibvirtOptions { BaseImageDir = _dir }), _runner);
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
    [InlineData("abc\n")] // `$` would match before a trailing newline
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 65 chars
    public async Task InvalidStem_Throws(string stem)
    {
        var s = Store();
        Assert.Throws<ArgumentException>(() => s.Exists(stem));
        Assert.Throws<ArgumentException>(() => s.SizeOf(stem));
        await Assert.ThrowsAsync<ArgumentException>(() => s.DeleteAsync(stem, CancellationToken.None));
    }

    [Fact]
    public async Task Delete_AlsoRemovesTheTempFilesOfAnInterruptedCopy()
    {
        var s = Store();
        File.WriteAllBytes(Qcow + ".tmp", [1, 2]); // a crash mid-copy leaves these behind
        File.WriteAllBytes(Vars + ".tmp", [3]);
        await s.DeleteAsync(Stem, CancellationToken.None);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    private const string Copy = "fedcba9876543210fedcba9876543210";
    private string CopyQcow => Path.Combine(_dir, Copy + ".qcow2");
    private string CopyVars => Path.Combine(_dir, Copy + ".vars.fd");

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task WorkingCopy_CopiesBothFiles_Writable0600()
    {
        var s = Store();
        File.WriteAllBytes(Qcow, [1, 2, 3]);
        File.WriteAllBytes(Vars, [9]);
        File.SetUnixFileMode(Qcow, ReadOnly);
        File.SetUnixFileMode(Vars, ReadOnly);
        await s.CreateWorkingCopyAsync(Stem, Copy, CancellationToken.None);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(CopyQcow));
        Assert.Equal([9], File.ReadAllBytes(CopyVars));
        var rw = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        Assert.Equal(rw, File.GetUnixFileMode(CopyQcow));
        Assert.Equal(rw, File.GetUnixFileMode(CopyVars));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        Assert.Equal(ReadOnly, File.GetUnixFileMode(Qcow));
    }

    [Fact]
    public async Task WorkingCopy_RefusesExistingTarget()
    {
        var s = Store();
        File.WriteAllBytes(Qcow, [1]);
        File.WriteAllBytes(Vars, [1]);
        File.WriteAllBytes(CopyVars, [7]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.CreateWorkingCopyAsync(Stem, Copy, CancellationToken.None));
        Assert.Equal([7], File.ReadAllBytes(CopyVars));
        Assert.False(File.Exists(CopyQcow));
    }

    [Fact]
    public async Task WorkingCopy_MissingSource_LeavesNothing()
    {
        var s = Store();
        File.WriteAllBytes(Qcow, [1]);
        await Assert.ThrowsAsync<FileNotFoundException>(() => s.CreateWorkingCopyAsync(Stem, Copy, CancellationToken.None));
        Assert.False(File.Exists(CopyQcow));
        Assert.False(File.Exists(CopyVars));
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task Seal_MakesBothFilesReadOnly()
    {
        var s = Store();
        File.WriteAllBytes(Qcow, [1]);
        File.WriteAllBytes(Vars, [1]);
        await s.SealAsync(Stem, CancellationToken.None);
        Assert.Equal(ReadOnly, File.GetUnixFileMode(Qcow));
        Assert.Equal(ReadOnly, File.GetUnixFileMode(Vars));
    }

    [Fact]
    public async Task WorkingCopy_InvalidStem_Throws()
    {
        var s = Store();
        await Assert.ThrowsAsync<ArgumentException>(() => s.CreateWorkingCopyAsync("../x", Copy, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => s.CreateWorkingCopyAsync(Stem, "A/b", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => s.SealAsync("../x", CancellationToken.None));
    }

    private string VarsTemplate()
    {
        var p = Path.Combine(_dir, "template.vars.fd");
        if (File.Exists(p)) return p;
        File.WriteAllBytes(p, [4, 5, 6]);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(p, ReadOnly);
        return p;
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task CreateBlank_RunsQemuImgCreateQcow2WithTheByteSize_AndPublishesBothFiles0600()
    {
        var s = Store();
        await s.CreateBlankAsync(Copy, 64L * 1024 * 1024 * 1024, VarsTemplate(), CancellationToken.None);

        var call = Assert.Single(_runner.Calls);
        Assert.Equal("qemu-img", call.File);
        Assert.Equal(["create", "-f", "qcow2", CopyQcow + ".tmp", "68719476736"], call.Args);
        var rw = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        Assert.Equal(rw, File.GetUnixFileMode(CopyQcow));
        Assert.Equal(rw, File.GetUnixFileMode(CopyVars));
        Assert.Equal([4, 5, 6], File.ReadAllBytes(CopyVars));
        Assert.True(s.Exists(Copy));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task CreateBlank_ExistingStem_Refused_AndTouchesNothing()
    {
        var s = Store();
        File.WriteAllBytes(CopyVars, [7]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.CreateBlankAsync(Copy, 1024, VarsTemplate(), CancellationToken.None));
        Assert.Equal([7], File.ReadAllBytes(CopyVars));
        Assert.False(File.Exists(CopyQcow));
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public async Task CreateBlank_FailedQemuImg_LeavesNoFileBehind()
    {
        var s = Store();
        var template = VarsTemplate();
        _runner.Fail = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.CreateBlankAsync(Copy, 1024, template, CancellationToken.None));
        Assert.Equal([Path.GetFileName(template)], Directory.GetFiles(_dir).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public async Task CreateBlank_MissingVarsTemplate_LeavesNoFileBehind()
    {
        var s = Store();
        await Assert.ThrowsAsync<FileNotFoundException>(() => s.CreateBlankAsync(Copy, 1024, Path.Combine(_dir, "none.fd"), CancellationToken.None));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task CreateBlank_InvalidStemOrSize_Throws()
    {
        var s = Store();
        var template = VarsTemplate();
        await Assert.ThrowsAsync<ArgumentException>(() => s.CreateBlankAsync("../x", 1024, template, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => s.CreateBlankAsync(Copy, 0, template, CancellationToken.None));
    }
}
