using System.Text.Json.Serialization;
using Maldex.Data;
using Maldex.Infrastructure.Storage;
using Maldex.Core.Abstractions;
using Maldex.Web;
using Maldex.Web.Auth;
using Maldex.Web.Endpoints;
using Maldex.Web.Security;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddDbContext<MaldexDbContext>(o => o.UseSqlServer(
    builder.Configuration.GetConnectionString("Maldex")
    ?? throw new InvalidOperationException("ConnectionStrings:Maldex is not configured")));
builder.Services.Configure<ArtifactStoreOptions>(builder.Configuration.GetSection(ArtifactStoreOptions.SectionName));
if (!OperatingSystem.IsWindows()) builder.Services.AddSingleton<IArtifactStore, FileSystemArtifactStore>(); // Task 10 moves this into AddMaldexInfrastructure
builder.Services.Configure<WorkersOptions>(builder.Configuration.GetSection(WorkersOptions.SectionName));
builder.Services.Configure<SubmissionOptions>(builder.Configuration.GetSection(SubmissionOptions.SectionName));
builder.Services.PostConfigure<SubmissionOptions>(o => o.ApplyDefaults());
builder.Services.Configure<EgressOptions>(builder.Configuration.GetSection(EgressOptions.SectionName));
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit =
    new SubmissionOptions { MaxUploadBytes = builder.Configuration.GetValue<long?>("Maldex:MaxUploadBytes") ?? 268_435_456 }.MaxUploadBytes + 65536);
builder.AddMaldexAuth();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
    await db.Database.MigrateAsync();
    if (app.Environment.IsDevelopment()) await DevSeed.SeedAsync(db);
}

app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseAuthentication();
app.UseMiddleware<CurrentUserMiddleware>();
app.UseAuthorization();

app.MapMeEndpoints();
app.MapOrgEndpoints();
app.MapTaskEndpoints();

app.Run();

public partial class Program;
