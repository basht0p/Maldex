using System.Runtime.Versioning;
using Maldex.Agent.Collectors;
using Maldex.Agent.Commands;

namespace Maldex.Agent.Windows;

/// <summary>
/// Runs in the interactive session of the autologon user (started by a logon scheduled task, not as a service):
/// connect to the virtio-serial port, run the host, reconnect with backoff (1 s doubling to 10 s) until shutdown.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AgentRunner
{
    public const string PortPath = @"\\.\Global\org.maldex.agent";
    public const string MutexName = @"Global\MaldexAgent";

    public static async Task<int> RunAsync(CancellationToken ct)
    {
        using var mutex = new Mutex(true, MutexName, out var created);
        if (!created) return 0; // another instance already owns this machine

        AgentLog.Write($"Agent {AgentHost.Version} starting");
        GdiScreenshotGrabber.EnableDpiAwareness();
        var time = TimeProvider.System;
        var shutdown = false;
        var backoff = TimeSpan.FromSeconds(1);

        while (!ct.IsCancellationRequested && !shutdown)
        {
            var port = await OpenPortAsync(ct).ConfigureAwait(false);
            if (port is null) break;

            AgentLog.Write("Channel connected");
            var started = time.GetTimestamp();
            await using (port)
            {
                using var dispatcher = new CommandDispatcher(new ShellSampleOpener(), new ShellUrlOpener(), () => shutdown = true);
                var source = new CompositeEventSource([
                    SysmonPresent() ? new SysmonEventSource() : new ProcessPollingSource(time),
                    new ScreenshotEventSource(new GdiScreenshotGrabber(), time),
                ]);
                await new AgentHost(port, source, dispatcher, time).RunAsync(ct).ConfigureAwait(false);
            }

            if (shutdown) break;
            // A session that lasted a while resets the backoff; an immediate EOF must not hot-loop.
            backoff = time.GetElapsedTime(started) > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(1) : Next(backoff);
            try { await Task.Delay(backoff, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
        return 0;
    }

    public static TimeSpan Next(TimeSpan current) =>
        TimeSpan.FromSeconds(Math.Min(10, current.TotalSeconds * 2));

    private static bool SysmonPresent()
    {
        try { return System.Diagnostics.Eventing.Reader.EventLogSession.GlobalSession.GetLogNames().Contains(SysmonEventSource.LogName); }
        catch { return false; }
    }

    private static async Task<FileStream?> OpenPortAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                return new FileStream(PortPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return null; }
            }
        }
        return null;
    }
}
