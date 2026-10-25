using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Maldex.Core.Domain;
using Maldex.Data;
using Maldex.Integration.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Maldex.Integration.Tests.Web;

[Collection("sql")]
public sealed class TaskListQueryTests(SqlServerFixture sql) : IDisposable
{
    private readonly List<MaldexAppFactory> _factories = [];
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() { foreach (var f in _factories) f.Dispose(); }

    private MaldexAppFactory Factory()
    {
        var f = new MaldexAppFactory(sql);
        _factories.Add(f);
        return f;
    }

    private static async Task<Guid> CreateOrgAsync(HttpClient admin)
    {
        var resp = await admin.PostAsJsonAsync("/api/orgs", new { name = "Acme", retentionDays = 30 }, Ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> SubmitFileAsync(HttpClient c, Guid org, string name, string content)
    {
        using var form = new MultipartFormDataContent { { new StringContent(org.ToString()), "orgId" } };
        form.Add(new ByteArrayContent(Encoding.ASCII.GetBytes(content)), "file", name);
        var resp = await c.PostAsync("/api/tasks", form, Ct);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return await resp.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static async Task<JsonElement> SubmitUrlAsync(HttpClient c, Guid org, string url)
    {
        using var form = new MultipartFormDataContent { { new StringContent(org.ToString()), "orgId" }, { new StringContent(url), "url" } };
        var resp = await c.PostAsync("/api/tasks", form, Ct);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return await resp.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static async Task<JsonElement> ListAsync(HttpClient c, string qs)
    {
        var resp = await c.GetAsync("/api/tasks?" + qs, Ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return await resp.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static List<Guid> Ids(JsonElement list) => list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();

    private static async Task UpdateAsync(MaldexAppFactory f, Guid id, Action<AnalysisTask> change)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        var t = await db.Tasks.SingleAsync(x => x.Id == id, Ct);
        change(t);
        await db.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Hash_search_matches_sha256_sha1_and_md5_exactly()
    {
        using var f = Factory();
        using var analyst = f.CreateClient(UserRole.Analyst, "analyst");
        var org = await CreateOrgAsync(f.CreateClient(UserRole.Admin, "admin"));
        var a = await SubmitFileAsync(analyst, org, "a.exe", "sample A");
        await SubmitFileAsync(analyst, org, "b.exe", "sample B");
        var aId = a.GetProperty("id").GetGuid();
        var sha256 = a.GetProperty("sampleSha256").GetString()!;

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        var sample = await db.Samples.SingleAsync(s => s.Sha256 == sha256, Ct);
        // Stored hashes must be lowercase: the search lowercases the input and compares with them.
        Assert.Equal(sample.Sha256.ToLowerInvariant(), sample.Sha256);
        Assert.Equal(sample.Sha1.ToLowerInvariant(), sample.Sha1);
        Assert.Equal(sample.Md5.ToLowerInvariant(), sample.Md5);

        Assert.Equal([aId], Ids(await ListAsync(analyst, "q=" + sha256.ToUpperInvariant())));
        Assert.Equal([aId], Ids(await ListAsync(analyst, "q=" + sample.Sha1)));
        Assert.Equal([aId], Ids(await ListAsync(analyst, "q=" + sample.Md5.ToUpperInvariant())));
        Assert.Empty(Ids(await ListAsync(analyst, "q=" + new string('a', 64))));
    }

    [Fact]
    public async Task Text_search_matches_file_name_or_url_substring()
    {
        using var f = Factory();
        using var analyst = f.CreateClient(UserRole.Analyst, "analyst");
        var org = await CreateOrgAsync(f.CreateClient(UserRole.Admin, "admin"));
        var file = await SubmitFileAsync(analyst, org, "invoice.exe", "x");
        var url = await SubmitUrlAsync(analyst, org, "http://example.com/a");
        await SubmitFileAsync(analyst, org, "other.exe", "y");

        Assert.Equal([file.GetProperty("id").GetGuid()], Ids(await ListAsync(analyst, "q=invoice")));
        Assert.Equal([url.GetProperty("id").GetGuid()], Ids(await ListAsync(analyst, "q=example.com")));
    }

    [Fact]
    public async Task Filters_combine_verdict_mine_and_date_range()
    {
        using var f = Factory();
        using var a = f.CreateClient(UserRole.Analyst, "analyst-a");
        using var b = f.CreateClient(UserRole.Analyst, "analyst-b");
        var org = await CreateOrgAsync(f.CreateClient(UserRole.Admin, "admin"));
        var a1 = (await SubmitFileAsync(a, org, "a1.exe", "a1")).GetProperty("id").GetGuid();
        var a2 = (await SubmitFileAsync(a, org, "a2.exe", "a2")).GetProperty("id").GetGuid();
        var b1 = (await SubmitFileAsync(b, org, "b1.exe", "b1")).GetProperty("id").GetGuid();
        await UpdateAsync(f, a1, t => { t.Verdict = Verdict.Malicious; t.CreatedAt = DateTimeOffset.Parse("2026-03-10T23:59:59Z"); });
        await UpdateAsync(f, a2, t => t.CreatedAt = DateTimeOffset.Parse("2026-03-11T00:00:00Z"));
        await UpdateAsync(f, b1, t => { t.Verdict = Verdict.Malicious; t.CreatedAt = DateTimeOffset.Parse("2026-03-10T12:00:00Z"); });

        Assert.Equal(new[] { a1, b1 }.Order(), Ids(await ListAsync(a, "verdict=malicious")).Order());
        Assert.Equal([a1], Ids(await ListAsync(a, "verdict=Malicious&mine=true")));
        Assert.Equal(new[] { a1, b1 }.Order(), Ids(await ListAsync(a, "from=2026-03-10&to=2026-03-10")).Order()); // to is inclusive of the whole day
        Assert.Equal([a2], Ids(await ListAsync(a, "from=2026-03-11")));
        Assert.Equal([a1], Ids(await ListAsync(a, "mine=true&to=2026-03-10")));
        Assert.Empty(Ids(await ListAsync(a, "from=2026-03-12")));
    }

    [Fact]
    public async Task Duration_sort_puts_unfinished_tasks_last_in_both_directions()
    {
        using var f = Factory();
        using var analyst = f.CreateClient(UserRole.Analyst, "analyst");
        var org = await CreateOrgAsync(f.CreateClient(UserRole.Admin, "admin"));
        var t0 = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var ids = new List<Guid>();
        foreach (var (name, sec, viaEnded) in new[] { ("d60", 60, false), ("d120", 120, true), ("d30", 30, false) })
        {
            var id = (await SubmitFileAsync(analyst, org, name + ".exe", name)).GetProperty("id").GetGuid();
            await UpdateAsync(f, id, t =>
            {
                t.StartedAt = t0;
                if (viaEnded) t.EndedAt = t0.AddSeconds(sec); else t.CompletedAt = t0.AddSeconds(sec);
            });
            ids.Add(id);
        }
        var queued = (await SubmitFileAsync(analyst, org, "q.exe", "q")).GetProperty("id").GetGuid();

        Assert.Equal([ids[1], ids[0], ids[2], queued], Ids(await ListAsync(analyst, "sort=duration&dir=desc")));
        Assert.Equal([ids[2], ids[0], ids[1], queued], Ids(await ListAsync(analyst, "sort=duration&dir=asc")));
    }

    [Fact]
    public async Task Verdict_sort_orders_by_severity_then_newest()
    {
        using var f = Factory();
        using var analyst = f.CreateClient(UserRole.Analyst, "analyst");
        var org = await CreateOrgAsync(f.CreateClient(UserRole.Admin, "admin"));
        var low = (await SubmitFileAsync(analyst, org, "l.exe", "l")).GetProperty("id").GetGuid();
        var high = (await SubmitFileAsync(analyst, org, "h.exe", "h")).GetProperty("id").GetGuid();
        await UpdateAsync(f, high, t => t.Verdict = Verdict.Malicious);
        Assert.Equal([high, low], Ids(await ListAsync(analyst, "sort=verdict")));
        Assert.Equal([low, high], Ids(await ListAsync(analyst, "sort=verdict&dir=asc")));
    }

    [Fact]
    public async Task Dto_carries_submitter_name_and_ran_seconds()
    {
        using var f = Factory();
        using var analyst = f.CreateClient(UserRole.Analyst, "analyst");
        var org = await CreateOrgAsync(f.CreateClient(UserRole.Admin, "admin"));
        var done = (await SubmitFileAsync(analyst, org, "d.exe", "d")).GetProperty("id").GetGuid();
        var open = (await SubmitFileAsync(analyst, org, "o.exe", "o")).GetProperty("id").GetGuid();
        var t0 = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        await UpdateAsync(f, done, t => { t.StartedAt = t0; t.EndedAt = t0.AddSeconds(90); });

        var items = (await ListAsync(analyst, "")).GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("id").GetGuid());
        Assert.Equal(90, items[done].GetProperty("ranSec").GetInt32());
        Assert.Equal(JsonValueKind.Null, items[open].GetProperty("ranSec").ValueKind);
        Assert.False(string.IsNullOrEmpty(items[done].GetProperty("submittedByName").GetString()));
    }

    [Theory]
    [InlineData("verdict=Bogus")]
    [InlineData("sort=size")]
    [InlineData("dir=up")]
    [InlineData("from=2026-13-01")]
    [InlineData("to=nope")]
    [InlineData("q=%20%20")]
    public async Task Bad_parameters_are_400_problem_details(string qs)
    {
        using var f = Factory();
        using var analyst = f.CreateClient(UserRole.Analyst, "analyst");
        var resp = await analyst.GetAsync("/api/tasks?" + qs, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.True(body.TryGetProperty("errors", out _));
    }

    [Fact]
    public async Task Query_longer_than_256_is_400()
    {
        using var f = Factory();
        using var analyst = f.CreateClient(UserRole.Analyst, "analyst");
        Assert.Equal(HttpStatusCode.BadRequest, (await analyst.GetAsync("/api/tasks?q=" + new string('a', 257), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await analyst.GetAsync("/api/tasks?q=" + new string('a', 256), Ct)).StatusCode);
    }

    [Fact]
    public async Task Hostile_query_text_is_treated_as_data()
    {
        using var f = Factory();
        using var analyst = f.CreateClient(UserRole.Analyst, "analyst");
        var org = await CreateOrgAsync(f.CreateClient(UserRole.Admin, "admin"));
        await SubmitFileAsync(analyst, org, "a.exe", "a");
        Assert.Empty(Ids(await ListAsync(analyst, "q=" + Uri.EscapeDataString("%' OR 1=1 --"))));
    }
}
