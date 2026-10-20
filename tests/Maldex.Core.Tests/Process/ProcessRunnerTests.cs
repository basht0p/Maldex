using Maldex.Infrastructure.Process;
using Xunit;

namespace Maldex.Core.Tests.Process;

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
    public async Task ProcessRunner_CapturesStderrExitCodeAndStdin()
    {
        var r = await new ProcessRunner().RunAsync("sh", ["-c", "cat; echo err >&2; exit 3"], CancellationToken.None, stdin: "in");
        Assert.Equal(3, r.ExitCode);
        Assert.Equal("in", r.Stdout);
        Assert.Equal("err\n", r.Stderr);
    }
}
