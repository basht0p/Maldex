using Maldex.Infrastructure.Execution;
using Xunit;

namespace Maldex.Core.Tests.Execution;

public class ProcessRunnerTests
{
    [Fact]
    public async Task ProcessRunner_Timeout_KillsAndThrows()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() =>
            new ProcessRunner().RunAsync("sleep", ["5"], CancellationToken.None, timeout: TimeSpan.FromMilliseconds(200)));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task ProcessRunner_PassesArgsWithoutShell()
    {
        var r = await new ProcessRunner().RunAsync("printf", ["%s", "a b;echo pwned"], CancellationToken.None);
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("a b;echo pwned", r.Stdout);
    }

    [Fact]
    public async Task ProcessRunner_LargeOutput_DoesNotDeadlock()
    {
        var r = await new ProcessRunner().RunAsync("sh", ["-c", "head -c 5000000 /dev/zero | tr '\\0' x; head -c 5000000 /dev/zero | tr '\\0' y >&2"], CancellationToken.None);
        Assert.Equal(5_000_000, r.Stdout.Length);
        Assert.Equal(5_000_000, r.Stderr.Length);
    }

    [Fact]
    public async Task ProcessRunner_CallerCancellation_ThrowsOperationCanceled_NotTimeout()
    {
        using var cts = new CancellationTokenSource(200);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ProcessRunner().RunAsync("sleep", ["5"], cts.Token));
    }

    [Fact]
    public async Task ProcessRunner_GrandchildHoldingPipe_StillTimesOut()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() =>
            new ProcessRunner().RunAsync("sh", ["-c", "sleep 5 & exit 0"], CancellationToken.None, timeout: TimeSpan.FromMilliseconds(300)));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task ProcessRunner_CapturesStderrExitCodeAndStdin()
    {
        var r = await new ProcessRunner().RunAsync("sh", ["-c", "cat; echo err >&2; exit 3"], CancellationToken.None, stdin: "in");
        Assert.Equal(3, r.ExitCode);
        Assert.Equal("in", r.Stdout);
        Assert.Equal("err\n", r.Stderr);
    }
}
