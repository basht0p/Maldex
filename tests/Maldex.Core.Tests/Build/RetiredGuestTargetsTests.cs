using System.Diagnostics;
using System.Runtime.Versioning;
using Xunit;

namespace Maldex.Core.Tests.Build;

/// <summary>
/// build-guest, prepare-guest, finalize-guest and discard-guest are retired (Build from ISO and Edit in the app replace them):
/// each prints a pointer and exits 2, the scripts are gone, nothing refers to them, and real_verify checks the Build's host tools.
/// </summary>
[SupportedOSPlatform("linux")]
public class RetiredGuestTargetsTests
{
    private static string Root
    {
        get
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "build.sh"))) return d.FullName;
            throw new FileNotFoundException("build.sh");
        }
    }

    private static string BuildSh => Path.Combine(Root, "build.sh");

    private static (int Code, string Out) Bash(string script, string workDir, string? path = null)
    {
        var psi = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, WorkingDirectory = workDir };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(script);
        if (path is not null) psi.Environment["PATH"] = path;
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, stdout.Result + stderr.Result);
    }

    [Theory]
    [InlineData("build-guest")]
    [InlineData("prepare-guest")]
    [InlineData("finalize-guest")]
    [InlineData("discard-guest")]
    public void ARetiredTarget_ExitsTwo_AndPointsToTheApp(string target)
    {
        var (code, output) = Bash($"'{BuildSh}' {target}", Root);
        Assert.Equal(2, code);
        Assert.Contains("retired: use Admin → Sandbox Templates → New template (build from media) or Edit", output);
    }

    [Fact]
    public void TheUsageLine_DoesNotListTheRetiredTargets()
    {
        var (code, output) = Bash($"'{BuildSh}' nonsense", Root);
        Assert.Equal(64, code);
        Assert.Contains("usage:", output);
        Assert.DoesNotContain("build-guest", output);
        Assert.DoesNotContain("prepare-guest", output);
    }

    [Theory]
    [InlineData("build-guest.sh")]
    [InlineData("prepare-guest.sh")]
    [InlineData("lib.sh")]
    public void TheGuestScripts_AreDeleted(string name) =>
        Assert.False(File.Exists(Path.Combine(Root, "deploy", "guest", name)), name);

    private static IEnumerable<(string Rel, string Text)> RepoFiles(params string[] entries)
    {
        foreach (var entry in entries)
        {
            var path = Path.Combine(Root, entry);
            var files = File.Exists(path) ? [path] : Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories);
            foreach (var f in files)
            {
                var rel = Path.GetRelativePath(Root, f);
                var parts = rel.Split(Path.DirectorySeparatorChar);
                if (parts.Intersect(["bin", "obj", "node_modules"]).Any()) continue;
                string text;
                try { text = File.ReadAllText(f); } catch (IOException) { continue; }
                yield return (rel, text);
            }
        }
    }

    [Fact]
    public void NoFileReferences_TheDeletedScripts()
    {
        var offenders = RepoFiles("README.md", "deploy", "src", "tests", "build.sh")
            .Where(f => !f.Rel.EndsWith("RetiredGuestTargetsTests.cs", StringComparison.Ordinal)
                && (f.Text.Contains("build-guest.sh", StringComparison.Ordinal) || f.Text.Contains("prepare-guest.sh", StringComparison.Ordinal)))
            .Select(f => f.Rel).ToList();
        Assert.Empty(offenders);
    }

    /// <summary>Operator-facing text never tells anyone to run a retired target (README.md, deploy/, src/, the E2E skip messages).</summary>
    [Fact]
    public void NoOperatorText_TellsToRunARetiredTarget()
    {
        var offenders = RepoFiles("README.md", "deploy", "src", "tests/Maldex.Integration.Tests")
            .Where(f => new[] { "build-guest", "prepare-guest", "finalize-guest", "discard-guest" }.Any(t => f.Text.Contains("./build.sh " + t, StringComparison.Ordinal)))
            .Select(f => f.Rel).ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void RealVerify_ReportsTheBuildsHostTools_WhenTheyAreMissing()
    {
        var empty = Path.Combine(Path.GetTempPath(), "mxrv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            // An empty PATH and an empty working directory: every check fails, and each of these must be among them.
            var (code, output) = Bash($"source '{BuildSh}'; cd '{empty}'; real_verify", empty, empty);
            Assert.NotEqual(0, code);
            foreach (var what in new[] { "FAIL wiminfo", "FAIL xorriso", "FAIL Sysmon.zip", "FAIL virtio-win.iso", "FAIL media library (.data/media)" })
                Assert.Contains(what, output);
        }
        finally { try { Directory.Delete(empty, true); } catch (IOException) { } }
    }
}
