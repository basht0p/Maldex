using System.Net.Http.Json;
using System.Text.Json;
using Maldex.Integration.Tests.Fixtures;
using Maldex.Orchestrator.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Maldex.Integration.Tests.E2E;

/// <summary>
/// Interactive acceptance of a Build from <c>MALDEX_WIN_ISO</c>, run by a person with <c>./build.sh e2e-build-acceptance</c>
/// (also gated by <c>MALDEX_E2E_INTERACTIVE=1</c>; never part of <c>e2e-templates</c>, so it never blocks the validation stamp).
/// It builds a new version of <c>win11-26h2</c> with publish on and serves the app on localhost:5080 (DevAuth, never another
/// interface). The person opens the printed job page through an SSH forward, turns off Tamper Protection and Defender in the
/// console and presses Seal; the version must publish as an ISO build with the bundle's agent, and the harmless scenarios
/// (EICAR, registry write, URL) must pass on it. Its disks are deleted afterwards; the hand-prepared image is never written.
/// </summary>
[Collection("sql")]
public sealed class RealTemplateBuildAcceptanceTests(SqlServerFixture sql, ITestOutputHelper output)
{
    private const int Port = 5080;
    /// <summary>The address the person forwards to; the host itself listens on 127.0.0.1 only.</summary>
    private const string SshForward = "ssh -L 5080:localhost:5080 maldex_admin@192.168.1.113";

    // 60 (fetch) + 210 (Configuring) + 120 (the person, ConfigureIdleTimeout) + 30 (seal, verify) + 60 (three analyses) min.
    [Fact(Timeout = 480 * 60 * 1000)]
    public async Task Build_Interactive_SealAndPublish()
    {
        RealBuildHarness.RequireRealStack(interactive: true);
        await new RealBuildHarness(sql, output).RunAsync(async run =>
        {
            var v1 = (await run.TemplateAsync(RealBuildHarness.BaseTemplateId)).GetProperty("current").GetProperty("id").GetGuid();
            var media = await run.FetchIsoAsync();
            var job = await run.StartBuildAsync(RealBuildHarness.BaseTemplateId, "Windows 11 26H2", media, publish: true);
            // DevAuth makes every caller an Admin: the app must listen on loopback only, never on the LAN.
            AssertListensOnLoopbackOnly(Port);
            var page = $"http://localhost:{Port}/admin/template-jobs/{job}";
            run.Progress($"the job page: {page} (forward first: {SshForward})");

            var configuring = await run.WaitForConfiguringAsync(job);
            var idle = run.Host.Services.GetRequiredService<IOptions<TemplateJobOptions>>().Value.ConfigureIdleTimeout;
            run.Progress($"READY. Open {page} through `{SshForward}`. In the console open Windows Security, turn off Tamper Protection, "
                + $"then Real-time protection, then press Seal. The job is discarded after {idle.TotalMinutes:0} min without a console.");
            Assert.Equal("Configuring", configuring.GetProperty("state").GetString());

            // The person's part: a refused seal (Defender still on) brings the job back to Configuring, and the wait goes on.
            var ended = await run.WaitForAsync(job, d => RealBuildHarness.IsTerminal(d.GetProperty("state").GetString()!), idle + TimeSpan.FromMinutes(30));
            Assert.True(ended.GetProperty("state").GetString() == "Published", $"the build ended {RealBuildHarness.Describe(ended)}");
            Assert.Equal(2, ended.GetProperty("resultVersionNumber").GetInt32());
            Assert.Equal(2, RealBuildHarness.JobFiles(job).Count); // the sealed version: <stem>.qcow2 and <stem>.vars.fd

            // Published implies the verification boot read real-time protection off (on fails the job, R7/R16); unreadable would add a note.
            Assert.DoesNotContain(TemplateVerifier.DefenderUnreadableNote, ended.GetProperty("message").GetString() ?? "", StringComparison.Ordinal);
            var template = await run.TemplateAsync(RealBuildHarness.BaseTemplateId);
            var v2 = template.GetProperty("current");
            Assert.Equal(job, v2.GetProperty("id").GetGuid()); // the version id is the job id
            Assert.Equal("IsoBuild", v2.GetProperty("origin").GetString());
            Assert.Equal((await run.BundleAsync()).AgentBuildId, v2.GetProperty("agentVersion").GetString());
            Assert.Contains(template.GetProperty("versions").EnumerateArray(), v => v.GetProperty("id").GetGuid() == v1);
            run.Progress($"v2: {v2.GetProperty("windowsEdition").GetString()} {v2.GetProperty("windowsBuild").GetString()}, agent {v2.GetProperty("agentVersion").GetString()}");

            // The harmless scenarios on the new version: each run binds to it at Preparing.
            var e2e = new RealVmE2ETests(sql, output);
            foreach (var (name, scenario) in new (string, Func<Task<Guid>>)[]
                     { ("EICAR", () => e2e.EicarAsync(run.Host)), ("regwrite", () => e2e.RegWriteAsync(run.Host)), ("URL", () => e2e.ControlledUrlAsync(run.Host)) })
            {
                run.Progress($"analysis {name} on v2 ...");
                var taskId = await scenario();
                await using var db = RealBuildHarness.Db(run.Host);
                Assert.Equal(job, await db.Tasks.AsNoTracking().Where(t => t.Id == taskId).Select(t => t.TemplateVersionId).SingleAsync(TestContext.Current.CancellationToken));
                run.Progress($"analysis {name} on v2 passed");
            }
        }, port: Port);
    }

    private static void AssertListensOnLoopbackOnly(int port)
    {
        var listeners = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
            .Where(e => e.Port == port).ToList();
        Assert.NotEmpty(listeners);
        Assert.All(listeners, e => Assert.True(System.Net.IPAddress.IsLoopback(e.Address),
            $"the acceptance app listens on {e} — it must be loopback only (DevAuth)"));
    }
}
