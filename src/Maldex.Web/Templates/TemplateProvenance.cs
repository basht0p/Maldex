using Maldex.Core.Domain;
using Maldex.Data;
using Microsoft.EntityFrameworkCore;

namespace Maldex.Web.Templates;

/// <summary>What a task or report shows about the template version a run used.</summary>
public sealed record TaskTemplateDto(
    string Id,
    string DisplayName,
    int VersionNumber,
    string Origin,
    string? WindowsEdition,
    string? WindowsBuild,
    bool? OfficePresent,
    string? AgentVersion,
    DateTimeOffset SealedAt,
    string? SealedBy,
    string Summary);

public static class TemplateProvenance
{
    /// <summary>One line, e.g. <c>win11-golden v3 (Windows 11 Pro 26300, agent 1.0.0, sealed 2026-10-24 by basht0p)</c>.</summary>
    public static string Summary(string templateId, TemplateVersion v, string? sealedBy)
    {
        var head = $"{templateId} v{v.Number}";
        if (v.Origin == TemplateVersionOrigin.Imported
            && string.IsNullOrWhiteSpace(v.WindowsEdition) && string.IsNullOrWhiteSpace(v.WindowsBuild) && string.IsNullOrWhiteSpace(v.AgentVersion))
            return $"{head} (imported; provenance not recorded)";

        var parts = new List<string>(3);
        var windows = string.Join(' ', new[] { v.WindowsEdition, v.WindowsBuild }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (windows.Length > 0) parts.Add(windows);
        if (!string.IsNullOrWhiteSpace(v.AgentVersion)) parts.Add($"agent {v.AgentVersion}");
        parts.Add($"sealed {v.SealedAt.UtcDateTime:yyyy-MM-dd}" + (string.IsNullOrWhiteSpace(sealedBy) ? "" : $" by {sealedBy}"));
        return $"{head} ({string.Join(", ", parts)})";
    }

    /// <summary>Loads the provenance of the given versions in one query (versions, their templates and the sealing users joined).</summary>
    public static async Task<Dictionary<Guid, TaskTemplateDto>> LoadAsync(MaldexDbContext db, IEnumerable<Guid?> versionIds, CancellationToken ct)
    {
        var ids = versionIds.Where(i => i is not null).Select(i => i!.Value).Distinct().ToList();
        if (ids.Count == 0) return [];

        var rows = await (
            from v in db.TemplateVersions.AsNoTracking()
            join t in db.Templates.AsNoTracking() on v.TemplateId equals t.Id
            join u in db.Users.AsNoTracking() on v.SealedByUserId equals u.Id into users
            from u in users.DefaultIfEmpty()
            where ids.Contains(v.Id)
            select new { Version = v, t.DisplayName, SealedBy = u == null ? null : u.DisplayName }).ToListAsync(ct);

        return rows.ToDictionary(r => r.Version.Id, r => new TaskTemplateDto(
            r.Version.TemplateId, r.DisplayName, r.Version.Number, r.Version.Origin.ToString(), r.Version.WindowsEdition,
            r.Version.WindowsBuild, r.Version.OfficePresent, r.Version.AgentVersion, r.Version.SealedAt, r.SealedBy,
            Summary(r.Version.TemplateId, r.Version, r.SealedBy)));
    }

    public static async Task<TaskTemplateDto?> LoadAsync(MaldexDbContext db, Guid? versionId, CancellationToken ct) =>
        versionId is { } id && (await LoadAsync(db, [id], ct)).TryGetValue(id, out var dto) ? dto : null;
}
