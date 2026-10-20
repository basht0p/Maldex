using System.Runtime.Versioning;
using Maldex.Agent.Collectors;
using Maldex.Agent.Commands;
using Microsoft.Extensions.Hosting;

namespace Maldex.Agent.Windows;

/// <summary>Windows service body: connect to the virtio-serial port (retrying), run the host, repeat until shutdown.</summary>
[SupportedOSPlatform("windows")]
public sealed class AgentService(IHostApplicationLifetime lifetime) : BackgroundService
{
    public const string PortPath = @"\\.\Global\org.maldex.agent";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var time = TimeProvider.System;
        var shutdown = false;
        while (!stoppingToken.IsCancellationRequested && !shutdown)
        {
            var port = await OpenPortAsync(stoppingToken).ConfigureAwait(false);
            if (port is null) return;

            await using (port)
            {
                var dispatcher = new CommandDispatcher(new ShellSampleOpener(), new ShellUrlOpener(), () => shutdown = true);
                var source = new CompositeEventSource([
                    OperatingSystem.IsWindows() && SysmonPresent() ? new SysmonEventSource() : new EtwProcessSource(time),
                    new ScreenshotEventSource(new GdiScreenshotGrabber(), time),
                ]);
                await new AgentHost(port, source, dispatcher, time).RunAsync(stoppingToken).ConfigureAwait(false);
            }
        }

        if (shutdown) lifetime.StopApplication();
    }

    private static bool SysmonPresent() =>
        System.Diagnostics.Eventing.Reader.EventLogSession.GlobalSession.GetLogNames().Contains(SysmonEventSource.LogName);

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
