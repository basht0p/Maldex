using System.Globalization;
using System.Text;
using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Maldex.Core.Scheduling;
using Maldex.Data;
using Maldex.Data.Templates;
using Maldex.Web.Audit;
using Maldex.Web.Auth;
using Maldex.Web.Templates;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Maldex.Web.Endpoints;

public static class TaskEndpoints
{
    private const int MaxFieldBytes = 4096;
    private const int MaxFileNameLength = 260;
    private const int MaxUrlLength = 2048;
    private const int MaxSearchLength = 256;
    private const long MultipartOverheadBytes = 64 * 1024;

    public static IEndpointRouteBuilder MapTaskEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/tasks").RequireAuthorization(AuthSetup.AnalystPolicy);

        g.MapPost("", SubmitAsync);

        g.MapGet("", async (Guid? orgId, string? status, string? q, string? verdict, bool? mine, string? from, string? to, string? sort, string? dir,
            int? skip, int? take, MaldexDbContext db, CurrentUser user, CancellationToken ct) =>
        {
            var errors = new Dictionary<string, string[]>();
            RunStatus? statusFilter = null;
            if (!string.IsNullOrWhiteSpace(status))
            {
                if (TryParseEnum<RunStatus>(status, out var s)) statusFilter = s;
                else errors["status"] = ["Unknown status."];
            }
            Verdict? verdictFilter = null;
            if (!string.IsNullOrWhiteSpace(verdict))
            {
                if (TryParseEnum<Verdict>(verdict, out var v)) verdictFilter = v;
                else errors["verdict"] = ["Unknown verdict."];
            }
            var text = q?.Trim();
            if (text is not null && (text.Length is 0 or > MaxSearchLength))
                errors["q"] = [$"q must be 1 to {MaxSearchLength} characters."];
            DateTimeOffset? fromAt = null, toBefore = null;
            if (!string.IsNullOrWhiteSpace(from))
            {
                if (DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                    fromAt = new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
                else errors["from"] = ["from must be yyyy-MM-dd."];
            }
            if (!string.IsNullOrWhiteSpace(to))
            {
                if (DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                    toBefore = new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddDays(1);
                else errors["to"] = ["to must be yyyy-MM-dd."];
            }
            var sortKey = string.IsNullOrWhiteSpace(sort) ? "created" : sort.Trim().ToLowerInvariant();
            if (sortKey is not ("created" or "verdict" or "duration")) errors["sort"] = ["sort must be created, verdict or duration."];
            var dirKey = string.IsNullOrWhiteSpace(dir) ? "desc" : dir.Trim().ToLowerInvariant();
            if (dirKey is not ("asc" or "desc")) errors["dir"] = ["dir must be asc or desc."];
            if (skip is < 0) errors["skip"] = ["skip must be 0 or more."];
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var takeN = Math.Clamp(take ?? 50, 1, 100);
            var desc = dirKey == "desc";

            var query = db.Tasks.AsNoTracking().AsQueryable();
            if (orgId is not null) query = query.Where(t => t.OrgId == orgId);
            if (statusFilter is not null) query = query.Where(t => t.Status == statusFilter);
            if (verdictFilter is not null) query = query.Where(t => t.Verdict == verdictFilter);
            if (mine == true) { var me = user.UserId; query = query.Where(t => t.SubmittedByUserId == me); }
            if (fromAt is { } f) query = query.Where(t => t.CreatedAt >= f);
            if (toBefore is { } tb) query = query.Where(t => t.CreatedAt < tb);
            if (text is not null)
            {
                var lower = text.ToLowerInvariant();
                if (lower.Length == 64 && IsHex(lower)) query = query.Where(t => t.SampleSha256 == lower);
                else if (lower.Length == 40 && IsHex(lower)) query = query.Where(t => t.Sample!.Sha1 == lower);
                else if (lower.Length == 32 && IsHex(lower)) query = query.Where(t => t.Sample!.Md5 == lower);
                else query = query.Where(t => (t.OriginalFileName != null && t.OriginalFileName.Contains(text)) || (t.Url != null && t.Url.Contains(text)));
            }

            var total = await query.CountAsync(ct);
            IOrderedQueryable<AnalysisTask> ordered = sortKey switch
            {
                // Verdicts are stored as text, so rank them explicitly by enum value instead of sorting the names.
                "verdict" => (desc ? query.OrderByDescending(VerdictRank) : query.OrderBy(VerdictRank))
                    .ThenByDescending(t => t.CreatedAt),
                // Rows without both ends sort last in both directions.
                "duration" => desc
                    ? query.OrderBy(t => EF.Functions.DateDiffSecond(t.StartedAt, t.EndedAt ?? t.CompletedAt) == null ? 1 : 0)
                        .ThenByDescending(t => EF.Functions.DateDiffSecond(t.StartedAt, t.EndedAt ?? t.CompletedAt))
                    : query.OrderBy(t => EF.Functions.DateDiffSecond(t.StartedAt, t.EndedAt ?? t.CompletedAt) == null ? 1 : 0)
                        .ThenBy(t => EF.Functions.DateDiffSecond(t.StartedAt, t.EndedAt ?? t.CompletedAt)),
                _ => desc ? query.OrderByDescending(t => t.CreatedAt) : query.OrderBy(t => t.CreatedAt),
            };
            var page = await ordered.ThenByDescending(t => t.Id).Include(t => t.Org).Include(t => t.SubmittedBy)
                .Skip(skip ?? 0).Take(takeN).ToListAsync(ct);
            var queued = await QueuedAsync(db, ct);
            var templates = await TemplateProvenance.LoadAsync(db, page.Select(t => t.TemplateVersionId), ct); // one query for the whole page
            return Results.Ok(new TaskListDto(
                page.Select(t => TaskDto.From(t, t.Org!.Name, PositionOf(t, queued),
                    t.TemplateVersionId is { } v && templates.TryGetValue(v, out var tpl) ? tpl : null)).ToList(), total));
        });

        g.MapGet("{id:guid}", async (Guid id, MaldexDbContext db, CancellationToken ct) =>
        {
            var t = await db.Tasks.AsNoTracking().Include(t => t.Org).FirstOrDefaultAsync(t => t.Id == id, ct);
            if (t is null) return Results.NotFound();
            var position = t.Status == RunStatus.Queued ? PositionOf(t, await QueuedAsync(db, ct)) : 0;
            return Results.Ok(TaskDto.From(t, t.Org!.Name, position, await TemplateProvenance.LoadAsync(db, t.TemplateVersionId, ct)));
        });

        g.MapPost("{id:guid}/cancel", async (Guid id, MaldexDbContext db, AuditWriter audit, TimeProvider time, CancellationToken ct) =>
        {
            var t = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id, ct);
            if (t is null) return Results.NotFound();
            if (t.Status != RunStatus.Queued) return NotCancellable();
            t.TransitionTo(RunStatus.Cancelled, "cancelled by analyst", time.GetUtcNow());
            try
            {
                await audit.WriteAsync("task.cancel", t.Id.ToString(), null, ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                return NotCancellable();
            }
            return Results.NoContent();
        });

        return app;
    }

    private static IResult NotCancellable() =>
        Results.Problem(title: "Only queued tasks can be cancelled.", statusCode: StatusCodes.Status409Conflict);

    private static Task<List<QueuedTaskInfo>> QueuedAsync(MaldexDbContext db, CancellationToken ct) =>
        db.Tasks.AsNoTracking().Where(t => t.Status == RunStatus.Queued)
            .Select(t => new QueuedTaskInfo(t.Id, t.Interactive, t.CreatedAt)).ToListAsync(ct);

    private static int PositionOf(AnalysisTask t, List<QueuedTaskInfo> queued) =>
        t.Status == RunStatus.Queued ? TaskPrioritizer.PositionOf(t.Id, queued) : 0;

    // ---- submission ----

    private static async Task<IResult> SubmitAsync(
        HttpContext ctx, MaldexDbContext db, IArtifactStore store, AuditWriter audit, CurrentUser user, TimeProvider time,
        IOptions<SubmissionOptions> submissionOptions, IOptions<EgressOptions> egressOptions, TemplateCatalog templates, CancellationToken ct)
    {
        var opts = submissionOptions.Value;
        var vpnCountries = egressOptions.Value.VpnCountries;
        var request = ctx.Request;

        if (!request.HasFormContentType || !request.ContentType!.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            return Results.Problem(title: "Expected a multipart/form-data body.", statusCode: StatusCodes.Status415UnsupportedMediaType);

        var limit = opts.MaxUploadBytes + MultipartOverheadBytes;
        var sizeFeature = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = limit;

        var errors = new Dictionary<string, string[]>();
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        StoredBlob? blob = null;
        string? fileName = null;

        try
        {
            var boundary = HeaderUtilities.RemoveQuotes(MediaTypeHeaderValue.Parse(request.ContentType).Boundary).Value;
            if (string.IsNullOrEmpty(boundary))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["Missing multipart boundary."] });
            var reader = new MultipartReader(boundary, request.Body) { BodyLengthLimit = limit };

            while (await reader.ReadNextSectionAsync(ct) is { } section)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var cd)) continue;
                var name = HeaderUtilities.RemoveQuotes(cd.Name).Value;
                if (string.IsNullOrEmpty(name)) continue;

