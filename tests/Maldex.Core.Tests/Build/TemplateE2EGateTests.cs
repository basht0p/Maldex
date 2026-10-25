using System.Diagnostics;
using System.Runtime.Versioning;
using Xunit;

namespace Maldex.Core.Tests.Build;

/// <summary>
/// The template E2E gate in build.sh: <c>template_e2e_check</c> (exit 0 = a real template E2E is due, with the reason and the changed
/// files on stdout; exit 1 = the stamp covers HEAD) and <c>template_e2e_gate_notice</c> (what the end of <c>./build.sh e2e</c> prints).
/// Runs against a throw-away git repository with a small TEMPLATE_E2E_PATHS list.
/// </summary>
[SupportedOSPlatform("linux")]
public class TemplateE2EGateTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "mxgate-" + Guid.NewGuid().ToString("N"));

    public TemplateE2EGateTests()
    {
        Directory.CreateDirectory(_repo);
        Git("init", "-q", "-b", "main");
        Write("src/watched/a.cs", "a");
        Write("docs/other.md", "o");
        Commit("first");
    }

    public void Dispose() { try { Directory.Delete(_repo, true); } catch (IOException) { } }

    private void Write(string rel, string text)
    {
        var f = Path.Combine(_repo, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        File.WriteAllText(f, text);
    }

    private (int Code, string Out) Bash(string script, string? workDir = null)
    {
        var psi = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, WorkingDirectory = workDir ?? _repo };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(script);
        foreach (var (k, v) in new Dictionary<string, string>
                 {
                     ["GIT_AUTHOR_NAME"] = "t", ["GIT_AUTHOR_EMAIL"] = "t@example.invalid",
                     ["GIT_COMMITTER_NAME"] = "t", ["GIT_COMMITTER_EMAIL"] = "t@example.invalid",
                 })
            psi.Environment[k] = v;
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();   // never a TTY
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, stdout.Result + stderr.Result);
    }

    private string Git(params string[] args)
    {
        var (code, output) = Bash("git " + string.Join(' ', args.Select(a => $"'{a}'")));
        Assert.True(code == 0, output);
        return output.Trim();
    }

    private void Commit(string message) { Git("add", "-A"); Git("commit", "-q", "-m", message); }
    private string Head() => Git("rev-parse", "HEAD");
    private void Stamp(string text) => Write(".data/e2e-templates.validated", text + "\n");

    private static string BuildSh()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var f = Path.Combine(d.FullName, "build.sh");
            if (File.Exists(f)) return f;
        }
        throw new FileNotFoundException("build.sh");
    }

    private (int Code, string Out) InRepo(string call) =>
        Bash($"source '{BuildSh()}'; cd '{_repo}'; TEMPLATE_E2E_PATHS=(src/watched/ src/Single.cs 'src/Job*'); {call}");

    // ---- template_e2e_check -----------------------------------------------------------------------------------------

    [Fact]
    public void NoStamp_IsDue()
    {
        var (code, output) = InRepo("template_e2e_check");
        Assert.Equal(0, code);
        Assert.Contains("no validated", output);
    }

    [Fact]
    public void StampAtHead_IsUpToDate()
    {
        Stamp(Head());
        var (code, output) = InRepo("template_e2e_check");
        Assert.Equal(1, code);
        Assert.Equal("", output.Trim());
    }

    [Fact]
    public void ChangesOutsideTheWatchedPaths_AreIgnored()
    {
        Stamp(Head());
        Write("docs/other.md", "changed");
        Write("src/unwatched/x.cs", "x");
        Commit("docs only");
        Write("docs/another.md", "uncommitted");
        Assert.Equal(1, InRepo("template_e2e_check").Code);
    }

    [Fact]
    public void ACommittedChangeToAWatchedPath_IsDue_AndNamesTheFile()
    {
        Stamp(Head());
        Write("src/watched/a.cs", "changed");
        Write("src/JobThing.cs", "glob-matched");
        Commit("affecting");
        var (code, output) = InRepo("template_e2e_check");
        Assert.Equal(0, code);
        Assert.Contains("src/watched/a.cs", output);
        Assert.Contains("src/JobThing.cs", output);
        Assert.DoesNotContain("docs/other.md", output);
    }

    [Fact]
    public void AnUncommittedChangeToAWatchedPath_IsDue_AndNamesTheFile()
    {
        Stamp(Head());
        Write("src/watched/a.cs", "edited, not committed");
        var (code, output) = InRepo("template_e2e_check");
        Assert.Equal(0, code);
        Assert.Contains("src/watched/a.cs", output);
        Assert.Contains("uncommitted", output);
    }

    [Fact]
    public void AnUntrackedFileInAWatchedPath_IsDue()
    {
        Stamp(Head());
        Write("src/watched/new.cs", "new");
        var (code, output) = InRepo("template_e2e_check");
        Assert.Equal(0, code);
        Assert.Contains("src/watched/new.cs", output);
    }

    [Fact]
    public void AStampThatIsNotAnAncestorOfHead_IsDue()
    {
        Git("checkout", "-q", "-b", "other");
        Write("docs/other.md", "x");
        Commit("on other");
        var onOther = Head();
        Git("checkout", "-q", "main");
        Stamp(onOther);
        var (code, output) = InRepo("template_e2e_check");
        Assert.Equal(0, code);
        Assert.Contains("not an ancestor", output);
    }

    [Theory]
    [InlineData("not-a-commit")]
    [InlineData("0000000000000000000000000000000000000000")]
    [InlineData("")]
    public void AnUnusableStamp_IsDue(string text)
    {
        Stamp(text);
        var (code, output) = InRepo("template_e2e_check");
        Assert.Equal(0, code);
        Assert.Contains("ancestor", output);
    }

    // ---- template_e2e_gate_notice -----------------------------------------------------------------------------------

    [Fact]
    public void Notice_NonInteractive_OnlyPrints_AndNeverPrompts()
    {
        Stamp(Head());
        Write("src/watched/a.cs", "edited");
        var (code, output) = InRepo("template_e2e_gate_notice");
        Assert.Equal(0, code);
        Assert.Contains("src/watched/a.cs", output);
        Assert.Contains("./build.sh e2e-templates", output);
        Assert.DoesNotContain("[y/N]", output);
    }

    [Fact]
    public void Notice_WhenUpToDate_PrintsNothing()
    {
        Stamp(Head());
        var (code, output) = InRepo("template_e2e_gate_notice");
        Assert.Equal(0, code);
        Assert.Equal("", output.Trim());
    }

    // ---- the real list ----------------------------------------------------------------------------------------------

    [Fact]
    public void EveryRealWatchedPath_ExistsInTheRepository()
    {
        var root = Path.GetDirectoryName(BuildSh())!;
        var (code, output) = Bash($"source '{BuildSh()}'; cd '{root}'; for p in \"${{TEMPLATE_E2E_PATHS[@]}}\"; do compgen -G \"$p\" >/dev/null || echo \"missing: $p\"; done; echo count=${{#TEMPLATE_E2E_PATHS[@]}}", root);
        Assert.Equal(0, code);
        Assert.DoesNotContain("missing:", output);
        Assert.DoesNotContain("count=0", output);
    }

    /// <summary>The Build-from-ISO code is watched too: a change to it makes the real template E2E due.</summary>
    [Fact]
    public void TheRealWatchedPaths_CoverTheBuildFromIsoCode()
    {
        var root = Path.GetDirectoryName(BuildSh())!;
        var (code, output) = Bash($"source '{BuildSh()}'; printf '%s\\n' \"${{TEMPLATE_E2E_PATHS[@]}}\"", root);
        Assert.Equal(0, code);
        var paths = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var expected in new[]
                 {
                     "src/Maldex.Core/Media/", "src/Maldex.Infrastructure/Media/", "src/Maldex.Orchestrator/Media/",
                     "src/Maldex.Web/Endpoints/MediaEndpoints.cs", "deploy/guest/provision/", "deploy/guest/autounattend/",
                     "tests/Maldex.Integration.Tests/E2E/RealTemplateBuildTests.cs",
                 })
            Assert.Contains(expected, paths);
        // The whole provisioning directory replaces the single Sysmon config entry.
        Assert.DoesNotContain("deploy/guest/provision/sysmon-config.xml", paths);
    }

    // ---- the ISO requirement of the real Build targets ---------------------------------------------------------------

    /// <summary>The Build E2E would only skip without installer media, and a skip never writes the stamp: refuse up front.</summary>
    [Theory]
    [InlineData("cmd_e2e_templates", "")]
    [InlineData("cmd_e2e_templates", "/nonexistent/win11.iso")]
    [InlineData("cmd_e2e_build_acceptance", "")]
    [InlineData("cmd_e2e_build_acceptance", "/nonexistent/win11.iso")]
    public void TheRealBuildTargets_RefuseWithoutAnInstallerIso(string target, string iso)
    {
        // Everything else is in place (a stand-in base image), so the ISO is the only reason to refuse.
        Write(".data/vm/base/win11-26h2.qcow2", "");
        Write(".data/vm/base/win11-26h2.vars.fd", "");
        var (code, output) = Bash($"source '{BuildSh()}'; cd '{_repo}'; export MALDEX_WIN_ISO='{iso}'; {target}");
        Assert.Equal(2, code);
        Assert.Contains("MALDEX_WIN_ISO must point to", output);
        Assert.DoesNotContain("dotnet", output); // refused before anything was built or run
    }
}
