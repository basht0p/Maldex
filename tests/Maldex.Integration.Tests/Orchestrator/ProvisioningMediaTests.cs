using System.IO.Compression;
using System.Xml.Linq;
using Maldex.Infrastructure.Execution;
using Maldex.Infrastructure.Real;
using Maldex.Orchestrator.Templates;
using Maldex.Orchestrator.Templates.Build;
using Microsoft.Extensions.Options;
using Xunit;

namespace Maldex.Integration.Tests.Orchestrator;

public sealed class AutounattendRendererTests
{
    private static readonly string Template = File.ReadAllText(Path.Combine(RealPaths.FindRoot(AppContext.BaseDirectory), "deploy/guest/autounattend/Autounattend.xml"));
    private static readonly XNamespace U = "urn:schemas-microsoft-com:unattend";

    private static XElement MetaData(string xml) => XDocument.Parse(xml).Descendants(U + "InstallFrom").Single().Element(U + "MetaData")!;

    [Fact]
    public void Render_SetsTheImageIndex()
    {
        var md = MetaData(AutounattendRenderer.Render(Template, 6, null));
        Assert.Equal("/IMAGE/INDEX", md.Element(U + "Key")!.Value);
        Assert.Equal("6", md.Element(U + "Value")!.Value);
    }

    [Fact]
    public void Render_InsertsTheKeyBeforeWillShowUI_ForNonEvaluationMedia()
    {
        var pk = XDocument.Parse(AutounattendRenderer.Render(Template, 1, "W269N-WFGWX-YVC9B-4J6C9-T83GX")).Descendants(U + "UserData").Single().Element(U + "ProductKey")!;
        Assert.Equal(["Key", "WillShowUI"], pk.Elements().Select(e => e.Name.LocalName).ToArray());
        Assert.Equal("W269N-WFGWX-YVC9B-4J6C9-T83GX", pk.Element(U + "Key")!.Value);
    }

    [Fact]
    public void Render_AddsNoKey_ForEvaluationMedia()
    {
        var pk = XDocument.Parse(AutounattendRenderer.Render(Template, 1, null)).Descendants(U + "UserData").Single().Element(U + "ProductKey")!;
        Assert.Null(pk.Element(U + "Key"));
    }

    [Fact]
    public void Render_RemovesTheMarkerComments_AndStaysWellFormed()
    {
        var xml = AutounattendRenderer.Render(Template, 2, "NPPR9-FWDCX-D2C8J-H872K-2YT43");
        Assert.DoesNotContain("MX-IMAGE-", xml);
        Assert.StartsWith("<?xml", xml);
        XDocument.Parse(xml); // well-formed
    }
}

