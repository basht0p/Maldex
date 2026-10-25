using System.Text.Json.Serialization;
using Maldex.Core.Abstractions;
using Maldex.Data;
using Maldex.Data.Templates;
using Maldex.Infrastructure;
using Maldex.Infrastructure.Real;
using Maldex.Orchestrator;
using Maldex.Orchestrator.Media;
using Maldex.Orchestrator.Templates;
using Maldex.Processing;
using Maldex.Web;
using Maldex.Web.Auth;
using Maldex.Web.Endpoints;
using Maldex.Web.Hubs;
using Maldex.Web.Reports;
using Maldex.Web.Security;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

// `dotnet run --project src/Maldex.Web -- --install-browsers` (./build.sh setup) installs the Chromium used for PDF reports.
if (args.Contains("--install-browsers"))
    Environment.Exit(Microsoft.Playwright.Program.Main(["install", "--with-deps", "chromium"]));

var builder = WebApplication.CreateBuilder(args);

// Real mode (Maldex__Mode=Real, as ./build.sh run sets it, or --Maldex:Mode=Real) layers appsettings.Real.json over
// appsettings.json / appsettings.{Environment}.json but under the unprefixed environment variables and the command line.
if (builder.Configuration["Maldex:Mode"] == "Real")
    RealSettings.Insert(builder.Configuration.Sources, builder.Environment.ContentRootFileProvider);

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddDbContext<MaldexDbContext>(o => o.UseSqlServer(
    builder.Configuration.GetConnectionString("Maldex")
    ?? throw new InvalidOperationException("ConnectionStrings:Maldex is not configured")));
builder.Services.AddMaldexInfrastructure(builder.Configuration);
builder.Services.Configure<WorkersOptions>(builder.Configuration.GetSection(WorkersOptions.SectionName));
builder.Services.Configure<SubmissionOptions>(builder.Configuration.GetSection(SubmissionOptions.SectionName));
builder.Services.PostConfigure<SubmissionOptions>(o => o.ApplyDefaults());
builder.Services.Configure<EgressOptions>(builder.Configuration.GetSection(EgressOptions.SectionName));
builder.Services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<Maldex.Web.Templates.TemplateAdminOptions>, Maldex.Web.Templates.TemplateAdminOptionsValidation>();
builder.Services.AddOptions<Maldex.Web.Templates.TemplateAdminOptions>().Bind(builder.Configuration.GetSection(Maldex.Web.Templates.TemplateAdminOptions.SectionName)).ValidateOnStart();
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit =
    new SubmissionOptions { MaxUploadBytes = builder.Configuration.GetValue<long?>("Maldex:MaxUploadBytes") ?? 268_435_456 }.MaxUploadBytes + 65536);
builder.AddMaldexAuth();
builder.Services.AddScoped<ReportModelBuilder>();
builder.Services.AddScoped<TaskReportBuilder>();
builder.Services.AddSingleton<ReportRenderer>();
builder.Services.AddSingleton<PdfRenderer>();

// Real-time surface: the hub carries live status/process/event pushes; enums go over the wire as strings, like the REST DTOs.
builder.Services.AddSignalR().AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton<ITaskEventPublisher, SignalRTaskEventPublisher>();
builder.Services.AddSingleton<ITemplateJobPublisher, SignalRTemplateJobPublisher>();
builder.Services.AddMaldexOrchestrator(builder.Configuration);
builder.Services.AddMaldexProcessing(builder.Configuration);
// The orchestrator and processing workers run inside the web host only when Maldex:Workers:Enabled (default true).
if ((builder.Configuration.GetSection(WorkersOptions.SectionName).Get<WorkersOptions>() ?? new WorkersOptions()).Enabled)
{
    // Real mode: the orchestrator only starts when the host guards pass (fail closed; the web app stays up either way).
    builder.Services.AddGatedHostedService<OrchestratorWorker>();
    // Template jobs run VMs too: same gate, and they start only after the orchestrator's startup reconcile.
    builder.Services.AddGatedHostedService<TemplateJobWorker>();
    // Media uploads, fetches and inspection: same gate (Real mode mounts images, with sudo).
    builder.Services.AddGatedHostedService<MediaWorker>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ProcessingWorker>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<RetentionWorker>());
}

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
    await db.Database.MigrateAsync();
    await TemplateImporter.ImportAsync(db, scope.ServiceProvider.GetRequiredService<ITemplateStore>(),
        scope.ServiceProvider.GetRequiredService<IOptions<SubmissionOptions>>().Value.Images,
        scope.ServiceProvider.GetRequiredService<TimeProvider>(),
        scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Maldex.TemplateImport"), CancellationToken.None);
    if (app.Environment.IsDevelopment()) await DevSeed.SeedAsync(db);
}

app.UseMiddleware<SecurityHeadersMiddleware>();
// The built React SPA is copied into wwwroot by ./build.sh build; it is served same-origin with the API.
app.UseStaticFiles();
app.UseWebSockets();
app.UseAuthentication();
app.UseMiddleware<CurrentUserMiddleware>();
app.UseAuthorization();

app.MapMeEndpoints();
app.MapOrgEndpoints();
app.MapStatusEndpoints();
app.MapTemplateEndpoints();
app.MapMediaEndpoints();
app.MapTaskEndpoints();
app.MapLiveControlEndpoints();
app.MapReportEndpoints();
app.MapTaskReportEndpoints();
app.MapArtifactEndpoints();
app.MapGuacamoleTunnelEndpoints();
app.MapHub<TaskHub>("/hubs/tasks");
// Client-side routes resolve to the SPA. Extension-less paths only (a missing .js stays a 404), and never /api or
// /hubs: those keep their own 404/401 behaviour instead of answering with index.html.
app.MapFallbackToFile("/", "index.html");
app.MapFallbackToFile(@"{*path:nonfile:regex(^(?!(api|hubs)(/|$)).*$)}", "index.html");

app.Run();

public partial class Program;
