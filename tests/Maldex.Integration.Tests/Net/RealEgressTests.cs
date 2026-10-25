using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Maldex.Infrastructure.Execution;
using Maldex.Infrastructure.Net;
using Microsoft.Extensions.Options;
using Xunit;

namespace Maldex.Integration.Tests.Net;

/// <summary>
/// Gated: needs MALDEX_REAL_NET=1, passwordless sudo and the real-mode toolchain (nft, dnsmasq, mitmdump, inetsim, curl).
/// A scratch "guest stand-in" namespace is attached to the run bridge by a veth (playing the tap) to probe from the guest side.
/// Only interfaces/namespaces named mx* are created or removed.
/// </summary>
public class RealEgressTests
{
    private static readonly ProcessRunner Proc = new();

    [Fact]
    public Task CreateReleaseRealNamespace_None() => Exercise(NetworkMode.None);

    [Fact]
    public Task CreateReleaseRealNamespace_Simulated() => Exercise(NetworkMode.Simulated);

    /// <summary>
    /// Two Simulated runs at once (each INetSim has its own pid file), then a SIGKILLed INetSim main in one of them must not
    /// block the next Simulated run.
    /// </summary>
    [Fact]
    public async Task ConcurrentSimulatedRuns_EachHaveTheirOwnInetSim()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MALDEX_REAL_NET") == "1" && OperatingSystem.IsLinux(),
            "set MALDEX_REAL_NET=1 (needs passwordless sudo, nft, dnsmasq, mitmdump, inetsim)");
        var tmp = Path.Combine(Path.GetTempPath(), "mx-real-net-" + Guid.NewGuid().ToString("N"));
        var opts = Options.Create(new NetOptions
        {
            RunDir = Path.Combine(tmp, "run"),
            MitmConfDir = Path.Combine(tmp, "ca"),
            DeployNetDir = RepoDeployNet(),
            ControlPlaneIps = ["192.0.2.1"],
        });
        await Proc.RunAsync("timeout", ["8", "/opt/mitmproxy/mitmdump", "--set", $"confdir={Path.Combine(tmp, "ca")}", "-n", "-q"], CancellationToken.None, null, TimeSpan.FromSeconds(20));
        var router = new NetnsEgressRouter(opts, Proc);
        Guid[] ids = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        try
        {
            var leases = await Task.WhenAll(ids[..2].Select(id => router.CreateAsync(id, NetworkMode.Simulated, null, CancellationToken.None)));
            var mains = new List<string>();
            foreach (var id in ids[..2])
            {
                var ns = RunNetNames.For(id).Namespace;
                mains.Add(await AssertInetSimSandboxAsync("/run/maldex/inetsim/" + ns, ns));
                var code = (await Sudo("ip", "netns", "exec", ns, "curl", "-s", "-o", "/dev/null", "-m", "5", "-w", "%{http_code}", "http://198.18.0.10/")).Stdout.Trim();
                Assert.Contains(code, new[] { "200", "404" });
            }
            Assert.NotEqual(mains[0], mains[1]);

            // SIGKILL the first run's INetSim main (no clean-up of its pid file), release it, start a third Simulated run
            Assert.Equal(0, (await Sudo("kill", "-KILL", mains[0])).ExitCode);
            await router.ReleaseAsync(leases[0], CancellationToken.None);
            await router.CreateAsync(ids[2], NetworkMode.Simulated, null, CancellationToken.None);
            var ns3 = RunNetNames.For(ids[2]).Namespace;
            await AssertInetSimSandboxAsync("/run/maldex/inetsim/" + ns3, ns3);
            // the second run is still serving
            var ns2 = RunNetNames.For(ids[1]).Namespace;
            Assert.Contains((await Sudo("ip", "netns", "exec", ns2, "curl", "-s", "-o", "/dev/null", "-m", "5", "-w", "%{http_code}", "http://198.18.0.10/")).Stdout.Trim(), new[] { "200", "404" });
        }
        finally
        {
            foreach (var id in ids) await router.ReleaseAsync(new NetworkLease(id, RunNetNames.For(id).Tap), CancellationToken.None);
            await Sudo("rm", "-rf", tmp);
        }
        foreach (var id in ids)
        {
            var h = id.ToString("N")[..12];
            Assert.DoesNotContain(h, (await Proc.RunAsync("ip", ["-o", "link"], CancellationToken.None)).Stdout);
            Assert.DoesNotContain(h, (await Proc.RunAsync("ip", ["netns", "list"], CancellationToken.None)).Stdout);
            Assert.False(Directory.Exists("/run/maldex/inetsim/mxn" + h));
            Assert.False(Directory.Exists("/run/maldex/mitm/mxn" + h));
            Assert.False(Directory.Exists("/run/maldex/dns/mxn" + h));
        }
    }

    /// <summary>
    /// Each Simulated run's INetSim lives in its own PID namespace with a private /proc: from inside run A only A's own
    /// processes are visible (never B's), and after release no process of either run survives.
    /// </summary>
    [Fact]
    public async Task ConcurrentSimulatedRuns_HavePrivatePidNamespaces_AndLeaveNoProcessesBehind()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MALDEX_REAL_NET") == "1" && OperatingSystem.IsLinux(),
            "set MALDEX_REAL_NET=1 (needs passwordless sudo, nft, dnsmasq, mitmdump, inetsim)");
        var tmp = Path.Combine(Path.GetTempPath(), "mx-real-net-" + Guid.NewGuid().ToString("N"));
        var opts = Options.Create(new NetOptions
        {
            RunDir = Path.Combine(tmp, "run"),
            MitmConfDir = Path.Combine(tmp, "ca"),
            DeployNetDir = RepoDeployNet(),
            ControlPlaneIps = ["192.0.2.1"],
        });
        await Proc.RunAsync("timeout", ["8", "/opt/mitmproxy/mitmdump", "--set", $"confdir={Path.Combine(tmp, "ca")}", "-n", "-q"], CancellationToken.None, null, TimeSpan.FromSeconds(20));
        var router = new NetnsEgressRouter(opts, Proc);
        Guid[] ids = [Guid.NewGuid(), Guid.NewGuid()];
        var everyPid = new List<string>();
        try
        {
            await Task.WhenAll(ids.Select(id => router.CreateAsync(id, NetworkMode.Simulated, null, CancellationToken.None)));
            var hostPids = new List<string>();
            var nsLinks = new List<string>();
            var members = new List<Dictionary<string, string>>(); // per run: host pid -> in-namespace pid, for the run's pid namespace
            foreach (var id in ids)
            {
                var ns = RunNetNames.For(id).Namespace;
                var main = await AssertInetSimSandboxAsync("/run/maldex/inetsim/" + ns, ns);
                hostPids.Add(main);
                var link = (await Sudo("readlink", $"/proc/{main}/ns/pid")).Stdout.Trim();
                nsLinks.Add(link);
                var m = new Dictionary<string, string>();
                var all = (await Sudo("sh", "-c", "for p in /proc/[0-9]*; do echo \"${p#/proc/} $(readlink $p/ns/pid) $(grep '^NSpid:' $p/status)\"; done")).Stdout;
                foreach (var l in all.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var f = l.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                    if (f.Length >= 4 && f[1] == link) m[f[0]] = f[^1];
                }
                Assert.True(m.Count >= 3, "launcher (PID 1) + INetSim main + service children expected in the namespace");
                Assert.Equal("1", m.Values.Min(v => int.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToString(System.Globalization.CultureInfo.InvariantCulture)); // the launcher is init
                members.Add(m);
                everyPid.AddRange(m.Keys);
            }
            Assert.NotEqual(nsLinks[0], nsLinks[1]);
            Assert.Empty(members[0].Keys.Intersect(members[1].Keys));
            for (var i = 0; i < 2; i++)
            {
                // the view from inside run i (its pid namespace + its mount namespace with the private /proc)
                var view = await Sudo("nsenter", "-t", hostPids[i], "-p", "-m", "--", "ls", "/proc");
                Assert.Equal(0, view.ExitCode);
                var seen = view.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(e => e.All(char.IsAsciiDigit)).ToHashSet();
                var own = members[i].Values.ToHashSet();
                Assert.True(own.IsSubsetOf(seen), $"run {i}: own [{string.Join(',', own)}] not all in its /proc [{string.Join(',', seen)}]");
                Assert.True(seen.Count <= own.Count + 1, $"run {i} sees {seen.Count} processes but owns {own.Count} (+ the ls itself): {string.Join(',', seen)}");
            }
        }
        finally
        {
            foreach (var id in ids) await router.ReleaseAsync(new NetworkLease(id, RunNetNames.For(id).Tap), CancellationToken.None);
            await Sudo("rm", "-rf", tmp);
        }
        for (var i = 0; i < 40 && everyPid.Any(p => Directory.Exists("/proc/" + p)); i++) await Task.Delay(100, CancellationToken.None);
        Assert.False(everyPid.Any(p => Directory.Exists("/proc/" + p)), "a process of a released run survived: " + string.Join(',', everyPid.Where(p => Directory.Exists("/proc/" + p))));
    }

    /// <summary>
    /// DHCP per run: two consecutive runs on the same subnet index with different task ids, plus a "retry attempt" (a fresh
    /// NIC with the same deterministic MAC) inside the first run, all get the run's guest address. Each run keeps its own
    /// lease file; the host-wide /var/lib/misc/dnsmasq.leases is never touched. Guest stand-ins run busybox udhcpc.
    /// </summary>
    [Fact]
    public async Task ConsecutiveRuns_SameSlot_EachGuestGetsTheLease()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MALDEX_REAL_NET") == "1" && OperatingSystem.IsLinux(),
            "set MALDEX_REAL_NET=1 (needs passwordless sudo, nft, dnsmasq, mitmdump, busybox)");
        var tmp = Path.Combine(Path.GetTempPath(), "mx-real-net-" + Guid.NewGuid().ToString("N"));
        var opts = Options.Create(new NetOptions
        {
            RunDir = Path.Combine(tmp, "run"),
            MitmConfDir = Path.Combine(tmp, "ca"),
            DeployNetDir = RepoDeployNet(),
            ControlPlaneIps = ["192.0.2.1"],
        });
        await Proc.RunAsync("timeout", ["8", "/opt/mitmproxy/mitmdump", "--set", $"confdir={Path.Combine(tmp, "ca")}", "-n", "-q"], CancellationToken.None, null, TimeSpan.FromSeconds(20));
        var router = new NetnsEgressRouter(opts, Proc);
        var sharedBefore = (await Sudo("sh", "-c", "cat /var/lib/misc/dnsmasq.leases 2>/dev/null || true")).Stdout;
        Guid[] ids = [Guid.NewGuid(), Guid.NewGuid()];
        var guestIp = RunSubnet.For(0).GuestIp;
        var scratch = new List<(string Ns, string HostLeg)>();
        try
        {
            foreach (var id in ids)
            {
                var lease = await router.CreateAsync(id, NetworkMode.None, null, CancellationToken.None);
                var n = RunNetNames.For(id);
                await AssertDnsmasqUnprivilegedAsync(id, n.Namespace);
                // run 1 gets two VM "attempts" (the second NIC reappears with the same MAC while the first lease is still held)
                for (var attempt = 0; attempt < (id == ids[0] ? 2 : 1); attempt++)
                {
                    var tag = $"{id.ToString("N")[..10]}{attempt}";
                    var sns = "mxs" + tag;
                    var hostLeg = "mxq" + tag;
                    scratch.Add((sns, hostLeg));
                    Assert.Equal(0, (await Sudo("ip", "netns", "add", sns)).ExitCode);
                    Assert.Equal(0, (await Sudo("ip", "link", "add", hostLeg, "type", "veth", "peer", "name", "mxe" + tag, "netns", sns)).ExitCode);
                    Assert.Equal(0, (await Sudo("ip", "netns", "exec", sns, "ip", "link", "set", "mxe" + tag, "address", RunNetNames.GuestMac(id))).ExitCode);
                    Assert.Equal(0, (await Sudo("ip", "link", "set", hostLeg, "master", n.Bridge)).ExitCode);
                    Assert.Equal(0, (await Sudo("ip", "link", "set", hostLeg, "up")).ExitCode);
                    Assert.Equal(0, (await Sudo("ip", "netns", "exec", sns, "ip", "link", "set", "mxe" + tag, "up")).ExitCode);
                    // -q: obtain one lease and exit; -n: fail instead of retrying forever; no-op script (nothing is configured)
                    var dhcp = await Sudo("ip", "netns", "exec", sns, "busybox", "udhcpc", "-i", "mxe" + tag, "-q", "-n", "-t", "4", "-T", "2", "-s", "/bin/true");
                    var leases = await Sudo("cat", NetnsEgressRouter.DnsDir(id) + "/dnsmasq.leases");
                    Assert.True(dhcp.ExitCode == 0 && (dhcp.Stderr + dhcp.Stdout).Contains($"lease of {guestIp} obtained", StringComparison.Ordinal),
                        $"run {Array.IndexOf(ids, id)} attempt {attempt}: no lease ({dhcp.ExitCode}) {dhcp.Stdout} {dhcp.Stderr}");
                    Assert.Contains(RunNetNames.GuestMac(id), leases.Stdout); // the run's own lease database
                    await Sudo("ip", "link", "del", hostLeg);
                    await Sudo("ip", "netns", "del", sns);
                }
                await router.ReleaseAsync(lease, CancellationToken.None);
            }
            Assert.Equal(sharedBefore, (await Sudo("sh", "-c", "cat /var/lib/misc/dnsmasq.leases 2>/dev/null || true")).Stdout);
        }
        finally
        {
            foreach (var (sns, hostLeg) in scratch)
            {
                await Sudo("ip", "link", "del", hostLeg);
                await Sudo("ip", "netns", "del", sns);
            }
            foreach (var id in ids) await router.ReleaseAsync(new NetworkLease(id, RunNetNames.For(id).Tap), CancellationToken.None);
            await Sudo("rm", "-rf", tmp);
        }
        foreach (var id in ids)
        {
            var h = id.ToString("N")[..10];
            Assert.DoesNotContain(h, (await Proc.RunAsync("ip", ["-o", "link"], CancellationToken.None)).Stdout);
            Assert.DoesNotContain(h, (await Proc.RunAsync("ip", ["netns", "list"], CancellationToken.None)).Stdout);
            Assert.False(Directory.Exists(NetnsEgressRouter.DnsDir(id)), "per-run dnsmasq dir must be removed");
        }
    }

    /// <summary>
    /// INetSim's main process: per-run pid file, own IPC namespace, read-only mounts except its run dir, and a user namespace
    /// whose root is the unprivileged maldex-isroot (no host capability: cannot read root-only files, write devices, remount
    /// or enter host namespaces), in a PID namespace of its own. The pid file holds its PID inside that namespace; the host
    /// PID is found through NSpid. Returns its host PID.
    /// </summary>
    private static async Task<string> AssertInetSimSandboxAsync(string iRoot, string ns)
    {
        Assert.Equal("root:root 755", (await Sudo("stat", "-c", "%U:%G %a", iRoot)).Stdout.Trim());
        var nsPid = (await Sudo("cat", iRoot + "/pid/inetsim.pid")).Stdout.Trim();
        Assert.Matches("^[0-9]+$", nsPid);
        var pid = await InetSimHostPidAsync(ns, nsPid);
        // its own PID namespace (the launcher is PID 1 there), so it cannot see or signal any other run's processes
        var pidNs = (await Sudo("readlink", $"/proc/{pid}/ns/pid")).Stdout.Trim();
        Assert.StartsWith("pid:[", pidNs);
        Assert.NotEqual((await Sudo("readlink", "/proc/1/ns/pid")).Stdout.Trim(), pidNs);
        Assert.False(File.Exists("/var/run/inetsim.pid"), "the shared default pid file must not be used");
        var ipc = (await Sudo("readlink", $"/proc/{pid}/ns/ipc")).Stdout.Trim();
        Assert.StartsWith("ipc:[", ipc);
        Assert.NotEqual((await Sudo("readlink", "/proc/1/ns/ipc")).Stdout.Trim(), ipc);
        var writable = (await Sudo("cat", $"/proc/{pid}/mountinfo")).Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split(' '))
            .Where(f => !f[5].Split(',').Contains("ro") && f[Array.IndexOf(f, "-") + 1] != "nsfs")
            .Select(f => f[4] + " " + f[5]).ToArray();
        Assert.Equal([iRoot + " rw,nosuid,nodev,noexec,relatime"], writable);

        // user namespace: host credentials are the dedicated ids, never 0
        var isRootUid = (await Proc.RunAsync("id", ["-u", "maldex-isroot"], CancellationToken.None)).Stdout.Trim();
        var isRootGid = (await Proc.RunAsync("id", ["-g", "maldex-isroot"], CancellationToken.None)).Stdout.Trim();
        var isUserUid = (await Proc.RunAsync("id", ["-u", "maldex-isuser"], CancellationToken.None)).Stdout.Trim();
        var inUid = (await Proc.RunAsync("id", ["-u", "inetsim"], CancellationToken.None)).Stdout.Trim();
        Assert.NotEqual((await Sudo("readlink", "/proc/1/ns/user")).Stdout.Trim(), (await Sudo("readlink", $"/proc/{pid}/ns/user")).Stdout.Trim());
        var status = File.ReadAllLines($"/proc/{pid}/status").ToDictionary(l => l.Split(':')[0], l => l.Split(':', 2)[1].Trim());
        Assert.Equal([isRootUid, isRootUid, isRootUid, isRootUid], status["Uid"].Split('\t'));
        Assert.Equal([isRootGid, isRootGid, isRootGid, isRootGid], status["Gid"].Split('\t'));
        var uidMap = (await Sudo("cat", $"/proc/{pid}/uid_map")).Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => string.Join(' ', l.Split(' ', StringSplitOptions.RemoveEmptyEntries))).ToArray();
        Assert.Equal([$"0 {isRootUid} 1", $"{inUid} {isUserUid} 1"], uidMap);
        // every INetSim process (main + service children) has a non-root host uid; the services run as maldex-isuser
        var uids = new List<string>();
        foreach (var p in (await Sudo("ip", "netns", "pids", ns)).Stdout.Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var ps = (await Proc.RunAsync("ps", ["-o", "uid=,comm=", "-p", p], CancellationToken.None)).Stdout.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (ps.Length == 2 && ps[1].StartsWith("inetsim", StringComparison.Ordinal)) uids.Add(ps[0]);
        }
        Assert.Contains(isRootUid, uids);
        Assert.Contains(isUserUid, uids);
        Assert.DoesNotContain("0", uids);

        // from INSIDE its namespaces, as its root: no host privilege at all
        async Task<ProcessResult> AsInetSimRoot(string script) =>
            await Sudo("nsenter", "-t", pid, "-U", "-m", "-n", "-i", "--", "sh", "-c", script);
        Assert.Equal("0", (await AsInetSimRoot("id -u")).Stdout.Trim()); // it IS root in its own namespace...
        var shadow = await AsInetSimRoot("cat /etc/shadow >/dev/null");
        Assert.NotEqual(0, shadow.ExitCode);
        Assert.Contains("Permission denied", shadow.Stderr); // EACCES: host DAC applies, no CAP_DAC_OVERRIDE on the host
        var disk = (await Proc.RunAsync("sh", ["-c", "lsblk -dnpo NAME,TYPE | awk '$2==\"disk\"{print $1; exit}'"], CancellationToken.None)).Stdout.Trim();
        Assert.StartsWith("/dev/", disk);
        Assert.NotEqual(0, (await AsInetSimRoot($": >> {disk}")).ExitCode); // opens only (no byte written): device write denied
        Assert.NotEqual(0, (await AsInetSimRoot("mount -o remount,bind,rw /")).ExitCode);
        Assert.NotEqual(0, (await AsInetSimRoot("unshare -m mount -o remount,bind,rw /")).ExitCode); // own mount ns: mounts locked
        Assert.NotEqual(0, (await AsInetSimRoot("nsenter -t 1 -m true")).ExitCode); // no setns into host namespaces
        Assert.NotEqual(0, (await AsInetSimRoot($"echo x > {iRoot}/../escape")).ExitCode);
        return pid;
    }

    /// <summary>Host PID of the process in the run's netns whose NSpid chain is (host pid, <paramref name="nsPid"/>).</summary>
    private static async Task<string> InetSimHostPidAsync(string ns, string nsPid)
    {
        foreach (var p in (await Sudo("ip", "netns", "pids", ns)).Stdout.Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = (await Sudo("cat", $"/proc/{p}/status")).Stdout.Split('\n').FirstOrDefault(l => l.StartsWith("NSpid:", StringComparison.Ordinal));
            var f = line?.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray() ?? [];
            if (f.Length == 2 && f[1] == nsPid) return p;
        }
        Assert.Fail($"no process in {ns} has in-namespace pid {nsPid}");
        return "";
    }

    /// <summary>
    /// dnsmasq: dedicated user and group, no supplementary groups, no capability in any set, no new privileges; its files live
    /// in the per-run dir (0700, owned by it). Returns its host PID.
    /// </summary>
    private static async Task<string> AssertDnsmasqUnprivilegedAsync(Guid id, string ns)
    {
        var uid = (await Proc.RunAsync("id", ["-u", NetnsEgressRouter.DnsUser], CancellationToken.None)).Stdout.Trim();
        var gid = (await Proc.RunAsync("id", ["-g", NetnsEgressRouter.DnsUser], CancellationToken.None)).Stdout.Trim();
        string? dns = null;
        foreach (var p in (await Sudo("ip", "netns", "pids", ns)).Stdout.Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries))
            if ((await Proc.RunAsync("ps", ["-o", "comm=", "-p", p], CancellationToken.None)).Stdout.Trim() == "dnsmasq") dns = p;
        Assert.NotNull(dns);
        var st = File.ReadAllLines($"/proc/{dns}/status").ToDictionary(l => l.Split(':')[0], l => l.Split(':', 2)[1].Trim());
        Assert.Equal([uid, uid, uid, uid], st["Uid"].Split('\t'));
        Assert.Equal([gid, gid, gid, gid], st["Gid"].Split('\t'));
        Assert.Equal("", st["Groups"]);
        Assert.Equal("1", st["NoNewPrivs"]);
        foreach (var cap in new[] { "CapInh", "CapPrm", "CapEff", "CapBnd", "CapAmb" })
            Assert.Equal("0000000000000000", st[cap]);
        var d = NetnsEgressRouter.DnsDir(id);
        Assert.Equal("maldex-dns:maldex-dns 700", (await Sudo("stat", "-c", "%U:%G %a", d)).Stdout.Trim());
        Assert.Equal("root:root 755", (await Sudo("stat", "-c", "%U:%G %a", "/run/maldex/dns")).Stdout.Trim());
        Assert.Equal(dns, (await Sudo("cat", d + "/dnsmasq.pid")).Stdout.Trim());
        var log = (await Sudo("cat", d + "/dnsmasq.log")).Stdout;
        Assert.Contains("DHCP, sockets bound exclusively to interface", log);
        foreach (var bad in new[] { "failed", "cannot", "error", "setuid", "setgid", "capabilit" })
            Assert.DoesNotContain(bad, log, StringComparison.OrdinalIgnoreCase); // no complaint about running unprivileged
        return dns;
    }

    /// <summary>
    /// Direct egress (template edit sessions) on the real host: a stand-in guest (DHCP from the run's dnsmasq) reaches a public
    /// address and resolves a public name through dnsmasq, and is refused the LAN gateway, the host, the control plane, the
    /// gateway's own services and the uplink; IsolateAsync then cuts the public address off while the stand-in stays attached;
    /// release leaves no mxu link, no maldex_direct table and no namespace. Contacts only the default probe URL host and a
    /// public name lookup.
    /// </summary>
    [Fact]
    public async Task Direct_StandInGuest_ReachesInternet_NotLanHostOrControlPlane_IsolateCutsIt_ReleaseLeavesNothing()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MALDEX_REAL_NET") == "1" && OperatingSystem.IsLinux(),
            "set MALDEX_REAL_NET=1 (needs passwordless sudo, nft, dnsmasq, busybox, curl, dig)");
        var hostIp = (await Proc.RunAsync("sh", ["-c", "ip -4 -o addr show scope global | grep -v ' mx' | head -n1 | awk '{print $4}' | cut -d/ -f1"], CancellationToken.None)).Stdout.Trim();
        var lanGw = (await Proc.RunAsync("sh", ["-c", "ip -4 -o route show default | awk '{print $3; exit}'"], CancellationToken.None)).Stdout.Trim();
        Assert.False(string.IsNullOrEmpty(hostIp) || string.IsNullOrEmpty(lanGw), "no host LAN IPv4 / default gateway found");

        var tmp = Path.Combine(Path.GetTempPath(), "mx-real-net-" + Guid.NewGuid().ToString("N"));
        var opts = Options.Create(new NetOptions { RunDir = Path.Combine(tmp, "run"), DeployNetDir = RepoDeployNet(), ControlPlaneIps = [hostIp] });
        var probeUrl = new NetOptions().DirectProbeUrl;
        var router = new NetnsEgressRouter(opts, Proc);
        var id = Guid.NewGuid();
        var n = RunNetNames.For(id);
        var h = id.ToString("N")[..12];
        var scratchNs = "mxs" + h;
        var scratchHost = "mxq" + h;
        var scratchGuestLeg = "mxe" + h;
        var subnet = RunSubnet.For(0);
        NetworkLease? lease = null;
        async Task<ProcessResult> Guest(params string[] a) => await Sudo(["ip", "netns", "exec", scratchNs, .. a]);
        async Task<string> Http(string url, params string[] extra) =>
            (await Guest(["curl", "-s", "-m", "6", "-o", "/dev/null", "-w", "%{http_code}", .. extra, url])).Stdout.Trim();
        try
        {
            lease = await router.CreateAsync(id, NetworkMode.Direct, null, CancellationToken.None);
            Assert.Equal(n.Tap, lease.NetworkId);

            // topology: the uplink pair, the lease's own host table, no interception daemons
            Assert.Contains(n.UplinkHost, (await Sudo("ip", "-o", "link", "show", n.UplinkHost)).Stdout);
            var table = (await Sudo("nft", "list", "table", "inet", n.DirectTable)).Stdout;
            var defIface = (await Proc.RunAsync("sh", ["-c", "ip -4 -o route show default | awk '{print $5; exit}'"], CancellationToken.None)).Stdout.Trim();
            Assert.Contains($"iifname \"{n.UplinkHost}\" oifname \"{defIface}\" masquerade", string.Join(' ', table.Split([' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries)));
            var comms = new List<string>();
            foreach (var pid in (await Sudo("ip", "netns", "pids", n.Namespace)).Stdout.Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries))
                comms.Add((await Proc.RunAsync("ps", ["-o", "comm=", "-p", pid], CancellationToken.None)).Stdout.Trim());
            Assert.Equal(["dnsmasq"], comms.Distinct());
            await AssertDnsmasqUnprivilegedAsync(id, n.Namespace);

            // stand-in guest on the run bridge, addressed by the run's dnsmasq (DHCP), like the guest NIC
            Assert.Equal(0, (await Sudo("ip", "netns", "add", scratchNs)).ExitCode);
            Assert.Equal(0, (await Sudo("ip", "link", "add", scratchHost, "type", "veth", "peer", "name", scratchGuestLeg, "netns", scratchNs)).ExitCode);
            Assert.Equal(0, (await Guest("ip", "link", "set", scratchGuestLeg, "address", RunNetNames.GuestMac(id))).ExitCode);
            Assert.Equal(0, (await Sudo("ip", "link", "set", scratchHost, "master", n.Bridge)).ExitCode);
            Assert.Equal(0, (await Sudo("ip", "link", "set", scratchHost, "up")).ExitCode);
            await Guest("sysctl", "-w", "net.ipv6.conf.all.disable_ipv6=1");
            Assert.Equal(0, (await Guest("ip", "link", "set", scratchGuestLeg, "up")).ExitCode);
            Assert.Equal(0, (await Guest("ip", "link", "set", "lo", "up")).ExitCode);
            var dhcp = await Guest("busybox", "udhcpc", "-i", scratchGuestLeg, "-q", "-n", "-t", "4", "-T", "2", "-s", "/bin/true");
            Assert.Contains($"lease of {subnet.GuestIp} obtained", dhcp.Stderr + dhcp.Stdout);
            Assert.Equal(0, (await Guest("ip", "addr", "add", subnet.GuestIp + "/30", "dev", scratchGuestLeg)).ExitCode);
            Assert.Equal(0, (await Guest("ip", "route", "add", "default", "via", subnet.GatewayIp)).ExitCode);

            // the stand-in resolves through the run's dnsmasq like the guest does (ip netns exec bind-mounts this resolv.conf)
            Assert.Equal(0, (await Sudo("sh", "-c", $"mkdir -p /etc/netns/{scratchNs} && echo 'nameserver {subnet.GatewayIp}' > /etc/netns/{scratchNs}/resolv.conf")).ExitCode);
            // the internet: a public address is reachable, and a public name resolves through the run's dnsmasq
            Assert.Equal("204", await Http(probeUrl));
            var dig = await Guest("dig", "+short", "+time=4", "+tries=1", $"@{subnet.GatewayIp}", "example.com", "A");
            Assert.Matches(@"(?m)^\d+\.\d+\.\d+\.\d+$", dig.Stdout);

            // ... and nothing internal: the LAN gateway, the host, the control plane, the gateway, the uplink, other private ranges
            foreach (var (what, ip) in new[] { ("LAN gateway", lanGw), ("host LAN IP", hostIp), ("gateway", subnet.GatewayIp), ("uplink host end", "100.127.0.1"),
                         ("docker bridge", "172.17.0.1"), ("rfc1918", "10.0.0.1"), ("link-local", "169.254.169.254") })
                foreach (var port in new[] { 22, 80, 443 })
                {
                    var tcp = await Guest("curl", "-s", "-m", "3", "-o", "/dev/null", $"http://{ip}:{port}/");
                    Assert.True(tcp.ExitCode != 0, $"{what}:{port} answered");
                }
            Assert.NotEqual(0, (await Guest("ping", "-c", "1", "-W", "2", hostIp)).ExitCode);
            Assert.NotEqual(0, (await Guest("ping", "-c", "1", "-W", "2", lanGw)).ExitCode);
            // IsolateAsync: the stand-in stays attached, the public address is gone
            Assert.Contains(lease, await router.ListAsync(CancellationToken.None));
            await router.IsolateAsync(lease, CancellationToken.None);
            await router.IsolateAsync(lease, CancellationToken.None); // idempotent
            Assert.NotEqual("204", await Http(probeUrl));
            Assert.NotEqual(0, (await Guest("curl", "-s", "-m", "4", "-o", "/dev/null", "http://93.184.216.34/")).ExitCode);
            Assert.NotEqual(0, (await Sudo("nft", "list", "table", "inet", n.DirectTable)).ExitCode);
            Assert.DoesNotContain(n.UplinkHost, (await Proc.RunAsync("ip", ["-o", "link"], CancellationToken.None)).Stdout);
            Assert.DoesNotContain("default", (await Sudo("ip", "netns", "exec", n.Namespace, "ip", "route", "show")).Stdout);
            Assert.DoesNotContain("masquerade", (await Sudo("ip", "netns", "exec", n.Namespace, "nft", "list", "ruleset")).Stdout);
            await Guest("ping", "-c", "1", "-W", "2", subnet.GatewayIp);
            Assert.Contains("lladdr", (await Guest("ip", "neigh", "show", subnet.GatewayIp)).Stdout); // still attached to its gateway
            Assert.Contains(n.Namespace, (await Sudo("ip", "netns", "list")).Stdout);
        }
        finally
        {
            if (lease is not null) await router.ReleaseAsync(lease, CancellationToken.None);
            await router.ReleaseAsync(new NetworkLease(id, n.Tap), CancellationToken.None); // idempotent
            await Sudo("ip", "link", "del", scratchHost);
            await Sudo("ip", "netns", "del", scratchNs);
            await Sudo("rm", "-rf", "--", "/etc/netns/" + scratchNs);
            await Sudo("rm", "-rf", tmp);
        }
        var links = (await Proc.RunAsync("ip", ["-o", "link"], CancellationToken.None)).Stdout;
        Assert.DoesNotContain(h, links);
        Assert.DoesNotContain("mxu", links);
        Assert.DoesNotContain("maldex_direct", (await Sudo("nft", "list", "tables")).Stdout);
        Assert.DoesNotContain(h, (await Proc.RunAsync("ip", ["netns", "list"], CancellationToken.None)).Stdout);
        Assert.False(Directory.Exists("/run/maldex/dns/mxn" + h));
    }

    private static async Task<ProcessResult> Sudo(params string[] args) => await Proc.RunAsync("sudo", ["-n", .. args], CancellationToken.None, null, TimeSpan.FromSeconds(30));

    private static string RepoDeployNet()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "deploy", "net", "inetsim"))) return Path.Combine(d.FullName, "deploy", "net");
        throw new InvalidOperationException("deploy/net not found above " + AppContext.BaseDirectory);
    }

    private static async Task Exercise(NetworkMode mode)
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MALDEX_REAL_NET") == "1" && OperatingSystem.IsLinux(),
            "set MALDEX_REAL_NET=1 (needs passwordless sudo, nft, dnsmasq, mitmdump, inetsim)");

        var hostIp = (await Proc.RunAsync("sh", ["-c", "ip -4 -o addr show scope global | grep -v ' mx' | head -n1 | awk '{print $4}' | cut -d/ -f1"], CancellationToken.None)).Stdout.Trim();
        Assert.False(string.IsNullOrEmpty(hostIp), "no host LAN IPv4 found");

        var tmp = Path.Combine(Path.GetTempPath(), "mx-real-net-" + Guid.NewGuid().ToString("N"));
        var opts = Options.Create(new NetOptions
        {
            RunDir = Path.Combine(tmp, "run"),
            MitmConfDir = Path.Combine(tmp, "ca"),
            DeployNetDir = RepoDeployNet(),
            ControlPlaneIps = [hostIp],
        });
        // seed the CA the guest image would trust (mitmdump writes it at start-up, then idles; stop it)
        await Proc.RunAsync("timeout", ["8", "/opt/mitmproxy/mitmdump", "--set", $"confdir={Path.Combine(tmp, "ca")}", "-n", "-q"], CancellationToken.None, null, TimeSpan.FromSeconds(20));
        var router = new NetnsEgressRouter(opts, Proc);
        var id = Guid.NewGuid();
        var n = RunNetNames.For(id);
        var h = id.ToString("N")[..12];
        var scratchNs = "mxs" + h;
        var scratchHost = "mxq" + h;
        var scratchNsLeg = "mxs" + h;
        var subnet = RunSubnet.For(0);
        NetworkLease? lease = null;
        try
        {
            lease = await router.CreateAsync(id, mode, null, CancellationToken.None);
            Assert.Equal(n.Tap, lease.NetworkId);

            Assert.Contains(n.Namespace, (await Sudo("ip", "netns", "list")).Stdout);
            var rules = (await Sudo("ip", "netns", "exec", n.Namespace, "nft", "list", "ruleset")).Stdout;
            Assert.True(rules.Split("policy drop").Length - 1 >= 3, "expected policy drop on input/forward/output:\n" + rules);
            var addr = (await Sudo("ip", "-o", "addr", "show", n.Bridge)).Stdout;
            Assert.DoesNotContain(" inet ", addr);
            Assert.Contains(n.Bridge, (await Sudo("ip", "-o", "link", "show", n.Bridge)).Stdout);
            Assert.Contains(n.Tap, (await Sudo("ip", "-o", "link", "show", n.Tap)).Stdout);

            // mitmdump (hostile-traffic parser + our addon) runs as the dedicated powerless user, fully de-privileged
            var mitmUid = (await Proc.RunAsync("id", ["-u", "maldex-mitm"], CancellationToken.None)).Stdout.Trim();
            var mitmGid = (await Proc.RunAsync("id", ["-g", "maldex-mitm"], CancellationToken.None)).Stdout.Trim();
            var pids = (await Sudo("ip", "netns", "pids", n.Namespace)).Stdout.Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries);
            string? mitmPid = null;
            foreach (var pid in pids)
                if ((await Proc.RunAsync("ps", ["-o", "comm=", "-p", pid], CancellationToken.None)).Stdout.Trim() == "mitmdump") mitmPid = pid;
            Assert.NotNull(mitmPid);
            var status = File.ReadAllLines($"/proc/{mitmPid}/status").ToDictionary(l => l.Split(':')[0], l => l.Split(':', 2)[1].Trim());
            Assert.Equal([mitmUid, mitmUid, mitmUid, mitmUid], status["Uid"].Split('\t'));
            Assert.Equal([mitmGid, mitmGid, mitmGid, mitmGid], status["Gid"].Split('\t'));
            Assert.Equal("", status["Groups"]);
            Assert.Equal("1", status["NoNewPrivs"]);
            Assert.Equal("0000000000000000", status["CapBnd"]);
            Assert.Equal("0000000000000000", status["CapEff"]);
            Assert.Equal("0000000000000000", status["CapInh"]);
            await AssertDnsmasqUnprivilegedAsync(id, n.Namespace);
            // the mitm user can read/write only its own conf/out (private CA copy 0600), nothing of ours
            Assert.Equal("maldex-mitm", (await Sudo("stat", "-c", "%U", $"/run/maldex/mitm/{n.Namespace}/conf")).Stdout.Trim());
            Assert.Equal("root", (await Sudo("stat", "-c", "%U", $"/run/maldex/mitm/{n.Namespace}")).Stdout.Trim());
            Assert.Equal("root", (await Sudo("stat", "-c", "%U", $"/run/maldex/mitm/{n.Namespace}/addon/maldex_jsonl.py")).Stdout.Trim());

            // guest stand-in: scratch namespace + veth into the run bridge (in place of the tap)
            Assert.Equal(0, (await Sudo("ip", "netns", "add", scratchNs)).ExitCode);
            Assert.Equal(0, (await Sudo("ip", "link", "add", scratchHost, "type", "veth", "peer", "name", scratchNsLeg, "netns", scratchNs)).ExitCode);
            Assert.Equal(0, (await Sudo("ip", "link", "set", scratchHost, "master", n.Bridge)).ExitCode);
            Assert.Equal(0, (await Sudo("ip", "link", "set", scratchHost, "up")).ExitCode);
            async Task<ProcessResult> Guest(params string[] a) => await Sudo(["ip", "netns", "exec", scratchNs, .. a]);
            await Guest("sysctl", "-w", "net.ipv6.conf.all.disable_ipv6=1");
            Assert.Equal(0, (await Guest("ip", "addr", "add", subnet.GuestIp + "/30", "dev", scratchNsLeg)).ExitCode);
            Assert.Equal(0, (await Guest("ip", "link", "set", scratchNsLeg, "up")).ExitCode);
            Assert.Equal(0, (await Guest("ip", "link", "set", "lo", "up")).ExitCode);
            Assert.Equal(0, (await Guest("ip", "route", "add", "default", "via", subnet.GatewayIp)).ExitCode);

            // sanity: the stand-in really is attached to the gateway (ARP resolves; ICMP to the gateway itself is dropped by design)
            await Guest("ping", "-c", "1", "-W", "2", subnet.GatewayIp);
            var neigh = (await Guest("ip", "neigh", "show", subnet.GatewayIp)).Stdout;
            Assert.Contains("lladdr", neigh);
            // ... but nothing else is reachable
            Assert.NotEqual(0, (await Guest("ping", "-c", "1", "-W", "2", hostIp)).ExitCode);
            Assert.NotEqual(0, (await Guest("ping", "-c", "1", "-W", "2", "8.8.8.8")).ExitCode);
            Assert.NotEqual(0, (await Guest("curl", "-s", "-m", "3", "-o", "/dev/null", $"http://{subnet.GatewayIp}:22/")).ExitCode);
            Assert.NotEqual(0, (await Guest("curl", "-s", "-m", "3", "-o", "/dev/null", $"http://{hostIp}:22/")).ExitCode);
            Assert.NotEqual(0, (await Guest("curl", "-s", "-m", "3", "-o", "/dev/null", $"http://{subnet.GatewayIp}:8080/")).ExitCode); // direct mitm port

            // capture end to end (Simulated): enable capture, make an HTTP request from the guest, expect a flows line
            if (mode == NetworkMode.Simulated) await File.WriteAllTextAsync(NetnsEgressRouter.MitmCaptureEnablePath(id), "");
            var any = await Guest("curl", "-s", "-m", "8", "-o", "/dev/null", "-w", "%{http_code}", "http://203.0.113.5/");
            if (mode == NetworkMode.Simulated)
            {
                Assert.Equal(0, any.ExitCode);
                Assert.Contains(any.Stdout.Trim(), new[] { "200", "404" }); // INetSim answers (its default root returns a 404 page here)
                var page = await Guest("curl", "-s", "-m", "8", "http://203.0.113.5/");
                Assert.Contains("html", page.Stdout, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                Assert.NotEqual(0, any.ExitCode);
            }

            if (mode == NetworkMode.Simulated)
            {
                var flowsPath = NetnsEgressRouter.MitmFlowsPath(id);
                string[] lines = [];
                for (var i = 0; i < 50 && lines.Length == 0; i++)
                {
                    if (File.Exists(flowsPath)) lines = File.ReadAllLines(flowsPath);
                    if (lines.Length == 0) await Task.Delay(100);
                }
                Assert.NotEmpty(lines);
                Assert.Contains("203.0.113.5", lines[0]);

                // HTTPS through the proxy: mitmdump must accept INetSim's self-signed upstream cert (ssl_insecure) and record
                // a real response from INetSim, not an upstream-verification error record
                var tlsCode = await Guest("curl", "-sk", "-m", "8", "-o", "/dev/null", "-w", "%{http_code}", "https://203.0.113.9/");
                Assert.Contains(tlsCode.Stdout.Trim(), new[] { "200", "404" });
                System.Text.Json.JsonElement tlsFlow = default;
                for (var i = 0; i < 50 && tlsFlow.ValueKind == System.Text.Json.JsonValueKind.Undefined; i++)
                {
                    foreach (var l in File.ReadAllLines(flowsPath))
                    {
                        var doc = System.Text.Json.JsonDocument.Parse(l).RootElement;
                        if (doc.GetProperty("url").GetString()!.StartsWith("https://203.0.113.9", StringComparison.Ordinal)) tlsFlow = doc.Clone();
                    }
                    if (tlsFlow.ValueKind == System.Text.Json.JsonValueKind.Undefined) await Task.Delay(100);
                }
                Assert.Equal(System.Text.Json.JsonValueKind.Object, tlsFlow.ValueKind);
                Assert.False(tlsFlow.TryGetProperty("error", out var tlsErr), "HTTPS flow is an error record: " + tlsErr);
                Assert.Contains(tlsFlow.GetProperty("status").GetInt32(), new[] { 200, 404 });
                Assert.True(tlsFlow.GetProperty("tls").GetBoolean());
                Assert.Equal("maldex-mitm 640", (await Sudo("stat", "-c", "%U %a", flowsPath)).Stdout.Trim());
                var dataDir = NetnsEgressRouter.InetSimDataPath(id);
                Assert.Equal(0, (await Sudo("test", "-d", dataDir + "/http/postdata")).ExitCode);
                Assert.Equal(0, (await Sudo("test", "-f", dataDir + "/http/mime.types")).ExitCode);
                // host 'inetsim' owns nothing; the user-namespace ids own exactly what INetSim must write
                var iRoot = Path.GetDirectoryName(dataDir)!;
                async Task<string[]> Find(params string[] a) =>
                    (await Sudo(["find", iRoot, .. a])).Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).OrderBy(x => x, StringComparer.Ordinal).ToArray();
                static string[] Sorted(params string[] a) => a.OrderBy(x => x, StringComparer.Ordinal).ToArray();
                Assert.Empty(await Find("-user", "inetsim"));
                Assert.Empty(await Find("-group", "inetsim"));
                Assert.Equal(Sorted(dataDir + "/ftp/upload", dataDir + "/http/postdata"), await Find("-user", "maldex-isuser"));
                Assert.Equal(Sorted(iRoot + "/pid", iRoot + "/pid/inetsim.pid", dataDir + "/certs/default_key.pem",
                    dataDir + "/smtp/smtp.mbox", dataDir + "/smtp/smtps.mbox", iRoot + "/log/main.log", iRoot + "/log/service.log", iRoot + "/log/debug.log"),
                    await Find("-user", "maldex-isroot"));
                Assert.Empty(await Find("-type", "l"));
                // every directory except the two upload leaves and the pid dir is root:root and not writable by others
                Assert.Equal(Sorted(dataDir + "/ftp/upload", dataDir + "/http/postdata", iRoot + "/pid"), await Find("-type", "d", "(", "!", "-user", "root", "-o", "-perm", "/022", ")"));
                Assert.Equal("maldex-isuser:maldex-isuser 750", (await Sudo("stat", "-c", "%U:%G %a", dataDir + "/http/postdata")).Stdout.Trim());
                Assert.Equal("maldex-isroot:maldex-isroot 755", (await Sudo("stat", "-c", "%U:%G %a", iRoot + "/pid")).Stdout.Trim());
                foreach (var mbox in new[] { "smtp.mbox", "smtps.mbox" })
                    Assert.Equal("maldex-isroot:maldex-isuser 660 regular empty file", (await Sudo("stat", "-c", "%U:%G %a %F", $"{dataDir}/smtp/{mbox}")).Stdout.Trim());
                Assert.Equal("maldex-isroot:maldex-isuser 640", (await Sudo("stat", "-c", "%U:%G %a", dataDir + "/certs/default_key.pem")).Stdout.Trim());
                Assert.Equal("root:root 755", (await Sudo("stat", "-c", "%U:%G %a", iRoot + "/log")).Stdout.Trim());
                Assert.Equal("root:root 755", (await Sudo("stat", "-c", "%U:%G %a", iRoot + "/report")).Stdout.Trim());
                Assert.Equal("root:root 755", (await Sudo("stat", "-c", "%U:%G %a", dataDir + "/smtp")).Stdout.Trim());
                // HTTP (above), HTTPS, SMTP and FTP all answer; SMTP appends to the pre-created mbox
                var https = (await Sudo("ip", "netns", "exec", n.Namespace, "curl", "-sk", "-o", "/dev/null", "-m", "8", "-w", "%{http_code}", "https://198.18.0.10/")).Stdout.Trim();
                Assert.Contains(https, new[] { "200", "404" });
                var mail = await Proc.RunAsync("sudo", ["-n", "ip", "netns", "exec", n.Namespace, "curl", "-s", "-m", "8", "smtp://198.18.0.10/",
                    "--mail-from", "a@example.org", "--mail-rcpt", "b@example.org", "-T", "-"], CancellationToken.None, "Subject: maldex\r\n\r\nhello\r\n", TimeSpan.FromSeconds(30));
                Assert.Equal(0, mail.ExitCode);
                Assert.Contains("hello", (await Sudo("cat", dataDir + "/smtp/smtp.mbox")).Stdout);
                // FTP control channel (INetSim's FTP has no passive mode configured; a login proves the service runs)
                var ftp = await Sudo("ip", "netns", "exec", n.Namespace, "curl", "-sS", "-v", "-m", "8", "-o", "/dev/null", "ftp://198.18.0.10/");
                Assert.Contains("230 Login successful", ftp.Stderr);
                await AssertInetSimSandboxAsync(iRoot, n.Namespace);
            }
            Assert.Contains(lease, await router.ListAsync(CancellationToken.None));
            if (mode == NetworkMode.Simulated)
            {
                // release keeps the flows for collection (safe copy), then everything is torn down
                await router.ReleaseAsync(lease, CancellationToken.None);
                var kept = Path.Combine(tmp, "run", "net", n.Namespace, "flows.jsonl");
                Assert.True(File.Exists(kept) && new FileInfo(kept).Length > 0, "flows.jsonl must be kept in the run net dir");
            }
        }
        finally
        {
            if (lease is not null) await router.ReleaseAsync(lease, CancellationToken.None);
            else await router.ReleaseAsync(new NetworkLease(id, n.Tap), CancellationToken.None);
            await router.ReleaseAsync(new NetworkLease(id, n.Tap), CancellationToken.None); // idempotent
            await Sudo("ip", "link", "del", scratchHost);
            await Sudo("ip", "netns", "del", scratchNs);
            await Sudo("rm", "-rf", tmp);
        }

        Assert.DoesNotContain(h, (await Proc.RunAsync("ip", ["-o", "link"], CancellationToken.None)).Stdout);
        Assert.False(Directory.Exists("/run/maldex/mitm/mxn" + h), "per-run mitm dir must be removed");
        Assert.False(Directory.Exists("/run/maldex/inetsim/mxn" + h), "per-run inetsim dir must be removed");
        Assert.False(Directory.Exists("/run/maldex/dns/mxn" + h), "per-run dnsmasq dir must be removed");
        Assert.DoesNotContain(h, (await Proc.RunAsync("ip", ["netns", "list"], CancellationToken.None)).Stdout);
    }
}
