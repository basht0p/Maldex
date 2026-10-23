using Xunit;

namespace Maldex.Integration.Tests.E2E;

/// <summary>Always-on tests of the E2E cleanup orchestration with fakes (no VM needed).</summary>
public sealed class E2ECleanupTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(300);
    private static readonly LeftOver Dirty = new(["maldex-x"], ["mxb1"], ["mxn1"]);

    private sealed class Calls
    {
        public bool Disposed, Found, Removed;
        public LeftOver? RemovedWhat;
    }

    private static E2ECleanup Make(Calls c, Func<CancellationToken, Task> stop, Func<CancellationToken, Task<LeftOver>> find, Func<LeftOver, CancellationToken, Task>? remove = null) =>
        new(stop, () => c.Disposed = true,
            async ct => { c.Found = true; return await find(ct); },
            remove ?? ((l, _) => { c.Removed = true; c.RemovedWhat = l; return Task.CompletedTask; }),
            Short, Short, Short, TimeSpan.FromMilliseconds(20), 100);

    [Fact]
    public async Task StopNeverTerminal_StillDisposesChecksAndForceRemoves()
    {
        var c = new Calls();
        var cleanup = Make(c, ct => Task.Delay(Timeout.Infinite, ct), _ => Task.FromResult(Dirty));
        var started = DateTime.UtcNow;
        var msg = await cleanup.RunAsync();
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
        Assert.True(c.Disposed);
        Assert.True(c.Found);
        Assert.True(c.Removed);
        Assert.Same(Dirty, c.RemovedWhat);
        Assert.Contains("did not reach a terminal state", msg);
        Assert.Contains("left behind", msg);
    }

    [Fact]
    public async Task CheckWaitTimesOut_ForceRemovalUsesWhatWasFound()
    {
        var c = new Calls();
        var msg = await Make(c, _ => Task.CompletedTask, _ => Task.FromResult(Dirty)).RunAsync(); // never clean: the wait times out
        Assert.True(c.Removed);
        Assert.Same(Dirty, c.RemovedWhat);
        Assert.Contains("leak check timed out", msg);
    }

    [Fact]
    public async Task FindThrows_ReportsCouldNotRun_AndStillDisposed()
    {
        var c = new Calls();
        var msg = await Make(c, _ => Task.CompletedTask, _ => throw new InvalidOperationException("'virsh' is not installed")).RunAsync();
        Assert.True(c.Disposed);
        Assert.False(c.Removed);
        Assert.Contains("leak check could not run: 'virsh' is not installed", msg);
    }

    [Fact]
    public async Task Clean_ReturnsNull_AndRemovesNothing()
    {
        var c = new Calls();
        Assert.Null(await Make(c, _ => Task.CompletedTask, _ => Task.FromResult(LeftOver.None)).RunAsync());
        Assert.True(c.Disposed);
        Assert.False(c.Removed);
    }

    [Fact]
    public async Task DisposeThrows_DoesNotSkipLeakCheck()
    {
        var c = new Calls();
        var cleanup = new E2ECleanup(_ => Task.CompletedTask, () => throw new IOException("boom"),
            async ct => { c.Found = true; return Dirty; }, (l, _) => { c.Removed = true; return Task.CompletedTask; },
            Short, Short, Short, TimeSpan.FromMilliseconds(20), 2);
        var msg = await cleanup.RunAsync();
        Assert.True(c.Found);
        Assert.True(c.Removed);
        Assert.Contains("disposing the host failed", msg);
    }

    [Theory]
    [InlineData("1234", true)]
    [InlineData("2", true)]
    [InlineData("1", false)]
    [InlineData("0", false)]
    [InlineData("-5", false)]
    [InlineData("12 34", false)]
    [InlineData("abc", false)]
    [InlineData("", false)]
    public void OnlyRealPidsAreKillable(string pid, bool expected) => Assert.Equal(expected, RealVmE2ETests.IsKillablePid(pid));
}
