using System.Diagnostics;
using System.Runtime.Versioning;
using Xunit;

namespace Maldex.Core.Tests.Net;

/// <summary>
/// Runs deploy/host/maldex-docker-user.sh against a fake iptables (one rule per line in a file, exact-argument -C/-I/-D) and a fake ip.
/// </summary>
[SupportedOSPlatform("linux")]
public class DockerUserScriptTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "mxdu-" + Guid.NewGuid().ToString("N"));
    private string Rules => Path.Combine(_tmp, "rules");
    private string State => Path.Combine(_tmp, "state");

    public DockerUserScriptTests()
    {
        Directory.CreateDirectory(_tmp);
        File.WriteAllText(Rules, "");
        Fake("iptables", string.Join('\n',
            "#!/bin/sh",
            "[ \"$1\" = -n ] && exit 0",
            "op=$1; shift; [ \"$1\" = DOCKER-USER ] && shift",
            "line=\"$*\"",
            "case \"$op\" in",
            "  -S) while IFS= read -r r; do printf '%s\\n' \"-A DOCKER-USER $r\"; done < \"$RULES\" ;;",
            "  -C) grep -qxF -- \"$line\" \"$RULES\" ;;",
            "  -I) printf '%s\\n' \"$line\" >> \"$RULES\" ;;",
            "  -D) grep -qxF -- \"$line\" \"$RULES\" || exit 1; grep -vxF -- \"$line\" \"$RULES\" > \"$RULES.new\" || true; mv \"$RULES.new\" \"$RULES\" ;;",
            "  *) exit 2 ;;",
            "esac"));
        Fake("ip", "#!/bin/sh\necho \"default via 10.0.0.1 dev ${FAKE_IFACE} proto dhcp metric 100\"");
    }

    public void Dispose() { try { Directory.Delete(_tmp, true); } catch (IOException) { } }

    private void Fake(string name, string body)
    {
        var path = Path.Combine(_tmp, name);
        File.WriteAllText(path, body + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private int Run(string iface, string? extraRule = null, string? arg = null)
    {
        if (extraRule is not null) File.AppendAllText(Rules, extraRule + "\n");
        var psi = new ProcessStartInfo("sh") { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(FindScript());
        if (arg is not null) psi.ArgumentList.Add(arg);
        psi.Environment["PATH"] = _tmp + ":" + Environment.GetEnvironmentVariable("PATH");
        psi.Environment["RULES"] = Rules;
        psi.Environment["FAKE_IFACE"] = iface;
        psi.Environment["MALDEX_DOCKER_USER_STATE"] = State;
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }

    private string[] Current() => File.ReadAllLines(Rules);

    private static string FindScript()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var f = Path.Combine(d.FullName, "deploy", "host", "maldex-docker-user.sh");
            if (File.Exists(f)) return f;
        }
        throw new FileNotFoundException("deploy/host/maldex-docker-user.sh");
    }

    private const string Out = "-i mxu+ -o {0} -m comment --comment maldex-direct -j ACCEPT";
    private const string Back = "-i {0} -o mxu+ -m conntrack --ctstate RELATED,ESTABLISHED -m comment --comment maldex-direct -j ACCEPT";

    [Fact]
    public void AddsExactlyTheTwoApprovedRules_AndIsIdempotent()
    {
        Assert.Equal(0, Run("eth0"));
        Assert.Equal(0, Run("eth0"));
        Assert.Equal(new[] { string.Format(Out, "eth0"), string.Format(Back, "eth0") }.Order(), Current().Order());
    }

    [Fact]
    public void WhenTheDefaultRouteMoves_TheOldInterfacesTwoRulesAreRemoved_AndOthersUntouched()
    {
        var foreign = "-s 1.2.3.4 -j DROP -m comment --comment other-maldex-direct-lookalike";
        Assert.Equal(0, Run("eth0", foreign));
        Assert.Equal(0, Run("wlan0"));
        Assert.Equal(new[] { foreign, string.Format(Out, "wlan0"), string.Format(Back, "wlan0") }.Order(), Current().Order());
    }

    [Fact]
    public void ALookalikeRule_IsNeitherRelied_NorTouched()
    {
        // Same comment, different action: not one of the approved rules, so the real ones are still added and this one is left alone.
        var lookalike = "-i mxu+ -o eth0 -m comment --comment maldex-direct -j DROP";
        Assert.Equal(0, Run("eth0", lookalike));
        var rules = Current();
        Assert.Contains(lookalike, rules);
        Assert.Contains(string.Format(Out, "eth0"), rules);
        Assert.Contains(string.Format(Back, "eth0"), rules);
    }

    [Theory]
    [InlineData("mxu1")]
    [InlineData("lo")]
    [InlineData("bad;name")]
    public void UnusableInterface_ChangesNothing_AndFails(string iface)
    {
        Assert.NotEqual(0, Run(iface));
        Assert.Empty(Current());
    }

    [Fact]
    public void Stop_RemovesExactlyTheTwoMaldexDirectRules_AndTheStateFile()
    {
        var foreign = "-s 1.2.3.4 -j DROP -m comment --comment other-maldex-direct-lookalike";
        var lookalike = "-i mxu+ -o eth0 -m comment --comment maldex-direct -j DROP";
        Assert.Equal(0, Run("eth0", foreign));
        File.AppendAllText(Rules, lookalike + "\n");
        Assert.Equal(0, Run("eth0", arg: "stop"));
        Assert.Equal(new[] { foreign, lookalike }.Order(), Current().Order());
        Assert.False(File.Exists(State));
    }

    [Fact]
    public void Stop_WithoutAStateFile_StillRemovesTheStaleRulesOfAnotherInterface()
    {
        Assert.Equal(0, Run("eth0"));
        File.Delete(State);
        Assert.Equal(0, Run("wlan0", arg: "stop"));
        Assert.Empty(Current());
    }

    [Fact]
    public void Stop_WithNothingToRemove_Succeeds()
    {
        Assert.Equal(0, Run("eth0", arg: "stop"));
        Assert.Empty(Current());
    }

    [Fact]
    public void Start_WithoutAStateFile_RemovesStaleRulesOfAnotherInterface_ByExactTokens()
    {
        var foreign = "-s 1.2.3.4 -j DROP -m comment --comment maldex-direct";
        var lookalike = "-i mxu+ -o eth0 -m comment --comment maldex-direct -j DROP";
        var padded = "-i mxu+ -o eth0 -m comment --comment maldex-direct-x -j ACCEPT";
        File.AppendAllText(Rules, string.Join('\n', foreign, lookalike, padded, string.Format(Out, "eth0"), string.Format(Back, "eth0")) + "\n");
        Assert.Equal(0, Run("wlan0"));
        Assert.Equal(new[] { foreign, lookalike, padded, string.Format(Out, "wlan0"), string.Format(Back, "wlan0") }.Order(), Current().Order());
    }

    [Fact]
    public void ScriptHasNoEval()
    {
        var code = string.Join('\n', File.ReadAllLines(FindScript()).Where(l => !l.TrimStart().StartsWith('#')));
        Assert.DoesNotContain("eval", code);
    }
}
