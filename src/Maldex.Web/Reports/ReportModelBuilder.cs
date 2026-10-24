using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Maldex.Data;
using Maldex.Web.Templates;
using Microsoft.EntityFrameworkCore;

namespace Maldex.Web.Reports;

public sealed record ReportTimelineRow(DateTimeOffset At, EventType Type, int Pid, string Summary);

public sealed record ReportProcessRow(long Id, int Depth, int Pid, string ImagePath, string CommandLine, string? User, DateTimeOffset StartedAt, DateTimeOffset? EndedAt);

public sealed record ReportDetectionRow(DetectionSource Source, string Rule, int Severity, string Tags, string Target, string? Details);

public sealed record ReportScreenshot(Guid ArtifactId, DateTimeOffset At, string DataUri);

/// <summary>Everything the report shows. Strings may come from the guest: the Razor renderer encodes every one of them.</summary>
public sealed record ReportModel(
    Guid TaskId,
    string OrgName,
    string? SampleName,
    string? Url,
    string? Sha256,
    string? Sha1,
    string? Md5,
    long? SampleSize,
    string Image,
    string? TemplateSummary,
    NetworkMode NetworkMode,
    string? ExitCountry,
    int DurationSec,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    Verdict Verdict,
    IReadOnlyList<string> ProcessingWarnings,
    int TotalEvents,
    IReadOnlyList<ReportTimelineRow> Timeline,
    IReadOnlyList<ReportProcessRow> Processes,
    IReadOnlyList<ReportTimelineRow> NetworkEvents,
    IReadOnlyList<ReportTimelineRow> DnsEvents,
    IReadOnlyList<ReportDetectionRow> Detections,
    IReadOnlyList<ReportScreenshot> Screenshots,
    DateTimeOffset GeneratedAt)
{
    /// <summary>The analyst-facing verdict label (R6).</summary>
    public string VerdictLabel => LabelOf(Verdict);

    public static string LabelOf(Verdict verdict) => verdict switch
    {
        Verdict.NoThreats => "No threats detected",
        Verdict.Suspicious => "Suspicious",
        Verdict.Malicious => "Malicious",
        _ => "Unknown",
    };
}

public sealed class ReportNotFoundException(Guid taskId) : Exception($"Task {taskId} does not exist.");

public sealed class ReportModelBuilder(MaldexDbContext db, IArtifactStore store, TimeProvider time)
{
    public const int MaxTimelineEvents = 2000;
    public const int MaxScreenshots = 12;
    private const int MaxNetworkRows = 2000;

    public async Task<ReportModel> BuildAsync(Guid taskId, CancellationToken ct)
    {
        var task = await db.Tasks.AsNoTracking().Include(t => t.Org).Include(t => t.Sample)
            .FirstOrDefaultAsync(t => t.Id == taskId, ct)
            ?? throw new ReportNotFoundException(taskId);

        static ReportTimelineRow Row(BehaviorEvent e) => new(e.At, e.Type, e.Pid, e.Summary);

        var totalEvents = await db.BehaviorEvents.AsNoTracking().CountAsync(e => e.TaskId == taskId, ct);
        var timeline = (await db.BehaviorEvents.AsNoTracking().Where(e => e.TaskId == taskId)
            .OrderBy(e => e.Id).Take(MaxTimelineEvents).ToListAsync(ct)).Select(Row).ToList();
        var network = (await db.BehaviorEvents.AsNoTracking().Where(e => e.TaskId == taskId && e.Type == EventType.Network)
            .OrderBy(e => e.Id).Take(MaxNetworkRows).ToListAsync(ct)).Select(Row).ToList();
        var dns = (await db.BehaviorEvents.AsNoTracking().Where(e => e.TaskId == taskId && e.Type == EventType.Dns)
            .OrderBy(e => e.Id).Take(MaxNetworkRows).ToListAsync(ct)).Select(Row).ToList();

        var nodes = await db.ProcessNodes.AsNoTracking().Where(n => n.TaskId == taskId).OrderBy(n => n.Id).ToListAsync(ct);
        var detections = (await db.Detections.AsNoTracking().Where(d => d.TaskId == taskId)
            .OrderByDescending(d => d.Severity).ThenBy(d => d.Id).ToListAsync(ct))
            .Select(d => new ReportDetectionRow(d.Source, d.Rule, d.Severity, d.Tags, d.Target, d.Details)).ToList();

        return new ReportModel(
            task.Id, task.Org?.Name ?? "", task.OriginalFileName, task.Url, task.SampleSha256, task.Sample?.Sha1, task.Sample?.Md5, task.Sample?.Size,
            task.Image, await TemplateSummaryAsync(task, ct), task.NetworkMode, task.ExitCountry, task.DurationSec, task.CreatedAt, task.StartedAt, task.CompletedAt,
            task.Verdict, task.ProcessingWarnings.ToList(), totalEvents, timeline, FlattenTree(nodes), network, dns, detections,
            await LoadScreenshotsAsync(taskId, ct), time.GetUtcNow());
    }

