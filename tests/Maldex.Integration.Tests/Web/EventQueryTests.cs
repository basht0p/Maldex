using System.Net;
using System.Text.Json;
using Maldex.Core.Domain;
using Maldex.Data;
using Maldex.Integration.Tests.Fixtures;
using Maldex.Web.Endpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Maldex.Integration.Tests.Web;

[Collection("sql")]
public sealed class EventQueryTests(SqlServerFixture sql) : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly List<MaldexAppFactory> _factories = [];

    public void Dispose() { foreach (var f in _factories) f.Dispose(); }

    private MaldexAppFactory Factory()
    {
        var f = new MaldexAppFactory(sql);
        _factories.Add(f);
        return f;
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient c, string url)
    {
        var resp = await c.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return await resp.Content.ReadFromJsonAsyncElement();
    }

    private static async Task AddEventAsync(MaldexAppFactory f, Guid taskId, string summary)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        db.BehaviorEvents.Add(new BehaviorEvent { TaskId = taskId, Pid = 300, Type = EventType.File, At = DateTimeOffset.Parse("2026-01-01T00:01:00+00:00"), Summary = summary });
        await db.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Events_filter_by_pid_process_and_text()
    {
        var f = Factory();
        var seeded = await CompletedTaskSeed.SeedAsync(f);
        using var client = f.CreateClient(UserRole.Analyst);
        var baseUrl = $"/api/tasks/{seeded.TaskId}/events";

        Assert.Equal(1, (await GetJsonAsync(client, baseUrl + "?q=evil")).GetArrayLength());
        Assert.Equal(1, (await GetJsonAsync(client, baseUrl + "?q=EVIL")).GetArrayLength());
        Assert.Equal(0, (await GetJsonAsync(client, baseUrl + "?pid=999")).GetArrayLength());
        Assert.Equal(4, (await GetJsonAsync(client, baseUrl + "?pid=200")).GetArrayLength());

        var evilNode = await GetJsonAsync(client, $"/api/tasks/{seeded.TaskId}/processes");
        var evilId = evilNode.EnumerateArray().Single(n => n.GetProperty("pid").GetInt32() == 200).GetProperty("id").GetInt64();
        Assert.Equal(4, (await GetJsonAsync(client, baseUrl + $"?processId={evilId}")).GetArrayLength());
        Assert.Equal(1, (await GetJsonAsync(client, baseUrl + $"?processId={evilId}&q=10.0.0.9")).GetArrayLength());
    }

    [Fact]
    public async Task Events_q_over_200_chars_is_400()
    {
        var f = Factory();
        var seeded = await CompletedTaskSeed.SeedAsync(f);
        using var client = f.CreateClient(UserRole.Analyst);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/tasks/{seeded.TaskId}/events?q={new string('a', 201)}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/tasks/{seeded.TaskId}/events?q=%20%20", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/tasks/{seeded.TaskId}/events?q={new string('a', 200)}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Csv_export_escapes_formula_and_quote_characters()
    {
        var f = Factory();
        var seeded = await CompletedTaskSeed.SeedAsync(f);
        await AddEventAsync(f, seeded.TaskId, "=cmd|' /c calc'!A1");
        await AddEventAsync(f, seeded.TaskId, "a,b \"q\"\nline2");
        using var client = f.CreateClient(UserRole.Analyst);

        var resp = await client.GetAsync($"/api/tasks/{seeded.TaskId}/events/export?format=csv", Ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/csv", resp.Content.Headers.ContentType!.MediaType);
        Assert.Equal($"attachment; filename=\"task-{seeded.TaskId}-events.csv\"", resp.Content.Headers.ContentDisposition!.ToString());
        var csv = await resp.Content.ReadAsStringAsync(Ct);

        Assert.StartsWith(EventCsv.Header + "\r\n", csv);
        Assert.Contains(",'=cmd|' /c calc'!A1,", csv);
        Assert.Contains(",\"a,b \"\"q\"\"\nline2\",", csv);
    }

    [Fact]
    public void EventCsv_cell_rules()
    {
        Assert.Equal("plain", EventCsv.Cell("plain"));
        foreach (var lead in new[] { "=", "+", "-", "@", "\t" })
            Assert.StartsWith("'" + lead, EventCsv.Cell(lead + "x"));
        Assert.Equal("\"'\rx\"", EventCsv.Cell("\rx"));
        Assert.Equal("", EventCsv.Cell(""));
    }

    [Fact]
    public async Task Json_export_is_a_parseable_array_with_filters_applied()
    {
        var f = Factory();
        var seeded = await CompletedTaskSeed.SeedAsync(f);
        using var client = f.CreateClient(UserRole.Analyst);

        var resp = await client.GetAsync($"/api/tasks/{seeded.TaskId}/events/export?format=json&q=evil", Ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/json", resp.Content.Headers.ContentType!.MediaType);
        Assert.Contains("events.json", resp.Content.Headers.ContentDisposition!.ToString());
        var arr = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(Ct)).RootElement;
        Assert.Equal(JsonValueKind.Array, arr.ValueKind);
        Assert.Equal(1, arr.GetArrayLength());
        Assert.Equal("started evil.exe", arr[0].GetProperty("summary").GetString());

        var all = JsonDocument.Parse(await client.GetStringAsync($"/api/tasks/{seeded.TaskId}/events/export?format=json&pid=999", Ct)).RootElement;
        Assert.Equal(0, all.GetArrayLength());
    }

    [Fact]
    public async Task Export_returns_every_row_under_the_cap()
    {
        Assert.Equal(100_000, LiveControlEndpoints.MaxExportRows);
        var f = Factory();
        var seeded = await CompletedTaskSeed.SeedAsync(f, extraEvents: 50);
        using var client = f.CreateClient(UserRole.Analyst);

        var csv = await client.GetStringAsync($"/api/tasks/{seeded.TaskId}/events/export?format=csv", Ct);
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(54, lines.Length - 1);
    }

    [Fact]
    public async Task Export_requires_a_valid_format()
    {
        var f = Factory();
        var seeded = await CompletedTaskSeed.SeedAsync(f);
        using var client = f.CreateClient(UserRole.Analyst);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/tasks/{seeded.TaskId}/events/export", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/tasks/{seeded.TaskId}/events/export?format=xml", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/tasks/{Guid.NewGuid()}/events/export?format=csv", Ct)).StatusCode);
    }

    [Fact]
    public async Task Export_is_audited()
    {
        var f = Factory();
        var seeded = await CompletedTaskSeed.SeedAsync(f);
        using var client = f.CreateClient(UserRole.Analyst);

        (await client.GetAsync($"/api/tasks/{seeded.TaskId}/events/export?format=json", Ct)).EnsureSuccessStatusCode();

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        var row = await db.AuditLogs.SingleAsync(a => a.Action == "task.events.export", Ct);
        Assert.Equal(seeded.TaskId.ToString(), row.Target);
        Assert.Contains("format=json", row.Details);
        Assert.Contains("rows=4", row.Details);
        Assert.DoesNotContain("complete=false", row.Details);
    }

    [Fact]
    public async Task Export_cut_short_is_still_audited_as_incomplete()
    {
        var f = Factory();
        var seeded = await CompletedTaskSeed.SeedAsync(f, extraEvents: 20_000);
        using var client = f.CreateClient(UserRole.Analyst);

        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct))
        {
            var resp = await client.GetAsync($"/api/tasks/{seeded.TaskId}/events/export?format=csv", HttpCompletionOption.ResponseHeadersRead, cts.Token);
            await using var body = await resp.Content.ReadAsStreamAsync(cts.Token);
            await body.ReadExactlyAsync(new byte[1024], cts.Token);
            cts.Cancel(); // the client goes away mid-download
            resp.Dispose();
        }

        AuditLog? row = null;
        for (var i = 0; i < 100 && row is null; i++)
        {
            using var scope = f.Services.CreateScope();
            row = await scope.ServiceProvider.GetRequiredService<MaldexDbContext>().AuditLogs.AsNoTracking()
                .SingleOrDefaultAsync(a => a.Action == "task.events.export", Ct);
            if (row is null) await Task.Delay(100, Ct);
        }
        Assert.NotNull(row);
        Assert.Contains("complete=false", row.Details);
    }

    [Fact]
    public async Task Export_requires_analyst_auth()
    {
        var f = Factory();
        var seeded = await CompletedTaskSeed.SeedAsync(f);
        // A second host on the same database, in Local mode: dev auth would admit an unauthenticated client.
        var local = new MaldexAppFactory(sql, authMode: "Local", connectionString: f.ConnectionString);
        _factories.Add(local);
        using var anonymous = local.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/tasks/{seeded.TaskId}/events/export?format=csv", Ct)).StatusCode);
    }
}

internal static class JsonResponseExtensions
{
    public static async Task<JsonElement> ReadFromJsonAsyncElement(this HttpContent content) =>
        JsonDocument.Parse(await content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.Clone();
}
