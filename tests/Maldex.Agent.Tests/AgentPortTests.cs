using System.Diagnostics;
using Maldex.Agent;
using Xunit;

namespace Maldex.Agent.Tests;

public sealed class AgentPortTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("maldex-port-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>
    /// The agent always has a read pending (waiting for host commands) while it streams events out. A buffered FileStream
    /// serialises async reads and writes, so every event write waited for the next command: runs saw no telemetry after the
    /// sample was opened. A FIFO opened read-write stands in for the virtio-serial port.
    /// </summary>
    [Fact]
    public async Task OpenDuplex_WriteCompletesWhileAReadIsPending()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses a POSIX FIFO as the stand-in port.");
        var fifo = Path.Combine(_dir, "port");
        using (var mk = Process.Start("mkfifo", fifo)) await mk.WaitForExitAsync(TestContext.Current.CancellationToken);

        var port = AgentPort.OpenDuplex(fifo);
        var read = port.ReadAsync(new byte[16], TestContext.Current.CancellationToken).AsTask(); // nothing to read yet: stays pending
        try
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
            Assert.False(read.IsCompleted);

            var write = port.WriteAsync("ping"u8.ToArray(), TestContext.Current.CancellationToken).AsTask();
            var done = await Task.WhenAny(write, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.Same(write, done); // not stuck behind the pending read
            await write;
            Assert.Equal(4, await read.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)); // and the read sees it
        }
        finally
        {
            // On failure the read is still pending (and holds the stream): feed it from a second handle so the stream can close.
            if (!read.IsCompleted)
            {
                await using (var feeder = new FileStream(fifo, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, bufferSize: 0))
                    feeder.Write("unblock"u8);
                await read.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None); // cleanup: runs even after cancellation
            }
            await port.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }
    }
}
