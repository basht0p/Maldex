using System.Text.Json.Serialization;
using Maldex.Core.Abstractions;
using Maldex.Data;
using Maldex.Infrastructure;
using Maldex.Orchestrator;
using Maldex.Processing;
using Maldex.Web;
using Maldex.Web.Auth;
using Maldex.Web.Endpoints;
using Maldex.Web.Hubs;
using Maldex.Web.Security;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddDbContext<MaldexDbContext>(o => o.UseSqlServer(
    builder.Configuration.GetConnectionString("Maldex")
    ?? throw new InvalidOperationException("ConnectionStrings:Maldex is not configured")));
builder.Services.AddMaldexInfrastructure(builder.Configuration);
builder.Services.Configure<WorkersOptions>(builder.Configuration.GetSection(WorkersOptions.SectionName));
builder.Services.Configure<SubmissionOptions>(builder.Configuration.GetSection(SubmissionOptions.SectionName));
builder.Services.PostConfigure<SubmissionOptions>(o => o.ApplyDefaults());
builder.Services.Configure<EgressOptions>(builder.Configuration.GetSection(EgressOptions.SectionName));
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit =
    new SubmissionOptions { MaxUploadBytes = builder.Configuration.GetValue<long?>("Maldex:MaxUploadBytes") ?? 268_435_456 }.MaxUploadBytes + 65536);
builder.AddMaldexAuth();

// Real-time surface: the hub carries live status/process/event pushes; enums go over the wire as strings, like the REST DTOs.
builder.Services.AddSignalR().AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton<ITaskEventPublisher, SignalRTaskEventPublisher>();
builder.Services.AddMaldexOrchestrator(builder.Configuration);
builder.Services.AddMaldexProcessing(builder.Configuration);
// The orchestrator and processing workers run inside the web host only when Maldex:Workers:Enabled (default true).
if ((builder.Configuration.GetSection(WorkersOptions.SectionName).Get<WorkersOptions>() ?? new WorkersOptions()).Enabled)
{
    builder.Services.AddHostedService(sp => sp.GetRequiredService<OrchestratorWorker>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ProcessingWorker>());
}

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
app.MapLiveControlEndpoints();
app.MapHub<TaskHub>("/hubs/tasks");

app.Run();

public partial class Program;