    /// <summary>The provenance line for a task bound to a template version; null for tasks that predate templates.</summary>
    private async Task<string?> TemplateSummaryAsync(AnalysisTask task, CancellationToken ct) =>
        (await TemplateProvenance.LoadAsync(db, task.TemplateVersionId, ct))?.Summary;

    /// <summary>Depth-first order, roots first. A node whose parent is unknown is a root; a cycle can never loop (each node is emitted once).</summary>
    internal static List<ReportProcessRow> FlattenTree(List<ProcessNode> nodes)
    {
        var ids = nodes.Select(n => n.Id).ToHashSet();
        var children = nodes.Where(n => n.ParentNodeId is { } p && ids.Contains(p))
            .GroupBy(n => n.ParentNodeId!.Value).ToDictionary(g => g.Key, g => g.OrderBy(n => n.StartedAt).ThenBy(n => n.Id).ToList());
        var result = new List<ReportProcessRow>(nodes.Count);
        var seen = new HashSet<long>();

        void Visit(ProcessNode n, int depth)
        {
            if (!seen.Add(n.Id)) return;
            result.Add(new ReportProcessRow(n.Id, depth, n.Pid, n.ImagePath, n.CommandLine, n.User, n.StartedAt, n.EndedAt));
            if (children.TryGetValue(n.Id, out var kids)) foreach (var k in kids) Visit(k, depth + 1);
        }

        foreach (var root in nodes.Where(n => n.ParentNodeId is not { } p || !ids.Contains(p)).OrderBy(n => n.StartedAt).ThenBy(n => n.Id)) Visit(root, 0);
        foreach (var leftover in nodes) Visit(leftover, 0); // only reachable through a parent cycle
        return result;
    }

    private async Task<List<ReportScreenshot>> LoadScreenshotsAsync(Guid taskId, CancellationToken ct)
    {
        var shots = await db.Artifacts.AsNoTracking()
            .Where(a => a.TaskId == taskId && a.Kind == ArtifactKind.Screenshot)
            .OrderBy(a => a.CreatedAt).ThenBy(a => a.Id).ToListAsync(ct);

        var result = new List<ReportScreenshot>(Math.Min(shots.Count, MaxScreenshots));
        foreach (var a in Sample(shots, MaxScreenshots))
        {
            if (!store.Exists(a.BlobKey)) continue; // purged by retention or lost: the report simply omits it
            await using var stream = await store.OpenReadAsync(a.BlobKey, ct);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            var bytes = buffer.ToArray();
            result.Add(new ReportScreenshot(a.Id, a.CreatedAt, $"data:{MimeOf(bytes)};base64,{Convert.ToBase64String(bytes)}"));
        }
        return result;
    }

    /// <summary>Picks <paramref name="max"/> items spread evenly over the list (first and last included); all of them when there are fewer.</summary>
    internal static List<T> Sample<T>(IReadOnlyList<T> items, int max)
    {
        if (items.Count <= max) return [.. items];
        var picked = new List<T>(max);
        for (var i = 0; i < max; i++) picked.Add(items[(int)((long)i * (items.Count - 1) / (max - 1))]);
        return picked;
    }

    private static string MimeOf(byte[] b) => b is [0xFF, 0xD8, ..] ? "image/jpeg" : "image/png";
}
