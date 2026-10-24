using System.Net.Http.Json;
using System.Text.Json;
using Maldex.Core.Domain;
using Maldex.Data;
using Maldex.Integration.Tests.Fixtures;
using Maldex.Web.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Maldex.Integration.Tests.Web;

public sealed class TemplateProvenanceTests
{
    private static TemplateVersion Version() => new()
    {
        Number = 3, Origin = TemplateVersionOrigin.Edit, WindowsEdition = "Windows 11 Pro", WindowsBuild = "26300",
        AgentVersion = "1.0.0", SealedAt = new DateTimeOffset(2026, 10, 24, 23, 30, 0, TimeSpan.Zero),
    };

    [Fact]
    public void Summary_Full() =>
        Assert.Equal("win11-golden v3 (Windows 11 Pro 26300, agent 1.0.0, sealed 2026-10-24 by basht0p)",
            TemplateProvenance.Summary("win11-golden", Version(), "basht0p"));

    [Fact]
    public void Summary_ImportedWithoutProvenance() =>
        Assert.Equal("win11-26h2 v1 (imported; provenance not recorded)",
            TemplateProvenance.Summary("win11-26h2", new TemplateVersion { Number = 1, Origin = TemplateVersionOrigin.Imported }, null));

    [Fact]
    public void Summary_ImportedWithBlankProvenance_CountsAsNotRecorded() =>
        Assert.Equal("win11-26h2 v1 (imported; provenance not recorded)",
            TemplateProvenance.Summary("win11-26h2",
                new TemplateVersion { Number = 1, Origin = TemplateVersionOrigin.Imported, WindowsEdition = "", WindowsBuild = " ", AgentVersion = "" }, null));

    [Fact]
    public void Summary_NoSealer() =>
        Assert.Equal("win11-golden v3 (Windows 11 Pro 26300, agent 1.0.0, sealed 2026-10-24)",
            TemplateProvenance.Summary("win11-golden", Version(), null));
}

/// <summary>The task endpoints expose the bound template version (a seeded task is submitted through the API, then bound in the db).</summary>
[Collection("sql")]
public sealed class TaskTemplateDtoTests(SqlServerFixture sql) : IDisposable
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
    public async Task TaskDto_IncludesTemplate_WhenBound()
    {
        var f = Factory();
        var seeded = await CompletedTaskSeed.SeedAsync(f);
        await TemplateSeed.BindAsync(f, seeded.TaskId);
        using var client = f.CreateClient(UserRole.Analyst);

        var one = (await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{seeded.TaskId}", Ct)).GetProperty("template");
        Assert.Equal("win11-golden", one.GetProperty("id").GetString());
        Assert.Equal("Win11 Golden", one.GetProperty("displayName").GetString());
        Assert.Equal(3, one.GetProperty("versionNumber").GetInt32());
        Assert.Equal("Edit", one.GetProperty("origin").GetString());
        Assert.Equal("basht0p", one.GetProperty("sealedBy").GetString());
        Assert.Equal("win11-golden v3 (Windows 11 Pro 26300, agent 1.0.0, sealed 2026-10-24 by basht0p)", one.GetProperty("summary").GetString());

        var list = await client.GetFromJsonAsync<JsonElement>("/api/tasks", Ct);
        var row = list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == seeded.TaskId);
        Assert.Equal("win11-golden", row.GetProperty("template").GetProperty("id").GetString());
    }

    [Fact]
    public async Task TaskDto_TemplateNull_ForOldTask()
    {
        var f = Factory();
        var seeded = await CompletedTaskSeed.SeedAsync(f);
        using var client = f.CreateClient(UserRole.Analyst);

        var one = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{seeded.TaskId}", Ct);
        Assert.Equal(JsonValueKind.Null, one.GetProperty("template").ValueKind);
    }
}

internal static class TemplateSeed
{
    public static readonly DateTimeOffset SealedAt = new(2026, 10, 24, 23, 30, 0, TimeSpan.Zero);

    /// <summary>Creates template win11-golden (circular FK order: template, version, then CurrentVersionId) and binds the task to v3.</summary>
    public static async Task BindAsync(MaldexAppFactory f, Guid taskId)
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();

        var user = new AppUser { Id = Guid.NewGuid(), EntraObjectId = "oid-" + Guid.NewGuid(), DisplayName = "basht0p", Role = UserRole.Admin };
        db.Users.Add(user);
        var template = new Template { Id = "win11-golden", DisplayName = "Win11 Golden", CreatedAt = SealedAt };
        db.Templates.Add(template);
        await db.SaveChangesAsync(ct);

        var version = new TemplateVersion
        {
            Id = Guid.NewGuid(), TemplateId = template.Id, Number = 3, Origin = TemplateVersionOrigin.Edit, FileStem = "win11-golden-v3",
            WindowsEdition = "Windows 11 Pro", WindowsBuild = "26300", AgentVersion = "1.0.0", OfficePresent = true,
            SealedByUserId = user.Id, SealedAt = SealedAt,
        };
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync(ct);

        template.CurrentVersionId = version.Id;
        var task = await db.Tasks.SingleAsync(t => t.Id == taskId, ct);
        task.TemplateVersionId = version.Id;
        await db.SaveChangesAsync(ct);
    }
}
