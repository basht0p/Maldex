using System.Collections.Concurrent;
using Maldex.Core.Agent;
using Maldex.Core.Domain;

namespace Maldex.Infrastructure.Fakes;

/// <summary>A file the fake guest can hand back for <c>collect_file</c>. The claims default to the truth; tests lie through them.</summary>
public sealed record FakeGuestFile(byte[] Data, string? ClaimedSha256 = null, long? ClaimedTotalBytes = null);

/// <summary>
/// What the replayed "guest" has on disk and how it behaves during bulk transfers, shared by every fake channel.
/// Empty by default: sample chunks are accepted and every <c>collect_file</c> is answered "file not found".
/// </summary>
public sealed class FakeGuest
{
    private readonly ConcurrentDictionary<string, FakeGuestFile> _files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When set, the chunk with this index is answered with a failure (the agent "refusing" the delivery).</summary>
    public int? RejectSampleChunkIndex { get; set; }

    /// <summary>When true, the open_sample command is never answered (a guest that hangs while opening).</summary>
    public bool SilentOpen { get; set; }

    /// <summary>When true, the open_sample command is refused (a sample the guest cannot open).</summary>
    public bool RejectOpen { get; set; }

    /// <summary>When set, the chunk with this index is never answered (a guest that hangs mid-delivery).</summary>
    public int? SilentSampleChunkIndex { get; set; }

    private readonly ConcurrentDictionary<string, byte> _reachable = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, string> _echoByTask = new();

    /// <summary>Self-test probe names the fake guest reports as reachable (empty = everything blocked, as a correctly isolated guest would).</summary>
    public ICollection<string> ReachableProbes => _reachable.Keys;

    /// <summary>When set, the self-test echo body for every run (wins over the per-run value). Null = the echo fails.</summary>
    public string? SelfTestEchoBody { get; set; }

    /// <summary>The fake run's lease address (matches <see cref="FakeRunNetworkInfo"/>).</summary>
    public const string LeaseAddress = "10.213.0.2";

    /// <summary>What a fake Simulated run's echo request gets back: INetSim's default page, as the real gateway serves it.</summary>
    public const string InetSimEchoPage = "<html>\n  <head>\n    <title>INetSim default HTML page</title>\n  </head>\n  <body>\n    <p>This is the default HTML page for INetSim HTTP server fake mode.</p>\n  </body>\n</html>\n";

    /// <summary>The addresses the fake guest reports in its self-test (default: it holds its lease address).</summary>
    public IReadOnlyList<string> SelfTestGuestAddresses { get; set; } = [LeaseAddress];

    /// <summary>When true, the fake guest never answers a self-test.</summary>
    public bool SilentSelfTest { get; set; }

    /// <summary>What the fake guest answers to <c>provenance</c>.</summary>
    public string ProvenanceEdition { get; set; } = "Windows 11 Pro";
    public string ProvenanceBuild { get; set; } = "26300.1";
    public string ProvenanceAgentVersion { get; set; } = "0.1.0-fake+000000000000";
    public bool? ProvenanceOfficePresent { get; set; } = false;
    public bool? ProvenanceRealTimeProtection { get; set; } = false;

    /// <summary>When true, the fake guest never answers <c>provenance</c>.</summary>
    public bool SilentProvenance { get; set; }

    private readonly ConcurrentQueue<string> _applied = new();

    /// <summary>Transfer ids (agent, Sysmon config, CA) of every accepted <c>apply_components</c>, in the order the command named them.</summary>
    public IReadOnlyList<string> AppliedComponentIds => _applied.ToArray();

    /// <summary>When true, <c>apply_components</c> is refused (a guest whose install step fails).</summary>
    public bool RejectApplyComponents { get; set; }

    /// <summary>
    /// Raised with the run's task id when a channel handles <c>power_off</c>: the VM has shut itself down. The fake hypervisor
    /// listens so that it stops listing the VM, as libvirt would.
    /// </summary>
    public event Action<Guid>? PoweredOff;

    internal void RecordApplied(ApplyComponentsCommand c)
    {
        _applied.Enqueue(c.AgentTransferId);
        _applied.Enqueue(c.SysmonConfigTransferId);
        _applied.Enqueue(c.CaCertTransferId);
    }

    public void NotifyPoweredOff(Guid taskId) => PoweredOff?.Invoke(taskId);

    public void SetReachable(string probe) => _reachable[probe] = 0;

    private readonly ConcurrentDictionary<Guid, NetworkMode> _modeByTask = new();

    internal void SetModeFor(Guid taskId, NetworkMode? mode)
    {
        if (mode is null) _modeByTask.TryRemove(taskId, out _);
        else _modeByTask[taskId] = mode.Value;
    }

    internal NetworkMode? ModeFor(Guid taskId) => _modeByTask.TryGetValue(taskId, out var m) ? m : null;

    internal void SetEchoFor(Guid taskId, string? body)
    {
        if (body is null) _echoByTask.TryRemove(taskId, out _);
        else _echoByTask[taskId] = body;
    }

    internal string? EchoFor(Guid taskId) => SelfTestEchoBody ?? (_echoByTask.TryGetValue(taskId, out var b) ? b : null);

    public void AddFile(string path, byte[] data) => _files[path] = new FakeGuestFile(data);

    public void AddFile(string path, FakeGuestFile file) => _files[path] = file;

    public bool TryGetFile(string path, out FakeGuestFile file) => _files.TryGetValue(path, out file!);
}