                if (string.Equals(name, "file", StringComparison.OrdinalIgnoreCase))
                {
                    if (blob is not null || errors.ContainsKey("file"))
                    {
                        errors["file"] = ["Only one file may be uploaded."];
                        break;
                    }
                    // Straight from the request body into the content-addressed store: no ASP.NET form buffering, no temp file.
                    blob = await store.PutAsync("samples", section.Body, opts.MaxUploadBytes, ct);
                    var rawName = cd.FileNameStar.HasValue ? cd.FileNameStar.Value : cd.FileName.Value;
                    fileName = SanitizeFileName(HeaderUtilities.RemoveQuotes(new Microsoft.Extensions.Primitives.StringSegment(rawName)).Value);
                }
                else
                {
                    var value = await ReadFieldAsync(section.Body, ct);
                    if (value is null) errors[name] = ["Field is too large."];
                    else fields[name] = value;
                }
            }
        }
        catch (BlobTooLargeException)
        {
            return Results.Problem(title: "The uploaded file is too large.", statusCode: StatusCodes.Status413PayloadTooLarge,
                detail: $"Samples may be at most {opts.MaxUploadBytes} bytes" + (opts.IsReal ? " (the most a real-mode guest can receive)." : "."));
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return Results.Problem(title: "The request body is too large.", statusCode: StatusCodes.Status413PayloadTooLarge,
                detail: $"Samples may be at most {opts.MaxUploadBytes} bytes" + (opts.IsReal ? " (the most a real-mode guest can receive)." : "."));
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or FormatException)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["Malformed multipart body."] });
        }

        // ---- validate (the blob, if any, is kept on failure: it is content-addressed and a concurrent valid upload may share it) ----
        Org? org = null;
        if (!fields.TryGetValue("orgId", out var orgRaw) || !Guid.TryParse(orgRaw, out var orgId) || orgId == Guid.Empty)
            errors["orgId"] = ["orgId is required and must be a GUID."];
        else if ((org = await db.Orgs.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orgId, ct)) is null)
            errors["orgId"] = ["Unknown organization."];

        var url = fields.GetValueOrDefault("url")?.Trim();
        if (string.IsNullOrEmpty(url)) url = null;
        if (blob is not null && url is not null)
        {
            errors["file"] = ["Provide either a file or a url, not both."];
            errors["url"] = ["Provide either a file or a url, not both."];
        }
        else if (blob is null && url is null && !errors.ContainsKey("file"))
        {
            errors["file"] = ["Provide a file or a url."];
            errors["url"] = ["Provide a file or a url."];
        }
        else if (url is not null)
        {
            if (TryNormalizeUrl(url, out var canonical)) url = canonical;
            else errors["url"] = ["url must be an absolute http or https URL of at most 2048 characters, without credentials, control, whitespace or format characters."];
        }
        if (blob is { Size: 0 }) errors["file"] = ["The file is empty."];

        var image = fields.GetValueOrDefault("image");
        var noTemplate = false;
        if (string.IsNullOrWhiteSpace(image))
        {
            image = (await templates.DefaultAsync(ct))?.Id;
            noTemplate = image is null;
        }
        else if (await templates.FindAvailableAsync(image.Trim(), ct) is { } found) image = found.Id;
        else if ((await templates.AvailableAsync(ct)).Count == 0) noTemplate = true;
        else errors["image"] = ["Unknown or disabled template."];

        var duration = 300;
        if (fields.TryGetValue("durationSec", out var durRaw) &&
            !(int.TryParse(durRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out duration) && duration is >= 60 and <= 1800))
            errors["durationSec"] = ["durationSec must be an integer between 60 and 1800."];

        var interactive = true;
        if (fields.TryGetValue("interactive", out var intRaw) && !bool.TryParse(intRaw, out interactive))
            errors["interactive"] = ["interactive must be true or false."];

        ValidateNetwork(fields, isUrl: url is not null, vpnCountries, errors, out var mode, out var exitCountry);

        if (errors.Count > 0) return Results.ValidationProblem(errors);
        if (noTemplate)
            return Results.Problem(title: "No sandbox template is available; ask an admin.", statusCode: StatusCodes.Status409Conflict);

        // ---- persist ----
        if (blob is not null) await EnsureSampleAsync(db, blob, fileName, time, ct);

        var now = time.GetUtcNow();
        var task = new AnalysisTask
        {
            Id = Guid.NewGuid(),
            OrgId = org!.Id,
            SampleSha256 = blob?.Sha256,
            Url = url,
            OriginalFileName = blob is null ? null : fileName,
            SubmittedByUserId = user.UserId,
            Image = image!,
            NetworkMode = mode,
            ExitCountry = exitCountry,
            DurationSec = duration,
            Interactive = interactive,
            Status = RunStatus.Queued,
            CreatedAt = now,
        };
        task.StatusHistory.Add(new TaskStatusHistory { TaskId = task.Id, From = null, To = RunStatus.Queued, At = now, Reason = "submitted" });
        db.Tasks.Add(task);
        await audit.WriteAsync("task.submit", task.Id.ToString(),
            $"org={org.Id}; mode={mode}; " + (blob is not null ? $"sha256={blob.Sha256}" : "url"), ct);

        var queued = await QueuedAsync(db, ct);
        return Results.Created($"/api/tasks/{task.Id}", TaskDto.From(task, org.Name, PositionOf(task, queued)));
    }

    private static async Task EnsureSampleAsync(MaldexDbContext db, StoredBlob blob, string? fileName, TimeProvider time, CancellationToken ct)
    {
        // Atomic insert-if-absent: concurrent duplicates never raise a key violation, so nothing is logged as an error.
        var firstSeen = time.GetUtcNow();
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO Samples (Sha256, Sha1, Md5, Size, OriginalName, BlobKey, FirstSeen)
                SELECT CAST({blob.Sha256} AS char(64)), CAST({blob.Sha1} AS char(40)), CAST({blob.Md5} AS char(32)), {blob.Size}, {fileName}, {blob.Key}, {firstSeen}
                WHERE NOT EXISTS (SELECT 1 FROM Samples WITH (UPDLOCK, HOLDLOCK) WHERE Sha256 = CAST({blob.Sha256} AS char(64)))", ct);
        }
        catch (Exception ex) when (ex is SqlException { Number: 2627 or 2601 })
        {
            // Backstop: another writer got in anyway; the existing row is what the task references.
        }
    }

    private static void ValidateNetwork(Dictionary<string, string> fields, bool isUrl, List<string> vpnCountries,
        Dictionary<string, string[]> errors, out NetworkMode mode, out string? exitCountry)
    {
        mode = NetworkMode.Simulated;
        exitCountry = null;
        var countryRaw = fields.GetValueOrDefault("exitCountry")?.Trim();
        if (string.IsNullOrEmpty(countryRaw)) countryRaw = null;

        var modeRaw = fields.GetValueOrDefault("networkMode")?.Trim();
        if (!string.IsNullOrEmpty(modeRaw))
        {
            if (!TryParseEnum<NetworkMode>(modeRaw, out mode))
            {
                errors["networkMode"] = ["Unknown networkMode."];
                return;
            }
        }
        else if (isUrl && vpnCountries.Count > 0)
        {
            mode = NetworkMode.LiveVpn;
            countryRaw ??= vpnCountries[0];
        }

        switch (mode)
        {
            case NetworkMode.LiveTor:
                errors["networkMode"] = ["LiveTor is not available yet"];
                break;
            case NetworkMode.Direct:
                errors["networkMode"] = ["Direct is only available to template edit sessions."];
                break;
            case NetworkMode.LiveVpn:
                if (vpnCountries.Count == 0)
                    errors["networkMode"] = ["LiveVpn is not available: no VPN exit countries are configured."];
                else if (countryRaw is null || vpnCountries.FirstOrDefault(c => string.Equals(c, countryRaw, StringComparison.OrdinalIgnoreCase)) is not { } match)
                    errors["exitCountry"] = [$"LiveVpn requires an exitCountry from: {string.Join(", ", vpnCountries)}."];
                else
                    exitCountry = match;
                break;
            default:
                if (countryRaw is not null) errors["exitCountry"] = ["exitCountry is only allowed with LiveVpn."];
                break;
        }
    }

    /// <summary>The verdict's enum value as a SQL expression (the column holds the name, which would sort alphabetically).</summary>
    private static readonly System.Linq.Expressions.Expression<Func<AnalysisTask, int>> VerdictRank = t =>
        t.Verdict == Verdict.Malicious ? (int)Verdict.Malicious
        : t.Verdict == Verdict.Suspicious ? (int)Verdict.Suspicious
        : t.Verdict == Verdict.NoThreats ? (int)Verdict.NoThreats : (int)Verdict.Unknown;

    private static bool IsHex(string s) => s.All(Uri.IsHexDigit);

    internal static bool TryParseEnum<T>(string raw, out T value) where T : struct, Enum
    {
        value = default;
        raw = raw.Trim();
        if (raw.Length == 0 || char.IsDigit(raw[0]) || raw[0] is '-' or '+') return false; // reject numeric values
        return Enum.TryParse(raw, ignoreCase: true, out value) && Enum.IsDefined(value);
    }

    /// <summary>Validates the (trimmed) input and returns the canonical form that is stored, so what is stored is what was validated.</summary>
    private static bool TryNormalizeUrl(string url, out string canonical)
    {
        canonical = "";
        if (url.Length > MaxUrlLength) return false;
        foreach (var c in url)
        {
            if (c < ' ' || c == '\u007F' || char.IsWhiteSpace(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format)
                return false;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;
        // Reject an '@' in the authority even when UserInfo parses as empty (https://@host).
        var start = url.IndexOf("://", StringComparison.Ordinal);
        if (start < 0) return false;
        var rest = url.AsSpan(start + 3);
        var end = rest.IndexOfAny('/', '?', '#');
        if ((end < 0 ? rest : rest[..end]).Contains('@')) return false;
        if (uri.AbsoluteUri.Length > MaxUrlLength) return false;
        canonical = uri.AbsoluteUri;
        return true;
    }

    /// <summary>Strips C0 control characters and truncates to 260 chars without splitting a surrogate pair. Stored as data only.</summary>
    internal static string? SanitizeFileName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var sb = new StringBuilder(Math.Min(name.Length, MaxFileNameLength + 1));
        foreach (var c in name) if (c >= ' ') sb.Append(c);
        if (sb.Length > MaxFileNameLength)
        {
            var cut = MaxFileNameLength;
            if (char.IsHighSurrogate(sb[cut - 1])) cut--;
            sb.Length = cut;
        }
        return sb.Length == 0 ? null : sb.ToString();
    }

    private static async Task<string?> ReadFieldAsync(Stream body, CancellationToken ct)
    {
        var buffer = new byte[MaxFieldBytes + 1];
        var total = 0;
        int n;
        while (total < buffer.Length && (n = await body.ReadAsync(buffer.AsMemory(total), ct)) > 0) total += n;
        if (total > MaxFieldBytes) return null;
        return Encoding.UTF8.GetString(buffer, 0, total);
    }
}
