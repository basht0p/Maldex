using Maldex.Core.Domain;
using Maldex.Data;
using Maldex.Web.Endpoints;
using Maldex.Web.Templates;
using Microsoft.EntityFrameworkCore;

namespace Maldex.Web.Reports;

/// <summary>Builds the JSON report the UI renders. Each list is one AsNoTracking query, capped at <see cref="Cap"/> rows.</summary>
public sealed class TaskReportBuilder(MaldexDbContext db)
{
    public const int Cap = 2000;
    private const string DnsSeparator = " → ";

    /// <summary>Null when the task is unknown.</summary>
    public async Task<TaskReportDto?> BuildAsync(Guid taskId, CancellationToken ct)
    {
        var task = await db.Tasks.AsNoTracking().Include(t => t.Org).Include(t => t.Sample).Include(t => t.SubmittedBy)
            .FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null) return null;

        var template = await TemplateProvenance.LoadAsync(db, task.TemplateVersionId, ct);

        var history = (await db.TaskStatusHistory.AsNoTracking().Where(h => h.TaskId == taskId).OrderBy(h => h.At).ThenBy(h => h.Id).ToListAsync(ct))
            .Select(h => new StatusStepDto(h.From, h.To, h.At, h.Reason)).ToList();

        // Rule order is ordinal, which SQL collation would not give; sort in memory.
        var detections = (await db.Detections.AsNoTracking().Where(d => d.TaskId == taskId).ToListAsync(ct))
            .OrderByDescending(d => d.Severity).ThenBy(d => d.Rule, StringComparer.Ordinal).ThenBy(d => d.Id)
            .Select(d => new DetectionDto(d.Source, d.Rule, d.Severity,
                d.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), d.Target, d.Details)).ToList();

        var nodes = await db.ProcessNodes.AsNoTracking().Where(n => n.TaskId == taskId).OrderBy(n => n.Id).Take(Cap + 1).ToListAsync(ct);
        var processesTruncated = nodes.Count > Cap;
        if (processesTruncated) nodes = nodes.Take(Cap).ToList();
        var parentOf = nodes.ToDictionary(n => n.Id, n => n.ParentNodeId);
        var processes = ReportModelBuilder.FlattenTree(nodes)
            .Select(r => new ReportProcessDto(r.Id, parentOf[r.Id] is { } p && parentOf.ContainsKey(p) ? p : null, r.Pid, r.ImagePath, r.CommandLine, r.User, r.StartedAt, r.EndedAt, r.Depth))
            .ToList();

        var flows = await db.NetworkFlows.AsNoTracking().Where(f => f.TaskId == taskId).OrderBy(f => f.StartedAt).ThenBy(f => f.Id).Take(Cap + 1).ToListAsync(ct);
        var flowsTruncated = flows.Count > Cap;

        var dnsEvents = await db.BehaviorEvents.AsNoTracking().Where(e => e.TaskId == taskId && e.Type == EventType.Dns)
            .OrderBy(e => e.At).ThenBy(e => e.Id).Take(Cap + 1).ToListAsync(ct);
        var dnsTruncated = dnsEvents.Count > Cap;

        var http = await db.HttpTransactions.AsNoTracking().Where(h => h.TaskId == taskId).OrderBy(h => h.Id).Take(Cap + 1).ToListAsync(ct);
        var httpTruncated = http.Count > Cap;

        var shots = (await db.Artifacts.AsNoTracking().Where(a => a.TaskId == taskId && a.Kind == ArtifactKind.Screenshot)
            .OrderBy(a => a.CreatedAt).ThenBy(a => a.Id).ToListAsync(ct)).Select(a => new ScreenshotRefDto(a.Id, a.CreatedAt)).ToList();
        var dropped = (await db.Artifacts.AsNoTracking().Where(a => a.TaskId == taskId && a.Kind == ArtifactKind.Dropped)
            .OrderBy(a => a.CreatedAt).ThenBy(a => a.Id).ToListAsync(ct))
            .Select(a => new DroppedFileDto(a.Id, a.GuestPath, a.Sha256, a.Size, a.CreatedAt)).ToList();

        var same = new SamePreviousDto(0, null);
        if (task.SampleSha256 is { } sha)
        {
            var others = db.Tasks.AsNoTracking().Where(t => t.SampleSha256 == sha && t.Id != taskId);
            var count = await others.CountAsync(ct);
            if (count > 0)
            {
                var latest = await others.OrderByDescending(t => t.CreatedAt).Select(t => new SamePreviousLatestDto(t.Id, t.Verdict, t.CreatedAt)).FirstAsync(ct);
                same = new SamePreviousDto(count, latest);
            }
        }

        return new TaskReportDto(
            TaskDto.From(task, task.Org?.Name ?? "", 0, template),
            task.SubmittedBy?.DisplayName, task.Sample?.Sha1, task.Sample?.Md5, task.Sample?.Size, task.Sample?.FileType, task.Sample?.Mime,
            template?.Summary,
            history, detections, processes,
            flows.Take(Cap).Select(f => new NetworkFlowDto(f.Proto, f.Src, f.Dst, f.DstPort, f.Bytes, f.StartedAt)).ToList(),
            dnsEvents.Take(Cap).Select(DnsRow).ToList(),
            http.Take(Cap).Select(h => new HttpRowDto(h.Id, h.Method, h.Url, h.Host, h.Status, h.ReqHeaders, h.RespHeaders, h.BodyBlobKey is not null)).ToList(),
            dropped, shots, same,
            new TruncatedDto(processesTruncated, flowsTruncated, dnsTruncated, httpTruncated));
    }

    /// <summary>EventIngestor writes <c>query → a1, a2</c>, or just the query when there are no answers.</summary>
    private static DnsRowDto DnsRow(BehaviorEvent e)
    {
        var i = e.Summary.IndexOf(DnsSeparator, StringComparison.Ordinal);
        return i < 0
            ? new DnsRowDto(e.At, e.Pid, e.Summary, [])
            : new DnsRowDto(e.At, e.Pid, e.Summary[..i], e.Summary[(i + DnsSeparator.Length)..].Split(", ", StringSplitOptions.RemoveEmptyEntries));
    }
}
