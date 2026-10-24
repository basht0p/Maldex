using System.Runtime.Versioning;
using Maldex.Core.Abstractions;
using Maldex.Infrastructure.Execution;
using Maldex.Infrastructure.Libvirt;
using Microsoft.Extensions.Options;
using Xunit;

namespace Maldex.Core.Tests.Libvirt;

[SupportedOSPlatform("linux")]
public sealed class GuestfishDiskEditorTests : IDisposable
{
    private const string Stem = "0123456789abcdef0123456789abcdef";
    private const string GuestPath = "/Program Files/Maldex/Maldex.Agent.exe";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Recorder : IProcessRunner
    {
        public readonly List<(string File, string[] Args, TimeSpan? Timeout)> Calls = [];
        public Func<string, string[], ProcessResult> Handler { get; set; } = (_, _) => new(0, "", "");

        public Task<ProcessResult> RunAsync(string file, IReadOnlyList<string> args, CancellationToken ct, string? stdin = null, TimeSpan? timeout = null)
        {
            ct.ThrowIfCancellationRequested();
            var a = args.ToArray();
            Calls.Add((file, a, timeout));
            return Task.FromResult(Handler(file, a));
        }
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mx-gf-" + Guid.NewGuid().ToString("N"));
    private readonly Recorder _runner = new();
    private readonly string _exe;

    public GuestfishDiskEditorTests()
    {
        Directory.CreateDirectory(_dir);
        _exe = Path.Combine(_dir, "agent source", "Maldex.Agent.exe"); // a space: argv must carry it intact
        Directory.CreateDirectory(Path.GetDirectoryName(_exe)!);
        File.WriteAllBytes(_exe, new byte[1234]);
        _runner.Handler = (_, _) => new(0, "1234\n", "");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string Qcow => Path.Combine(_dir, Stem + ".qcow2");

    private GuestfishDiskEditor Editor(string appliance = "/var/lib/maldex/guestfs-appliance") =>
        new(_runner, Options.Create(new LibvirtOptions { BaseImageDir = _dir, GuestfsAppliancePath = appliance }));

    private void WorkingCopy(UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite)
    {
        File.WriteAllBytes(Qcow, [1]);
        File.SetUnixFileMode(Qcow, mode);
    }

    [Fact]
    public async Task Install_RunsGuestfish_WithAnArgvArrayAndTheApplianceEnvironment_AndChecksTheWrittenSize()
    {
        WorkingCopy();

        await Editor("/opt/appliance").InstallAgentAsync(Stem, _exe, Ct);

        var call = Assert.Single(_runner.Calls);
        Assert.Equal("env", call.File); // the environment travels as argv: no shell, no process-wide state
        Assert.Equal(
        [
            "LIBGUESTFS_BACKEND=direct", "LIBGUESTFS_PATH=/opt/appliance",
            "guestfish", "--format=qcow2", "-a", Qcow, "-i", "--",
            "upload", _exe, GuestPath, ":", "filesize", GuestPath,
        ], call.Args);
        Assert.NotNull(call.Timeout); // ProcessRunner's default would kill a real run after 60 s
    }

    [Fact]
    public async Task Install_SizeMismatch_Fails()
    {
        WorkingCopy();
        _runner.Handler = (_, _) => new(0, "1000\n", "");

        var ex = await Assert.ThrowsAsync<TemplateAgentInstallException>(() => Editor().InstallAgentAsync(Stem, _exe, Ct));

        Assert.Contains("1000", ex.Message);
        Assert.Contains("1234", ex.Message);
    }

    [Fact]
    public async Task Install_UnreadableSizeAnswer_Fails()
    {
        WorkingCopy();
        _runner.Handler = (_, _) => new(0, "", "");

        await Assert.ThrowsAsync<TemplateAgentInstallException>(() => Editor().InstallAgentAsync(Stem, _exe, Ct));
    }

    [Fact]
    public async Task Install_GuestfishFailure_FailsWithASafeCleanedMessage()
    {
        WorkingCopy();
        _runner.Handler = (_, _) => new(1, "", "libguestfs: error: mount: \u001b[31mfailed\u001b[0m\n" + new string('x', 5000));

        var ex = await Assert.ThrowsAsync<TemplateAgentInstallException>(() => Editor().InstallAgentAsync(Stem, _exe, Ct));

        Assert.StartsWith("writing the agent into the working copy failed", ex.Message);
        Assert.Contains("mount", ex.Message);
        Assert.DoesNotContain('\u001b', ex.Message);
        Assert.DoesNotContain('\n', ex.Message);
        Assert.True(ex.Message.Length < 400);
    }

    [Fact]
    public async Task Install_Timeout_FailsAsAnInstallError()
    {
        WorkingCopy();
        _runner.Handler = (_, _) => throw new TimeoutException("'env' did not exit");

        var ex = await Assert.ThrowsAsync<TemplateAgentInstallException>(() => Editor().InstallAgentAsync(Stem, _exe, Ct));

        Assert.Contains("did not finish", ex.Message);
    }

    [Fact]
    public async Task Install_CannotStartGuestfish_FailsAsAnInstallError()
    {
        WorkingCopy();
        _runner.Handler = (_, _) => throw new System.ComponentModel.Win32Exception("No such file or directory");

        var ex = await Assert.ThrowsAsync<TemplateAgentInstallException>(() => Editor().InstallAgentAsync(Stem, _exe, Ct));

        Assert.Contains("./build.sh real-setup", ex.Message);
    }

    [Fact]
    public async Task Install_Cancelled_Propagates()
    {
        WorkingCopy();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Editor().InstallAgentAsync(Stem, _exe, cts.Token));
    }

    [Fact]
    public async Task Install_SealedVersionDisk_IsRefused_AndGuestfishNeverRuns()
    {
        WorkingCopy(UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        var ex = await Assert.ThrowsAsync<TemplateAgentInstallException>(() => Editor().InstallAgentAsync(Stem, _exe, Ct));

        Assert.Contains("sealed", ex.Message);
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public async Task Install_MissingDisk_IsRefused()
    {
        var ex = await Assert.ThrowsAsync<TemplateAgentInstallException>(() => Editor().InstallAgentAsync(Stem, _exe, Ct));

        Assert.Contains("missing", ex.Message);
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public async Task Install_MissingAgentExe_IsRefused()
    {
        WorkingCopy();

        var ex = await Assert.ThrowsAsync<TemplateAgentInstallException>(() => Editor().InstallAgentAsync(Stem, Path.Combine(_dir, "nope.exe"), Ct));

        Assert.Contains("guest agent", ex.Message);
        Assert.Empty(_runner.Calls);
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData("UPPER")]
    [InlineData("")]
    [InlineData("stem\n")]
    public async Task Install_InvalidStem_IsRejected_AndNothingRuns(string stem)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Editor().InstallAgentAsync(stem, _exe, Ct));

        Assert.Empty(_runner.Calls);
    }
}
