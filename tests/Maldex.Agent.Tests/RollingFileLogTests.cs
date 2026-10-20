using Maldex.Agent;
using Xunit;

namespace Maldex.Agent.Tests;

public class RollingFileLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mx-log-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Appends_BelowTheCap_WithoutRolling()
    {
        var log = new RollingFileLog(Path.Combine(_dir, "agent.log"), 1000);
        log.Write("one"); log.Write("two");
        Assert.Equal(["one", "two"], File.ReadAllLines(log.Path));
        Assert.False(File.Exists(log.Path + ".1"));
    }

    [Fact]
    public void Rolls_WhenTheCapWouldBeExceeded_KeepingOneOldFile()
    {
        var log = new RollingFileLog(Path.Combine(_dir, "agent.log"), 30);
        log.Write(new string('a', 20));
        log.Write(new string('b', 20)); // would exceed 30 -> roll
        Assert.Equal([new string('a', 20)], File.ReadAllLines(log.Path + ".1"));
        Assert.Equal([new string('b', 20)], File.ReadAllLines(log.Path));
        log.Write(new string('c', 20)); // roll again: old file replaced, not accumulated
        Assert.Equal([new string('b', 20)], File.ReadAllLines(log.Path + ".1"));
        Assert.Equal([new string('c', 20)], File.ReadAllLines(log.Path));
        Assert.Equal(2, Directory.GetFiles(_dir).Length);
    }

    [Fact]
    public void WriteFailure_DoesNotThrow()
    {
        Directory.CreateDirectory(_dir);
        var log = new RollingFileLog(_dir, 100); // path is a directory
        log.Write("x");
    }
}
