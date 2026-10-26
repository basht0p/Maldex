using System.Reflection;
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
using Maldex.Web.Templates;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

// `dotnet run --project src/Maldex.Web -- --install-browsers` (./build.sh setup) installs the Chromium used for PDF reports.
// `--no-deps` skips the OS-package step (RHEL: the package declares the libraries itself).
if (args.Contains("--install-browsers"))
    Environment.Exit(Microsoft.Playwright.Program.Main(args.Contains("--no-deps")
        ? ["install", "chromium"]
        : ["install", "--with-deps", "chromium"]));

// `Maldex.Web --version` prints the assembly informational version; it needs no configuration and no database.
if (args.Contains("--version"))
{
    var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0";
    Console.WriteLine(version);
    return 0;
}

var builder = WebApplication.CreateBuilder(args);

// Real mode (Maldex__Mode=Real, as ./build.sh run sets it, or --Maldex:Mode=Real) layers appsettings.Real.json over
// appsettings.json / appsettings.{Environment}.json but under the unprefixed environment variables and the command line.
// MALDEX_EXTRA_CONFIG (colon-separated JSON files: the packaged defaults, then /etc/maldex/appsettings.Production.json) layers
// over both, so a packaged install overrides appsettings.Real.json; they too sit under environment variables and the command line.
var extraAt = ExtraConfig.Add(builder.Configuration.Sources, Environment.GetEnvironmentVariable(ExtraConfig.EnvironmentVariable));
if (builder.Configuration["Maldex:Mode"] == "Real")
{
    if (extraAt >= 0) RealSettings.InsertAt(builder.Configuration.Sources, builder.Environment.ContentRootFileProvider, extraAt);
    else RealSettings.Insert(builder.Configuration.Sources, builder.Environment.ContentRootFileProvider);
}

// `Maldex.Web --print-paths` prints the content root, the web root and the JSON config files that were found, then exits 0
// (a packaged install sets ASPNETCORE_CONTENTROOT=/opt/maldex/app in its unit; without it the content root is the cwd).
if (args.Contains("--print-paths"))
{
    Console.WriteLine($"ContentRoot={builder.Environment.ContentRootPath}");
    Console.WriteLine($"WebRoot={builder.Environment.WebRootPath}");
    Console.WriteLine($"IndexHtml={(builder.Environment.WebRootFileProvider.GetFileInfo("index.html").Exists ? "present" : "missing")}");
    foreach (var json in builder.Configuration.Sources.OfType<Microsoft.Extensions.Configuration.Json.JsonConfigurationSource>())
    {
        var file = json.Path is null ? null : json.FileProvider?.GetFileInfo(json.Path);
        if (file is { Exists: true }) Console.WriteLine($"Config={file.PhysicalPath ?? json.Path}");
    }
    return 0;
}

// Fail fast on an unconfigured packaged install, with a hint instead of a stack trace, and with EX_CONFIG (78) so the unit's
// RestartPreventExitStatus=78 stops systemd from restarting it until the admin fixes the configuration.
if (builder.Environment.IsProduction() && string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("Maldex")))
{
    Console.Error.WriteLine(StartupChecks.MissingConnectionMessage);
    return StartupChecks.ConfigErrorExitCode;
}
if (builder.Environment.IsProduction() && builder.Configuration["Maldex:Mode"] == "Real"
    && StartupChecks.RealRootProblem(builder.Configuration["Maldex:Real:Root"]) is { } rootProblem)
{
    Console.Error.WriteLine(rootProblem);
    return StartupChecks.ConfigErrorExitCode;
}

// Sign-in configuration (Maldex:Auth): an unsupported mode, Dev outside Development/Testing, or no way to sign in at all.
if (AuthStartupValidation.Problem(builder.Configuration, builder.Environment) is { } authProblem)
{
    Console.Error.WriteLine(authProblem);
    return StartupChecks.ConfigErrorExitCode;
}

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddDbContext<MaldexDbContext>(o => o.UseSqlServer(
    builder.Configuration.GetConnectionString("Maldex")
    ?? throw new InvalidOperationException("ConnectionStrings:Maldex is not configured")));
builder.Services.AddMaldexInfrastructure(builder.Configuration);
builder.Services.Configure<WorkersOptions>(builder.Configuration.GetSection(WorkersOptions.SectionName));
builder.Services.Configure<SubmissionOptions>(builder.Configuration.GetSection(SubmissionOptions.SectionName));
builder.Services.PostConfigure<SubmissionOptions>(o => o.ApplyDefaults());
builder.Services.Configure<EgressOptions>(builder.Configuration.GetSection(EgressOptions.SectionName));
builder.Services.AddSingleton<IValidateOptions<TemplateAdminOptions>, TemplateAdminOptionsValidation>();
builder.Services.AddOptions<TemplateAdminOptions>().Bind(builder.Configuration.GetSection(TemplateAdminOptions.SectionName)).ValidateOnStart();
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
    if (app.Environment.IsDevelopment()) await DevSeed.SeedAsync(scope.ServiceProvider);
}

// A TLS proxy on this host (the app listens on loopback) sets the scheme and host the browser used, and the client address.
// Only loopback peers are trusted (the KnownProxies / KnownNetworks defaults).
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedFor,
});
app.UseMiddleware<SecurityHeadersMiddleware>();
// The built React SPA is copied into wwwroot by ./build.sh build; it is served same-origin with the API.
app.UseStaticFiles();
app.UseWebSockets();
app.UseMiddleware<CsrfMiddleware>();
app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<CurrentUserMiddleware>();
app.UseAuthorization();

app.MapMaldexAuthEndpoints();
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
return 0;

public partial class Program;
