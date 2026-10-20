using System.Text;
using System.Text.Json;
using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Maldex.Integration.Tests.Fixtures;
using Maldex.Integration.Tests.Orchestrator;
using Maldex.Processing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Maldex.Integration.Tests.Processing;

[Collection("sql")]
public class HttpFlowIndexingTests(SqlServerFixture sql)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<Guid> ProcessingTaskAsync(OrchestratorHarness h)
    {
        var id = await h.SeedPreparingTaskAsync(sampleContent: Encoding.ASCII.GetBytes("benign " + Guid.NewGuid()));
        await h.RunAsync(id);
        Assert.Equal(RunStatus.Processing, await h.StatusOfAsync(id));
        return id;
    }

    private static async Task AddFlowsArtifactAsync(OrchestratorHarness h, Guid taskId, string jsonl)
    {
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes(jsonl));
        var blob = await h.Get<IArtifactStore>().PutAsync("mitmflows", content, 10_000_000, Ct);
        await using var db = h.NewContext();
        db.Artifacts.Add(new Artifact { Id = Guid.NewGuid(), TaskId = taskId, Kind = ArtifactKind.MitmFlows, BlobKey = blob.Key, Sha256 = blob.Sha256, Size = blob.Size, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(Ct);
    }

    private static string Flow(string url, string host, string respHeaderValue, string? body, int? status = 200)
    {
        var obj = new Dictionary<string, object?>
        {
            ["ts"] = 1760000000.25, ["method"] = "GET", ["url"] = url, ["host"] = host, ["status"] = status,
            ["req_headers"] = new[] { new[] { "Host", host } },
            ["resp_headers"] = new[] { new[] { "X-Evil", respHeaderValue } },
            ["req_body_b64"] = null,
            ["resp_body_b64"] = body is null ? null : Convert.ToBase64String(Encoding.UTF8.GetBytes(body)),
            ["tls"] = false, ["truncated"] = false,
        };
        return JsonSerializer.Serialize(obj);
    }

    [Fact]
    public async Task Index_WritesHttpTransactions_CleansStrings()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql);
        var id = await ProcessingTaskAsync(h);
        var hostile = "a\u0000b\u001bc" + new string('x', 100_000);
        await AddFlowsArtifactAsync(h, id, Flow("http://evil.test/p", "evil.test", hostile, "payload-body") + "\n" + Flow("http://evil.test/empty", "evil.test", "ok", null, null) + "\ngarbage\n");

        await h.Get<HttpFlowIndexer>().IndexAsync(id, Ct);

        await using var db = h.NewContext();
        var rows = await db.HttpTransactions.Where(x => x.TaskId == id).OrderBy(x => x.Id).ToListAsync(Ct);
        Assert.Equal(2, rows.Count);
        Assert.Equal("evil.test", rows[0].Host);
        Assert.Equal(200, rows[0].Status);
        Assert.Null(rows[1].Status);
        var pairs = JsonSerializer.Deserialize<string[][]>(rows[0].RespHeaders)!;
        Assert.Equal("X-Evil", pairs[0][0]);
        Assert.StartsWith("abc", pairs[0][1]); // control characters stripped
        Assert.True(pairs[0][1].Length < 40_000); // oversize truncated
        Assert.Equal("Host", JsonSerializer.Deserialize<string[][]>(rows[0].ReqHeaders)![0][0]);

        Assert.NotNull(rows[0].BodyBlobKey);
        await using var stream = await h.Get<IArtifactStore>().OpenReadAsync(rows[0].BodyBlobKey!, Ct);
        using var reader = new StreamReader(stream);
        Assert.Equal("payload-body", await reader.ReadToEndAsync(Ct));
        Assert.Null(rows[1].BodyBlobKey);
    }

    [Fact]
    public async Task Index_Method_FitsColumn_Untruncated_WhenShortEnough()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql);
        var id = await ProcessingTaskAsync(h);
        string Line(string m) => Flow("http://a.test/", "a.test", "v", null).Replace("\"method\":\"GET\"", $"\"method\":\"{m}\"");
        var longMethod = new string('M', 40);
        await AddFlowsArtifactAsync(h, id, Line("OPTIONS") + "\n" + Line(longMethod));

        await h.Get<HttpFlowIndexer>().IndexAsync(id, Ct);

        await using var db = h.NewContext();
        var methods = await db.HttpTransactions.Where(x => x.TaskId == id).OrderBy(x => x.Id).Select(x => x.Method).ToListAsync(Ct);
        Assert.Equal("OPTIONS", methods[0]);
        Assert.True(methods[1].Length <= 16);
        Assert.StartsWith("MMMM", methods[1]);
        Assert.EndsWith("[truncated]", methods[1]);
    }

    [Fact]
    public async Task Index_IsIdempotent()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql);
        var id = await ProcessingTaskAsync(h);
        await AddFlowsArtifactAsync(h, id, Flow("http://a.test/", "a.test", "v", "b1") + "\n" + Flow("http://b.test/", "b.test", "v", "b2"));
        var indexer = h.Get<HttpFlowIndexer>();

        await indexer.IndexAsync(id, Ct);
        await indexer.IndexAsync(id, Ct);

        await using var db = h.NewContext();
        Assert.Equal(2, await db.HttpTransactions.CountAsync(x => x.TaskId == id, Ct));
    }

    [Fact]
    public async Task Pipeline_IndexesFlows_WhenArtifactExists()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql);
        var id = await ProcessingTaskAsync(h);
        await AddFlowsArtifactAsync(h, id, Flow("http://a.test/", "a.test", "v", "b1"));

        await h.Get<ProcessingPipeline>().ProcessAsync(id, Ct);

        await using var db = h.NewContext();
        Assert.Equal(1, await db.HttpTransactions.CountAsync(x => x.TaskId == id, Ct));
        Assert.Equal(RunStatus.Completed, await h.StatusOfAsync(id));
    }

    [Fact]
    public async Task Pipeline_IndexerFailure_IsWarning_TaskCompletes()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql);
        var id = await ProcessingTaskAsync(h);
        await using (var db = h.NewContext())
        {
            // An artifact row whose blob is gone: the indexer cannot read it.
            db.Artifacts.Add(new Artifact { Id = Guid.NewGuid(), TaskId = id, Kind = ArtifactKind.MitmFlows, BlobKey = "mitmflows/00/00/" + new string('0', 64), Sha256 = new string('0', 64), Size = 1, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Ct);
        }

        await h.Get<ProcessingPipeline>().ProcessAsync(id, Ct);

        await using var check = h.NewContext();
        var task = await check.Tasks.SingleAsync(t => t.Id == id, Ct);
        Assert.Equal(RunStatus.Completed, task.Status);
        Assert.Contains(task.ProcessingWarnings, w => w.StartsWith("http indexing failed:", StringComparison.Ordinal));
    }
}
