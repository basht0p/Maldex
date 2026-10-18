using System.Text.Json;
using Maldex.Core.Abstractions;
using Maldex.Core.Agent;
using Maldex.Core.Domain;
using Maldex.Core.Live;
using Maldex.Core.Text;
using Maldex.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maldex.Orchestrator;

/// <summary>
/// Persists one run's guest telemetry and publishes it live. One instance per run; <see cref="Add"/> and
/// <see cref="FlushAsync"/> may be called from different threads but flushes must not overlap.
/// Every guest string is sanitized and truncated with <see cref="GuestText.Clean"/> before it is stored.
/// </summary>
public sealed class EventIngestor(
    Guid taskId,
    MaldexDbContext db,
    IArtifactStore artifacts,
    ITaskEventPublisher publisher,
    TimeProvider time,
    ILogger? logger = null)
{
    private const long MaxScreenshotBytes = 16 * 1024 * 1024;
    // ProcessNode.User is nvarchar(256) and Integrity nvarchar(64); Clean appends a 12-char suffix when it cuts.
    private const int UserMax = 256 - 12;
    private const int IntegrityMax = 64 - 12;

    private readonly ILogger _logger = logger ?? NullLogger.Instance;
    private readonly object _gate = new();
    private List<AgentMessage> _pending = [];

    /// <summary>The latest node per pid (open or exited), used for parent links and event attribution.</summary>
    private readonly Dictionary<int, ProcessNode> _latestByPid = [];

    public void Add(AgentMessage message)
    {
        lock (_gate) _pending.Add(message);
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        List<AgentMessage> batch;
        lock (_gate)
        {
            if (_pending.Count == 0) return;
            batch = _pending;
            _pending = [];
        }

        var changedNodes = new List<ProcessNode>();
        var newEvents = new List<BehaviorEvent>();
        var newArtifacts = new List<Artifact>();

        foreach (var raw in batch)
        {
            var message = Sanitize(raw);
            switch (message)
            {
                case ProcessStartedMessage p:
                    await OnProcessStartedAsync(p, changedNodes, newEvents, ct).ConfigureAwait(false);
                    break;
                case ProcessExitedMessage x:
                    if (_latestByPid.TryGetValue(x.Pid, out var node) && node.EndedAt is null)
                    {
                        node.EndedAt = x.At;
                        node.ExitCode = x.ExitCode;
                        if (!changedNodes.Contains(node)) changedNodes.Add(node);
                    }
                    break;
                case FileEventMessage f:
                    await AddEventAsync(EventType.File, f.At, f.Pid, $"{f.Operation} {f.Path}", message, newEvents, ct).ConfigureAwait(false);
                    break;
                case RegistryEventMessage r:
                    await AddEventAsync(EventType.Registry, r.At, r.Pid,
                        r.ValueName is null ? $"{r.Operation} {r.Key}" : $"{r.Operation} {r.Key}\\{r.ValueName}",
                        message, newEvents, ct).ConfigureAwait(false);
                    break;
                case NetworkEventMessage n:
                    await AddEventAsync(EventType.Network, n.At, n.Pid, $"{n.Protocol} {n.RemoteAddress}:{n.RemotePort}", message, newEvents, ct).ConfigureAwait(false);
                    break;
                case DnsEventMessage d:
                    await AddEventAsync(EventType.Dns, d.At, d.Pid,
                        d.Answers is { Length: > 0 } ? $"{d.Query} → {string.Join(", ", d.Answers)}" : d.Query,
                        message, newEvents, ct).ConfigureAwait(false);
                    break;
                case ScreenshotMessage s:
                    var artifact = await StoreScreenshotAsync(s, ct).ConfigureAwait(false);
                    if (artifact is not null) newArtifacts.Add(artifact);
                    break;
                // Ready and command results are control-plane messages, not telemetry.
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var processDtos = changedNodes.Select(ToDto).ToList();
        var eventDtos = newEvents.Select(e => new LiveEventDto(e.Id, e.Type, e.At, e.Pid, e.ProcessNodeId, e.Summary, e.Data)).ToList();

        // Rows are saved; keep only the process nodes tracked (they are updated on exit).
        foreach (var e in newEvents) db.Entry(e).State = EntityState.Detached;
        foreach (var a in newArtifacts) db.Entry(a).State = EntityState.Detached;

        await PublishAsync(processDtos, eventDtos, ct).ConfigureAwait(false);
    }

    private async Task OnProcessStartedAsync(ProcessStartedMessage p, List<ProcessNode> changedNodes, List<BehaviorEvent> newEvents, CancellationToken ct)
    {
        long? parentId = null;
        if (_latestByPid.TryGetValue(p.ParentPid, out var parent))
        {
            if (parent.Id == 0) await db.SaveChangesAsync(ct).ConfigureAwait(false);
            parentId = parent.Id;
        }

        var node = new ProcessNode
        {
            TaskId = taskId,
            Pid = p.Pid,
            ParentPid = p.ParentPid,
            ParentNodeId = parentId,
            ImagePath = p.ImagePath,
            CommandLine = p.CommandLine,
            User = p.User,
            Integrity = p.Integrity,
            StartedAt = p.At,
        };
        db.ProcessNodes.Add(node);
        _latestByPid[p.Pid] = node;
        changedNodes.Add(node);
        await AddEventAsync(EventType.Process, p.At, p.Pid, $"{FileName(p.ImagePath)} (pid {p.Pid})", p, newEvents, ct).ConfigureAwait(false);
    }

    private async Task AddEventAsync(EventType type, DateTimeOffset at, int pid, string summary, AgentMessage message, List<BehaviorEvent> newEvents, CancellationToken ct)
    {
        long? nodeId = null;
        if (_latestByPid.TryGetValue(pid, out var node))
        {
            if (node.Id == 0) await db.SaveChangesAsync(ct).ConfigureAwait(false);
            nodeId = node.Id;
        }

        var row = new BehaviorEvent
        {
            TaskId = taskId,
            ProcessNodeId = nodeId,
            Pid = pid,
            Type = type,
            At = at,
            Summary = GuestText.Clean(summary),
            Data = JsonSerializer.Serialize(message, AgentJson.Options),
        };
        db.BehaviorEvents.Add(row);
        newEvents.Add(row);
    }

    private async Task<Artifact?> StoreScreenshotAsync(ScreenshotMessage s, CancellationToken ct)
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(s.ImageBase64); }
        catch (FormatException)
        {
            _logger.LogWarning("Dropping screenshot for task {TaskId}: invalid base64.", taskId);
            return null;
        }

        await using var content = new MemoryStream(bytes, writable: false);
        var blob = await artifacts.PutAsync("screenshots", content, MaxScreenshotBytes, ct).ConfigureAwait(false);
        var artifact = new Artifact
        {
            Id = Guid.NewGuid(),
            TaskId = taskId,
            Kind = ArtifactKind.Screenshot,
            BlobKey = blob.Key,
            Sha256 = blob.Sha256,
            Size = blob.Size,
            CreatedAt = time.GetUtcNow(),
        };
        db.Artifacts.Add(artifact);
        return artifact;
    }

    private async Task PublishAsync(List<ProcessNodeDto> processes, List<LiveEventDto> events, CancellationToken ct)
    {
        // The data is already persisted; a failed live push must not fail the run (clients re-sync from REST).
        try
        {
            foreach (var p in processes) await publisher.PublishProcessAsync(taskId, p, ct).ConfigureAwait(false);
            if (events.Count > 0) await publisher.PublishEventsAsync(taskId, events, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Live publish failed for task {TaskId}.", taskId);
        }
    }

    private static ProcessNodeDto ToDto(ProcessNode n) =>
        new(n.Id, n.Pid, n.ParentNodeId, n.ImagePath, n.CommandLine, n.User, n.StartedAt, n.EndedAt);

    private static string FileName(string path)
    {
        var i = path.LastIndexOfAny(['\\', '/']);
        return i < 0 ? path : path[(i + 1)..];
    }

    /// <summary>Returns a copy of the message with every guest string passed through <see cref="GuestText.Clean"/>.</summary>
    private static AgentMessage Sanitize(AgentMessage m) => m switch
    {
        ProcessStartedMessage x => x with
        {
            ImagePath = GuestText.Clean(x.ImagePath),
            CommandLine = GuestText.Clean(x.CommandLine),
            User = x.User is null ? null : GuestText.Clean(x.User, UserMax),
            Integrity = x.Integrity is null ? null : GuestText.Clean(x.Integrity, IntegrityMax),
        },
        FileEventMessage x => x with
        {
            Operation = GuestText.Clean(x.Operation),
            Path = GuestText.Clean(x.Path),
            NewPath = x.NewPath is null ? null : GuestText.Clean(x.NewPath),
        },
        RegistryEventMessage x => x with
        {
            Operation = GuestText.Clean(x.Operation),
            Key = GuestText.Clean(x.Key),
            ValueName = x.ValueName is null ? null : GuestText.Clean(x.ValueName),
            Data = x.Data is null ? null : GuestText.Clean(x.Data),
        },
        NetworkEventMessage x => x with
        {
            Protocol = GuestText.Clean(x.Protocol),
            RemoteAddress = GuestText.Clean(x.RemoteAddress),
            LocalAddress = x.LocalAddress is null ? null : GuestText.Clean(x.LocalAddress),
        },
        DnsEventMessage x => x with
        {
            Query = GuestText.Clean(x.Query),
            QueryType = x.QueryType is null ? null : GuestText.Clean(x.QueryType),
            Answers = x.Answers?.Select(a => GuestText.Clean(a)).ToArray(),
        },
        _ => m,
    };
}
