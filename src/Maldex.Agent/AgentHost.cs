using System.Reflection;
using Maldex.Agent.Collectors;
using Maldex.Agent.Commands;
using Maldex.Core.Agent;

namespace Maldex.Agent;

/// <summary>
/// Speaks the agent protocol over <paramref name="channel"/>: Ready first, then telemetry out and commands in.
/// All writes are serialised through one semaphore. Ends on end-of-stream, a shutdown command, a fatal frame,
/// a write failure or cancellation (cancellation does not throw).
/// </summary>
public sealed class AgentHost(Stream channel, IEventSource source, CommandDispatcher dispatcher, TimeProvider time)
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public static string Version { get; } =
        typeof(AgentHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(AgentHost).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    public async Task RunAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task? forwarder = null;
        try
        {
            await SendAsync(new ReadyMessage(Version, Environment.MachineName), cts.Token).ConfigureAwait(false);
            forwarder = ForwardEventsAsync(cts);
            await ReadCommandsAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            await cts.CancelAsync().ConfigureAwait(false);
            if (forwarder is not null) await forwarder.ConfigureAwait(false);
        }
    }

    private async Task ForwardEventsAsync(CancellationTokenSource cts)
    {
        try
        {
            await foreach (var m in source.Events(cts.Token).ConfigureAwait(false))
            {
                try
                {
                    await SendAsync(m, cts.Token).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    // Oversize frame: nothing was written, so drop just this message and keep running.
                    Log($"Dropped {m.GetType().Name}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // A broken source or channel ends the run; the reader loop is unblocked via cancellation.
            Log("Event forwarding stopped: " + ex.Message);
            await cts.CancelAsync().ConfigureAwait(false);
        }
    }

    private async Task ReadCommandsAsync(CancellationToken ct)
    {
        var reader = new AgentCommandReader(channel);
        while (!ct.IsCancellationRequested)
        {
            switch (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                case CommandReadResult.Frame f:
                    var result = await dispatcher.DispatchAsync(f.Command, ct).ConfigureAwait(false);
                    await SendAsync(result, ct).ConfigureAwait(false);
                    dispatcher.AfterResultSent(f.Command);
                    if (f.Command is ShutdownCommand) return;
                    break;
                case CommandReadResult.Dropped d:
                    Log("Dropped host frame: " + d.Reason);
                    break;
                case CommandReadResult.Fatal fa:
                    Log("Fatal host frame: " + fa.Reason);
                    return;
                default: // end of stream
                    return;
            }
        }
    }

    private void Log(string text) => Console.Error.WriteLine($"{time.GetUtcNow():O} {text}");

    private async Task SendAsync(AgentMessage message, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await AgentFrameWriter.WriteAsync(channel, message, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
