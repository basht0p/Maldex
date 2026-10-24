using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Maldex.Core.Domain;
using Maldex.Data;
using Maldex.Integration.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Maldex.Integration.Tests.Web;

[Collection("sql")]
public sealed class TemplateEndpointsTests(SqlServerFixture sql) : IDisposable
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

    [Fact]
    public async Task ListTemplates_ReturnsEnabledDefaultFirst()
    {
        var f = Factory();
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
            var now = DateTimeOffset.UtcNow;
            var first = new Template { Id = "aaa-first", DisplayName = "AAA First", CreatedAt = now };
            db.Templates.Add(first);
            db.Templates.Add(new Template { Id = "zzz-off", DisplayName = "ZZZ Off", Enabled = false, CreatedAt = now });
            db.Templates.Add(new Template { Id = "aaa-no-version", DisplayName = "AAA No version", CreatedAt = now }); // not available
            await db.SaveChangesAsync(Ct);
            var v1 = new TemplateVersion { Id = Guid.NewGuid(), TemplateId = first.Id, Number = 1, Origin = TemplateVersionOrigin.Imported, FileStem = "aaa-first", SealedAt = now };
            db.TemplateVersions.Add(v1);
            await db.SaveChangesAsync(Ct);
            first.CurrentVersionId = v1.Id;
            await db.SaveChangesAsync(Ct);
        }
        using var client = f.CreateClient(UserRole.Analyst);

        var list = await client.GetFromJsonAsync<JsonElement>("/api/templates", Ct);

        var rows = list.EnumerateArray().ToList();
        Assert.Equal(["win10-ltsc-2021", "aaa-first"], rows.Select(r => r.GetProperty("id").GetString()));
        Assert.True(rows[0].GetProperty("isDefault").GetBoolean());
        Assert.False(rows[1].GetProperty("isDefault").GetBoolean());
        Assert.Equal("AAA First", rows[1].GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task ListTemplates_RequiresAnalyst()
    {
        var f = Factory();
        // DevAuth authenticates every client, so the policy is pinned by reading the endpoint's metadata too.
        using var analyst = f.CreateClient(UserRole.Analyst);
        Assert.Equal(HttpStatusCode.OK, (await analyst.GetAsync("/api/templates", Ct)).StatusCode);

        var sources = f.Services.GetServices<Microsoft.AspNetCore.Routing.EndpointDataSource>();
        var endpoint = sources.SelectMany(s => s.Endpoints)
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText?.TrimEnd('/') == "/api/templates"
                && e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()!.HttpMethods.Contains("GET"));
        var auth = endpoint.Metadata.GetOrderedMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>();
        Assert.Contains(auth, a => a.Policy == Maldex.Web.Auth.AuthSetup.AnalystPolicy);
    }
}
