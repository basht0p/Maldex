using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Maldex.Core.Agent;

namespace Maldex.Agent.Collectors;

/// <summary>
/// Best-effort fallback process telemetry for guests without Sysmon: diffs the process table every 500 ms.
/// Short-lived processes between polls are missed; command line, parent PID and exit code are not available
/// this way (empty / 0). Windows only.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProcessPollingSource(TimeProvider time) : IEventSource
{
    public async IAsyncEnumerable<AgentMessage> Events([EnumeratorCancellation] CancellationToken ct)
    {
        var self = Environment.ProcessId;
        var known = new Dictionary<int, string>();
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500), time);
        var first = true;
        do
        {
            var current = new Dictionary<int, string>();
            var started = new List<AgentMessage>();
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    if (p.Id == self) continue;
                    var path = SafePath(p);
                    current[p.Id] = path;
                    if (!first && !known.ContainsKey(p.Id))
                        started.Add(new ProcessStartedMessage(time.GetUtcNow(), p.Id, 0, path, ""));
                }
            }

            foreach (var m in started) yield return m;
            foreach (var pid in known.Keys.Where(k => !current.ContainsKey(k)))
                yield return new ProcessExitedMessage(time.GetUtcNow(), pid, 0);

            known = current;
            first = false;
        }
        while (await WaitAsync(timer, ct).ConfigureAwait(false));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }
    }

    private static string SafePath(Process p)
    {
        try { return p.MainModule?.FileName ?? p.ProcessName; }
        catch { return p.ProcessName; }
    }
}
