using System.Text.Json;
using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Maldex.Core.Text;
using Maldex.Data;
using Maldex.Infrastructure.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Maldex.Processing;

/// <param name="Indexed">Rows written.</param>
/// <param name="Ignored">Flows beyond the per-task cap that were not indexed.</param>
/// <param name="BodiesDropped">Response bodies not stored (oversize or over the per-task budget).</param>
public sealed record HttpFlowIndexResult(int Indexed, int Ignored, int BodiesDropped = 0);

/// <summary>
/// Parses a task's <c>MitmFlows</c> artifacts and stores one <see cref="HttpTransaction"/> per flow. Safe to run again:
/// the task's earlier rows are replaced. Headers are stored as a JSON array of <c>[name, value]</c> pairs; non-empty
/// response bodies become content-addressed blobs referenced by <c>BodyBlobKey</c>.
/// </summary>
public sealed class HttpFlowIndexer(IServiceScopeFactory scopes, IArtifactStore artifacts)
{
    private const long MaxBodyBytes = MitmFlowParser.MaxBodyBytes;
    private const int MaxHeaders = MitmFlowParser.MaxHeaders;
    private const int HeaderNameMax = 256;
    private const int MethodMax = 16;
    private const int HostMax = 512;
    private const int SuffixLength = 12; // GuestText.Clean's truncation marker

    public async Task<HttpFlowIndexResult> IndexAsync(Guid taskId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();

        var flowArtifacts = await db.Artifacts.AsNoTracking()
            .Where(a => a.TaskId == taskId && a.Kind == ArtifactKind.MitmFlows)
            .OrderBy(a => a.CreatedAt).ThenBy(a => a.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        var rows = new List<HttpTransaction>();
        var ignored = 0;
        var droppedBodies = 0;
        var budget = MitmFlowParser.MaxTotalBodyBytes;
        foreach (var artifact in flowArtifacts)
        {
            await using var stream = await artifacts.OpenReadAsync(artifact.BlobKey, ct).ConfigureAwait(false);
            var parsed = MitmFlowParser.ParseWithStats(stream, MitmFlowParser.MaxFlows - rows.Count, budget);
            ignored += parsed.Ignored;
            droppedBodies += parsed.BodiesDropped;
            budget -= parsed.BodyBytes;
            foreach (var flow in parsed.Flows)
            {
                rows.Add(await ToRowAsync(taskId, flow, ct).ConfigureAwait(false));
            }
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await db.HttpTransactions.Where(x => x.TaskId == taskId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        db.HttpTransactions.AddRange(rows);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new HttpFlowIndexResult(rows.Count, ignored, droppedBodies);
    }

    private async Task<HttpTransaction> ToRowAsync(Guid taskId, ParsedHttpTransaction flow, CancellationToken ct)
    {
        string? bodyKey = null;
        if (flow.ResponseBody is { Length: > 0 } body)
        {
            try
            {
                await using var content = new MemoryStream(body, writable: false);
                bodyKey = (await artifacts.PutAsync("httpbodies", content, MaxBodyBytes, ct).ConfigureAwait(false)).Key;
            }
            catch (BlobTooLargeException)
            {
                // Keep the transaction; just not the oversized body.
            }
        }

        return new HttpTransaction
        {
            TaskId = taskId,
            Method = Fit(flow.Method, MethodMax),
            Url = GuestText.Clean(flow.Url),
            Host = Fit(flow.Host, HostMax),
            Status = flow.Status,
            ReqHeaders = SerializeHeaders(flow.RequestHeaders),
            RespHeaders = SerializeHeaders(flow.ResponseHeaders),
            BodyBlobKey = bodyKey,
        };
    }

    /// <summary>Cleaned text left untouched when it fits the column; otherwise truncated so text plus marker fits.</summary>
    private static string Fit(string s, int column)
    {
        var cleaned = GuestText.Clean(s, column);
        return cleaned.Length <= column ? cleaned : GuestText.Clean(s, column - SuffixLength);
    }

    private static string SerializeHeaders(IReadOnlyList<KeyValuePair<string, string>> headers) =>
        JsonSerializer.Serialize(headers.Take(MaxHeaders).Select(h => new[] { GuestText.Clean(h.Key, HeaderNameMax), GuestText.Clean(h.Value) }));
}
