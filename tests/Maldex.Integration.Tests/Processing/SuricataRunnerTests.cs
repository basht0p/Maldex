using Maldex.Core.Domain;
using Maldex.Infrastructure.Scanning;
using Microsoft.Extensions.Options;
using Xunit;

namespace Maldex.Integration.Tests.Processing;

public class SuricataRunnerTests
{
    [Fact]
    public async Task RunsOnSamplePcap_MatchesDevRule()
    {
        var repo = RepoRoot();
        var data = Path.Combine(repo, "tests", "Maldex.Integration.Tests", "TestData", "suricata");
        var options = new SuricataOptions
        {
            ConfigPath = Path.Combine(data, "suricata.yaml"),
            RulesPath = Path.Combine(repo, "deploy", "net", "suricata", "maldex-dev.rules"),
        };
        Assert.SkipUnless(BinaryAvailable(options.Binary), $"The '{options.Binary}' binary is not installed (apt install suricata); skipping.");

        var result = await new SuricataRunner(Options.Create(options))
            .RunAsync(Path.Combine(data, "dev-test.pcap"), TestContext.Current.CancellationToken);

        var d = Assert.Single(result.Alerts);
        Assert.Equal(DetectionSource.Suricata, d.Source);
        Assert.Equal("MALDEX TEST controlled URL", d.Rule);
        Assert.Equal(1, d.Severity);
        Assert.Equal("sid 9000001 rev 1", d.Details);
        Assert.Contains("10.213.0.2:49152", d.Target);

        // Real eve.json flow records (Suricata's own format) parse into flows.
        var f = Assert.Single(result.Flows, x => x.Src == "10.213.0.2" && x.DstPort == 80);
        Assert.Equal("TCP", f.Proto);
        Assert.True(f.Bytes > 0);
    }

    [Fact]
    public async Task MissingBinary_Throws()
    {
        var runner = new SuricataRunner(Options.Create(new SuricataOptions { Binary = "/nonexistent/suricata-xyz" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync("x.pcap", TestContext.Current.CancellationToken));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Maldex.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root (Maldex.slnx) not found");
    }

    private static bool BinaryAvailable(string binary)
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(binary, "-V")
            { RedirectStandardOutput = true, RedirectStandardError = true });
            p!.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }
}