public sealed class ProvisioningMediaTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "maldex-provmedia-" + Guid.NewGuid().ToString("N"));
    private readonly string _root = RealPaths.FindRoot(AppContext.BaseDirectory);
    private readonly BuildOptions _options;

    public ProvisioningMediaTests()
    {
        Directory.CreateDirectory(_dir);
        _options = new BuildOptions
        {
            SysmonZipPath = Path.Combine(_dir, "Sysmon.zip"),
            AutounattendPath = Path.Combine(_root, "deploy/guest/autounattend/Autounattend.xml"),
            ProvisionScriptPath = Path.Combine(_root, "deploy/guest/provision/Provision.ps1"),
            OfficeConfigPath = Path.Combine(_root, "deploy/guest/provision/office-config.xml"),
        };
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (DirectoryNotFoundException) { } }

    private void WriteSysmonZip(byte[] sysmon)
    {
        using var zip = ZipFile.Open(_options.SysmonZipPath, ZipArchiveMode.Create);
        using var s = zip.CreateEntry("Sysmon64.exe").Open();
        s.Write(sysmon);
    }

    private sealed class CapturingRunner(int exitCode = 0) : IProcessRunner
    {
        public string? File { get; private set; }
        public IReadOnlyList<string>? Args { get; private set; }
        public Dictionary<string, byte[]> StageSnapshot { get; } = [];

        public Task<ProcessResult> RunAsync(string file, IReadOnlyList<string> args, CancellationToken ct, string? stdin = null, TimeSpan? timeout = null)
        {
            File = file; Args = args;
            // The stage exists only while xorriso runs: capture it now.
            foreach (var f in Directory.GetFiles(args[^1])) StageSnapshot[Path.GetFileName(f)] = System.IO.File.ReadAllBytes(f);
            return Task.FromResult(new ProcessResult(exitCode, "", exitCode == 0 ? "" : "boom"));
        }
    }

    private XorrisoProvisioningMediaBuilder Real(CapturingRunner runner) => new(runner, Options.Create(_options));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Xorriso_StagesExactlyTheExpectedFiles_AndRemovesTheStage(bool office)
    {
        var bundle = TemplateJobKit.MakeBundle();
        var sysmon = new byte[] { 9, 8, 7, 6 };
        WriteSysmonZip(sysmon);
        var runner = new CapturingRunner();
        var work = Path.Combine(_dir, "work");

        var iso = await Real(runner).BuildAsync(new ProvisioningInputs(bundle, 3, "W269N-WFGWX-YVC9B-4J6C9-T83GX", office), work, CancellationToken.None);

        Assert.Equal(Path.Combine(work, "provision.iso"), iso);
        var expected = new List<string> { "Autounattend.xml", "Provision.ps1", "sysmon-config.xml", "Maldex.Agent.exe", "maldex-ca.cer", "Sysmon64.exe" };
        if (office) expected.Add("office-config.xml");
        Assert.Equal(expected.Order(StringComparer.Ordinal), runner.StageSnapshot.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(bundle.AgentExe, runner.StageSnapshot["Maldex.Agent.exe"]);
        Assert.Equal(bundle.CaDer, runner.StageSnapshot["maldex-ca.cer"]);
        Assert.Equal(bundle.SysmonConfig, runner.StageSnapshot["sysmon-config.xml"]);
        Assert.Equal(sysmon, runner.StageSnapshot["Sysmon64.exe"]);
        Assert.Contains("/IMAGE/INDEX", System.Text.Encoding.UTF8.GetString(runner.StageSnapshot["Autounattend.xml"]));
        Assert.Equal(File.ReadAllBytes(_options.ProvisionScriptPath), runner.StageSnapshot["Provision.ps1"]);
        Assert.False(Directory.Exists(Path.Combine(work, "stage")), "the stage is deleted after the ISO is written");
    }

    [Fact]
    public async Task Xorriso_RunsMkisofsWithTheMaldexProvLabel()
    {
        WriteSysmonZip([1]);
        var runner = new CapturingRunner();
        var work = Path.Combine(_dir, "work");

        var iso = await Real(runner).BuildAsync(new ProvisioningInputs(TemplateJobKit.MakeBundle(), 1, null, false), work, CancellationToken.None);

        Assert.Equal("xorriso", runner.File);
        Assert.Equal(["-as", "mkisofs", "-quiet", "-J", "-R", "-V", "MALDEXPROV", "-o", iso, Path.Combine(work, "stage")], runner.Args);
    }

    [Fact]
    public async Task Xorriso_FailureNamesTheExitCode_AndStillRemovesTheStage()
    {
        WriteSysmonZip([1]);
        var work = Path.Combine(_dir, "work");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Real(new CapturingRunner(exitCode: 5)).BuildAsync(new ProvisioningInputs(TemplateJobKit.MakeBundle(), 1, null, false), work, CancellationToken.None));

        Assert.Contains("exit code 5", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(work, "stage")));
    }

    [Fact]
    public async Task MissingSysmonZip_NamesTheFix()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Real(new CapturingRunner()).BuildAsync(new ProvisioningInputs(TemplateJobKit.MakeBundle(), 1, null, false), Path.Combine(_dir, "work"), CancellationToken.None));

        Assert.Contains("./build.sh real-setup", ex.Message);
    }

    [Fact]
    public async Task Fake_WritesTheStage_AndAnEmptyIso_WithoutSysmonZip()
    {
        var work = Path.Combine(_dir, "work");
        var iso = await new FakeProvisioningMediaBuilder(Options.Create(_options))
            .BuildAsync(new ProvisioningInputs(TemplateJobKit.MakeBundle(), 1, null, true), work, CancellationToken.None);

        Assert.Equal(0, new FileInfo(iso).Length);
        var names = Directory.GetFiles(Path.Combine(work, "stage")).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["Autounattend.xml", "Maldex.Agent.exe", "Provision.ps1", "Sysmon64.exe", "maldex-ca.cer", "office-config.xml", "sysmon-config.xml"], names);
    }
}
