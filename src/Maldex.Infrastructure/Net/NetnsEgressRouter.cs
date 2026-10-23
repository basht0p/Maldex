using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Maldex.Infrastructure.Execution;
using Microsoft.Extensions.Options;

namespace Maldex.Infrastructure.Net;

/// <summary>Timeouts and host paths the router polls with. Tests shrink these; production uses the defaults.</summary>
public sealed record NetnsRouterTuning
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan MitmWait { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan InetSimWait { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan HandshakeWait { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan KillGrace { get; init; } = TimeSpan.FromSeconds(5);
    public string BrNetfilterPath { get; init; } = "/proc/sys/net/bridge/bridge-nf-call-iptables";
    /// <summary>Where the live mitm flows file is (tests point it at a scratch file).</summary>
    public Func<Guid, string> FlowsSource { get; init; } = NetnsEgressRouter.MitmFlowsPath;
}

/// <summary>
/// Real <see cref="IEgressRouter"/>: one gateway network namespace per run. The ruleset is applied before any link
/// exists, the host bridge never gets an address, and any failure tears everything down before throwing (fail closed).
/// All privileged commands go through <see cref="IProcessRunner"/> as <c>sudo -n</c> with discrete arguments.
/// </summary>
public sealed partial class NetnsEgressRouter(
    IOptions<NetOptions> options, IProcessRunner runner, Action<string>? warn = null, NetnsRouterTuning? tuning = null)
    : IEgressRouter
{
    private sealed record LeaseFile(Guid TaskId, int Index, string Mode, string? ExitCountry, string Bridge, string Tap, string VethHost, string VethNs, string Namespace, string WgIface);

    private readonly NetOptions _o = options.Value;
    private readonly NetnsRouterTuning _t = tuning ?? new NetnsRouterTuning();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, (int Index, string? Country)> _active = [];

    [GeneratedRegex("^mxn[0-9a-f]{12}$")] private static partial Regex NsName();
    [GeneratedRegex("^mx[bvtw]([0-9a-f]{12})$")] private static partial Regex HostLink();
    [GeneratedRegex("^[A-Za-z0-9_-]{1,32}$")] private static partial Regex CountryName();
    [GeneratedRegex("^[a-z_][a-z0-9_-]{0,31}$")] private static partial Regex UserName();

    /// <summary>Dedicated account mitmdump runs as: own group, no supplementary groups, no sudo (created by <c>./build.sh real-setup</c>).</summary>
    public const string MitmUser = "maldex-mitm";

    private static readonly string[] MitmCaFiles = ["mitmproxy-ca.pem", "mitmproxy-ca-cert.pem", "mitmproxy-dhparam.pem"];

    /// <summary>Upper bound on the flows file kept at release.</summary>
    public const long MaxFlowsBytes = 512L * 1024 * 1024;

    private static string InetSimDir(string ns) => "/run/maldex/inetsim/" + ns;

    /// <summary>Marker in the run's net dir: the capture's flows are already stored as an artifact, so release skips its copy.</summary>
    public const string FlowsStoredMarker = "flows.stored";

    /// <summary>
    /// Tells a later <see cref="ReleaseAsync"/> not to copy the flows again (they can be 512 MiB). Writes the marker into the
    /// run's existing net dir (control-plane only, 0700); never creates the dir. Returns false when there is no such dir.
    /// </summary>
    public static bool MarkFlowsStored(string runDir, Guid taskId)
    {
        var dir = Path.Combine(Path.GetFullPath(runDir), "net", RunNetNames.For(taskId).Namespace);
        if (!Directory.Exists(dir)) return false;
        File.WriteAllText(Path.Combine(dir, FlowsStoredMarker), "");
        return true;
    }

    /// <summary>Unprivileged host account that is uid/gid 0 inside INetSim's user namespace (created by <c>./build.sh real-setup</c>).</summary>
    public const string IsRootUser = "maldex-isroot";

    /// <summary>Unprivileged host account that INetSim's service user 'inetsim' maps to inside its user namespace.</summary>
    public const string IsUserUser = "maldex-isuser";

    /// <summary>
    /// Fixed launcher, run by real root inside fresh IPC + mount namespaces. argv: uid_map, gid_map, run dir, sandbox script,
    /// command. The child unshares a user namespace; the parent writes its uid/gid maps (only real root may map arbitrary host
    /// ids; done before /proc goes read-only), then runs <see cref="InetSimSandboxScript"/> on the shared mount namespace
    /// (owned by the initial user namespace, so the child can never undo it) and exits. Only then does the child switch to the
    /// namespace's root (= the unprivileged maldex-isroot) and exec the command, with capabilities inside its user namespace
    /// only. Any failure kills the child before it execs and exits non-zero.
    /// </summary>
    public const string UserNsLauncherScript = """
        import os, subprocess, sys, time
        umap, gmap, rundir, sandbox, argv = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4], sys.argv[5:]
        r, w = os.pipe()
        pid = os.fork()
        if pid == 0:
            os.close(w)
            os.unshare(os.CLONE_NEWUSER)
            if os.read(r, 1) != b"1":
                os._exit(111)
            os.setgroups([])
            os.setresgid(0, 0, 0)
            os.setresuid(0, 0, 0)
            os.chdir(rundir)
            os.execvp(argv[0], argv)
        os.close(r)
        def fail(msg):
            os.kill(pid, 9)
            sys.exit("inetsim launcher: " + msg)
        me = os.readlink("/proc/self/ns/user")
        for _ in range(500):
            try:
                if os.readlink(f"/proc/{pid}/ns/user") != me:
                    break
            except OSError:
                pass
            time.sleep(0.01)
        else:
            fail("child did not enter a user namespace")
        try:
            with open(f"/proc/{pid}/gid_map", "w") as f:
                f.write(gmap)
            with open(f"/proc/{pid}/uid_map", "w") as f:
                f.write(umap)
        except OSError as e:
            fail(f"cannot write id maps: {e}")
        if subprocess.run(["sh", "-c", sandbox, rundir]).returncode != 0:
            fail("mount sandbox failed")
        os.write(w, b"1")
        os._exit(0)
        """;

    /// <summary>
    /// Fixed mount sandbox the launcher applies to INetSim's fresh IPC + mount namespace (<c>$0</c> = the run's INetSim dir). INetSim keeps its whole configuration in a fixed-key ("CNFG"), mode-0666 SysV shared memory segment
    /// that its ROOT main process reads back after the services (which a guest can take over) are running, e.g. the report
    /// settings at shutdown, where root then writes/chmods/chowns a file at a path taken from that segment. The private IPC
    /// namespace keeps runs (and every other local uid) apart; the mount namespace makes every mount read-only except the
    /// run's own dir (nosuid/nodev/noexec). Mounts that cannot be made read-only are detached; if any writable mount other
    /// than an nsfs handle remains, it fails closed. Run by <see cref="UserNsLauncherScript"/>, so INetSim's "root" is an
    /// unprivileged host id that holds no host capability and cannot undo these mounts (owned by the initial user namespace).
    /// </summary>
    public const string InetSimSandboxScript = """
        set -eu
        d="$0"
        rw() { awk '{ for (i = 7; $i != "-"; i++) ; if ($6 !~ /(^|,)ro(,|$)/ && $(i + 1) != "nsfs") print $5 }' /proc/self/mountinfo | sort -ru; }
        awk '{ print $5 }' /proc/self/mountinfo | sort -ru | while IFS= read -r m; do mount -o remount,bind,ro "$(printf '%b' "$m")" 2>/dev/null || true; done
        for pass in 1 2 3 4 5 6 7 8; do
          [ -z "$(rw)" ] && break
          rw | while IFS= read -r m; do umount -l "$(printf '%b' "$m")" 2>/dev/null || true; done # stacked mounts: one per pass
        done
        left=$(rw)
        if [ -n "$left" ]; then echo "inetsim sandbox: writable mounts remain: $left" >&2; exit 1; fi
        mount --bind "$d" "$d"
        mount -o remount,bind,rw,nosuid,nodev,noexec "$d"
        """;

    // mitmdump cannot traverse the owner's home (the repo and .data live there), so everything it touches is staged here.
    private static string MitmDir(string ns) => "/run/maldex/mitm/" + ns;

    /// <summary>
    /// Live flows file written by mitmdump: mode 0640, owner maldex-mitm, group = the control-plane user's group (readable by
    /// the control plane only). Untrusted: read it with <see cref="UntrustedFile"/>. After release it is copied to
    /// <c>{runNetDir}/flows.jsonl</c>.
    /// </summary>
    public static string MitmFlowsPath(Guid taskId) => MitmDir(RunNetNames.For(taskId).Namespace) + "/out/flows.jsonl";

    /// <summary>Capture is on while this file exists; the control plane owns the directory, mitmdump only stats it.</summary>
    public static string MitmCaptureEnablePath(Guid taskId) => MitmDir(RunNetNames.For(taskId).Namespace) + "/ctl/capture.on";

    /// <summary>
    /// The per-run INetSim data directory (guest uploads land in <c>http/postdata</c> and <c>ftp/upload</c>, mail in
    /// <c>smtp/smtp.mbox</c> under it). Root-managed: read it with sudo. Removed by <see cref="ReleaseAsync"/>.
    /// </summary>
    public static string InetSimDataPath(Guid taskId) => InetSimDir(RunNetNames.For(taskId).Namespace) + "/data";

    private string RunNetDir(string ns) => Path.Combine(Path.GetFullPath(_o.RunDir), "net", ns);

    public async Task<NetworkLease> CreateAsync(Guid taskId, NetworkMode mode, string? exitCountry, CancellationToken ct)
    {
        if (mode == NetworkMode.LiveTor) throw new EgressUnavailableException("LiveTor is not available");
        if (mode is not (NetworkMode.None or NetworkMode.Simulated or NetworkMode.LiveVpn))
            throw new EgressUnavailableException($"Network mode {mode} is not available");

        // Validate the exit before touching the host.
        WireGuardConf? wg = null;
        if (mode == NetworkMode.LiveVpn)
        {
            if (exitCountry is null || !CountryName().IsMatch(exitCountry))
                throw new EgressUnavailableException("LiveVpn requires a valid exit country.");
            var confPath = Path.Combine(_o.WireGuardConfigDir, exitCountry + ".conf");
            if (!File.Exists(confPath)) throw new EgressUnavailableException($"No WireGuard config for exit country '{exitCountry}'.");
            wg = WireGuardConf.Parse(await File.ReadAllTextAsync(confPath, ct));
        }

        // Host preconditions, re-checked on every run (fail closed).
        if (File.Exists(_t.BrNetfilterPath) && (await File.ReadAllTextAsync(_t.BrNetfilterPath, ct)).Trim() == "1")
            throw new EgressUnavailableException("br_netfilter is active (bridge-nf-call-iptables=1); refusing to create a run bridge.");
        var guard = await Run(PrivilegedCommands.Sudo("nft", "list", "table", "inet", "maldex_guard"), ct);
        if (guard.ExitCode != 0) throw new EgressUnavailableException("Host guard table 'inet maldex_guard' is not loaded.");

        if (mode != NetworkMode.LiveVpn) exitCountry = null; // an exit country only means something for LiveVpn
        var names = RunNetNames.For(taskId);
        int index;
        await _gate.WaitAsync(ct);
        try
        {
            if (_active.ContainsKey(taskId)) throw new EgressUnavailableException("A network already exists for this task.");
            var (usedIndexes, usedCountries, existing) = await ScanAsync(ct);
            if (existing.Contains(names.Namespace)) throw new EgressUnavailableException("A network namespace already exists for this task.");
            if (exitCountry is not null && (usedCountries.Contains(exitCountry) || _active.Values.Any(a => a.Country == exitCountry)))
                throw new EgressUnavailableException($"Exit country '{exitCountry}' is busy.");
            index = 0;
            var taken = usedIndexes.Concat(_active.Values.Select(a => a.Index)).ToHashSet();
            while (taken.Contains(index)) index++;
            if (index >= _o.MaxRuns || index > RunSubnet.MaxIndex) throw new EgressUnavailableException("No free network slot (MaxRuns reached).");
            _active[taskId] = (index, exitCountry);
        }
        finally { _gate.Release(); }

        try
        {
            await BuildAsync(taskId, mode, exitCountry, wg, names, RunSubnet.For(index), ct);
            return new NetworkLease(taskId, names.Tap);
        }
        catch (Exception ex)
        {
            try { await ReleaseCoreAsync(taskId, CancellationToken.None); }
            catch (Exception rex) { warn?.Invoke($"Teardown after failed create for {taskId:N} also failed: {rex.Message}"); }
            if (ex is OperationCanceledException) throw;
            throw new EgressUnavailableException(ex.Message);
        }
    }

    private async Task BuildAsync(Guid taskId, NetworkMode mode, string? exitCountry, WireGuardConf? wg,
        RunNetNames names, RunSubnet subnet, CancellationToken ct)
    {
        var ns = names.Namespace;
        var dir = RunNetDir(ns);
        const UnixFileMode owner = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        // 0700 from creation (no window with a umask-derived mode); chmod as well in case it already existed.
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
        else Directory.CreateDirectory(dir, owner);
        Chmod(dir, owner);
        // Written first so a crash mid-create is still reconcilable and the slot is visible to other processes.
        await File.WriteAllTextAsync(Path.Combine(dir, "lease.json"), JsonSerializer.Serialize(new LeaseFile(
            taskId, subnet.Index, mode.ToString(), exitCountry, names.Bridge, names.Tap, names.VethHost, names.VethNs, names.Namespace, names.WgIface)), ct);

        var topology = new EgressTopology(names, subnet, mode, _o.ControlPlaneIps, _o.SinkholeIp, _o.MitmPort, wg?.Dns);
        var ruleset = NftRulesetBuilder.Build(topology);
        var gw = subnet.GatewayIp;
        var user = Environment.UserName;
        if (!UserName().IsMatch(user)) throw new EgressUnavailableException("Cannot determine a safe tap owner user name.");

        // 1. namespace, IPv6 off, then the ruleset BEFORE any link exists or is up (lo included).
        await Must(PrivilegedCommands.Sudo("ip", "netns", "add", ns), ct);
        await Must(PrivilegedCommands.InNs(ns, "sysctl", "-w", "net.ipv6.conf.all.disable_ipv6=1"), ct);
        await Must(PrivilegedCommands.InNs(ns, "sysctl", "-w", "net.ipv6.conf.default.disable_ipv6=1"), ct);
        await Must(PrivilegedCommands.InNs(ns, "nft", "-f", "-"), ct, ruleset);
        await Must(PrivilegedCommands.InNs(ns, "ip", "link", "set", "lo", "up"), ct);

        // 2. links. The host bridge never gets an address.
        await Must(PrivilegedCommands.Sudo("ip", "link", "add", names.Bridge, "type", "bridge"), ct);
        await Must(PrivilegedCommands.Sudo("sysctl", "-w", $"net.ipv6.conf.{names.Bridge}.disable_ipv6=1"), ct);
        await Must(PrivilegedCommands.Sudo("ip", "tuntap", "add", "dev", names.Tap, "mode", "tap", "user", user), ct);
        await Must(PrivilegedCommands.Sudo("sysctl", "-w", $"net.ipv6.conf.{names.Tap}.disable_ipv6=1"), ct);
        await Must(PrivilegedCommands.Sudo("ip", "link", "add", names.VethHost, "type", "veth", "peer", "name", names.VethNs, "netns", ns), ct);
        await Must(PrivilegedCommands.Sudo("sysctl", "-w", $"net.ipv6.conf.{names.VethHost}.disable_ipv6=1"), ct);
        await Must(PrivilegedCommands.Sudo("ip", "link", "set", names.Tap, "master", names.Bridge), ct);
        await Must(PrivilegedCommands.Sudo("ip", "link", "set", names.VethHost, "master", names.Bridge), ct);
        await Must(PrivilegedCommands.InNs(ns, "ip", "addr", "add", $"{gw}/30", "dev", names.VethNs), ct);
        await Must(PrivilegedCommands.Sudo("ip", "link", "set", names.Bridge, "up"), ct);
        await Must(PrivilegedCommands.Sudo("ip", "link", "set", names.Tap, "up"), ct);
        await Must(PrivilegedCommands.Sudo("ip", "link", "set", names.VethHost, "up"), ct);
        await Must(PrivilegedCommands.InNs(ns, "ip", "link", "set", names.VethNs, "up"), ct);

        // 3. DNS + DHCP.
        string[] dnsArgs = mode switch
        {
            NetworkMode.Simulated => ["--no-resolv", $"--address=/#/{_o.SinkholeIp}"],
            NetworkMode.LiveVpn => ["--no-resolv", $"--server={wg!.Dns}"],
            _ => ["--no-resolv"],
        };
        // Per-run lease database (never the shared /var/lib/misc/dnsmasq.leases) and a reservation for the run's deterministic
        // guest MAC: the one-address pool can only ever be handed to this run's guest, on every VM attempt.
        await Must(PrivilegedCommands.InNs(ns, [_o.DnsmasqBinary, "--conf-file=/dev/null", "--no-hosts", $"--interface={names.VethNs}", "--bind-interfaces",
            $"--dhcp-range={subnet.GuestIp},{subnet.GuestIp},255.255.255.252,12h", $"--dhcp-host={RunNetNames.GuestMac(taskId)},{subnet.GuestIp}",
            $"--dhcp-leasefile={Path.Combine(dir, "dnsmasq.leases")}", $"--dhcp-option=3,{gw}", $"--dhcp-option=6,{gw}",
            $"--pid-file={Path.Combine(dir, "dnsmasq.pid")}", $"--log-facility={Path.Combine(dir, "dnsmasq.log")}", .. dnsArgs]), ct);

        // 4. MITM proxy, TCP only, bound to the gateway address. It parses hostile traffic and runs our addon, so it runs as
        //    the dedicated powerless user with no groups, no new privileges and an empty capability bounding set.
        //    Root stages its inputs (addon, a private CA copy) in root-owned directories; the user owns only conf/ and out/.
        await IdAsync("-u", ct, MitmUser); // fails closed if the dedicated user is missing
        var myUid = await IdAsync("-u", ct);
        var myGid = await IdAsync("-g", ct);
        var mdir = MitmDir(ns);
        var caDir = Path.GetFullPath(_o.MitmConfDir);
        foreach (var f in MitmCaFiles.Take(2))
            if (!File.Exists(Path.Combine(caDir, f)))
                throw new EgressUnavailableException($"mitmproxy CA file '{f}' is missing in {caDir}; run ./build.sh real-setup.");
        var addon = Path.GetFullPath(Path.Combine(_o.DeployNetDir, "mitm", "maldex_jsonl.py"));
        await Must(PrivilegedCommands.Sudo("install", "-d", "-m", "0755", "-o", "root", "-g", "root", "/run/maldex", "/run/maldex/mitm", mdir, mdir + "/addon"), ct);
        await Must(PrivilegedCommands.Sudo("install", "-m", "0644", "-o", "root", "-g", "root", addon, mdir + "/addon/maldex_jsonl.py"), ct);
        await Must(PrivilegedCommands.Sudo("install", "-d", "-m", "0700", "-o", MitmUser, "-g", MitmUser, mdir + "/conf"), ct);
        foreach (var f in MitmCaFiles)
            if (File.Exists(Path.Combine(caDir, f)))
                await Must(PrivilegedCommands.Sudo("install", "-m", "0600", "-o", MitmUser, "-g", MitmUser, Path.Combine(caDir, f), $"{mdir}/conf/{f}"), ct);
        // out/ is setgid to the control-plane group: flows.jsonl (umask 027 in the addon) is 0640 maldex-mitm:<control-plane group>,
        // readable by us, invisible to every other local uid (decrypted traffic).
        await Must(PrivilegedCommands.Sudo("install", "-d", "-m", "2750", "-o", MitmUser, "-g", myGid, mdir + "/out"), ct);
        await Must(PrivilegedCommands.Sudo("install", "-d", "-m", "0755", "-o", myUid, "-g", myGid, mdir + "/ctl"), ct);
        await Must(PrivilegedCommands.DetachedInNs(ns, Path.Combine(dir, "mitmdump.log"), ["setpriv", $"--reuid={MitmUser}", $"--regid={MitmUser}",
            "--clear-groups", "--no-new-privs", "--inh-caps=-all", "--bounding-set=-all", "--", _o.MitmdumpBinary,
            "--mode", "transparent", "--listen-host", gw, "--listen-port", _o.MitmPort.ToString(CultureInfo.InvariantCulture),
            // ssl_insecure: never verify upstream certificates. This is an analysis proxy and malware infrastructure (and
            // INetSim in Simulated) is routinely self-signed; verification would turn every such flow into an error record.
            "--set", $"confdir={mdir}/conf", "--set", "block_global=false", "--set", "http3=false", "--set", "ssl_insecure=true",
            "-s", mdir + "/addon/maldex_jsonl.py", "--set", $"maldex_out={mdir}/out/flows.jsonl", "--set", $"maldex_enable_file={mdir}/ctl/capture.on"]), ct);
        await WaitFor(async () =>
        {
            var ss = await Run(PrivilegedCommands.InNs(ns, "ss", "-ltn"), ct);
            return ss.ExitCode == 0 && ss.Stdout.Contains($"{gw}:{_o.MitmPort}", StringComparison.Ordinal);
        }, _t.MitmWait, () => $"mitmdump did not listen on {gw}:{_o.MitmPort}" + Tail(Path.Combine(dir, "mitmdump.log")), ct);

        // 5. exit
        switch (mode)
        {
            case NetworkMode.Simulated:
                await StartSimulatedAsync(ns, dir, ct);
                break;
            case NetworkMode.LiveVpn:
                await StartVpnAsync(names, dir, wg!, ct);
                break;
        }
    }

    private async Task StartSimulatedAsync(string ns, string dir, CancellationToken ct)
    {
        await Must(PrivilegedCommands.InNs(ns, "ip", "route", "add", "local", "0.0.0.0/0", "dev", "lo", "table", "main"), ct);
        // INetSim insists on starting as root, keeps its config in a mode-0666 shared memory segment its service children (which
        // a guest can take over) can write, and its root main process acts on that config. So its "root" is not root on the
        // host: it runs in a user namespace where inside uid/gid 0 is the dedicated unprivileged maldex-isroot and the inside
        // 'inetsim' ids (looked up from /etc/passwd, as INetSim does) are maldex-isuser. It holds no capability in the initial
        // user namespace (no device writes, no host mounts, no setns), and host DAC applies to everything it opens.
        var isRootUid = await IdAsync("-u", ct, IsRootUser);
        var isRootGid = await IdAsync("-g", ct, IsRootUser);
        var isUserUid = await IdAsync("-u", ct, IsUserUser);
        var isUserGid = await IdAsync("-g", ct, IsUserUser);
        var inUid = await IdAsync("-u", ct, "inetsim");
        var inGid = await IdAsync("-g", ct, "inetsim");
        if (new[] { isRootUid, isRootGid, isUserUid, isUserGid, inUid, inGid }.Any(v => v == "0") || isRootUid == isUserUid || isRootGid == isUserGid)
            throw new EgressUnavailableException("INetSim user-namespace ids are invalid (zero or shared); run ./build.sh real-setup.");
        var uidMap = $"0 {isRootUid} 1\n{inUid} {isUserUid} 1\n";
        var gidMap = $"0 {isRootGid} 1\n{inGid} {isUserGid} 1\n";

        // Real root still stages the tree: it only ever creates entries in root-owned parents (or rm -rf, which never follows
        // links). The template is verified (root-owned, not writable by others, no symlinks) and copied without following links.
        var isDir = InetSimDir(ns);
        var tmpl = _o.InetSimDataDir.TrimEnd('/');
        var bad = await Run(PrivilegedCommands.Sudo("find", tmpl, "(", "-type", "l", "-o", "!", "-user", "root", "-o", "-perm", "/022", ")", "-print", "-quit"), ct);
        if (bad.ExitCode != 0 || bad.Stdout.Trim().Length > 0)
            throw new EgressUnavailableException($"INetSim template {tmpl} is not root-owned/read-only/symlink-free ({bad.Stdout.Trim()}); run ./build.sh real-setup.");
        var data = isDir + "/data";
        var log = isDir + "/log";
        var report = isDir + "/report";
        var pid = isDir + "/pid";
        const string rootIds = IsRootUser + ":" + IsUserUser;
        await Must(PrivilegedCommands.Sudo("install", "-d", "-m", "0755", "-o", "root", "-g", "root", "/run/maldex", "/run/maldex/inetsim", isDir, data), ct);
        await Must(PrivilegedCommands.Sudo("cp", "-rP", "--no-preserve=ownership", tmpl + "/.", data + "/"), ct);
        // the TLS key: read by the namespace's root at start-up (owner) and by the service user (group)
        await Must(PrivilegedCommands.Sudo("chown", rootIds, data + "/certs/default_key.pem"), ct);
        await Must(PrivilegedCommands.Sudo("chmod", "0640", data + "/certs/default_key.pem"), ct);
        // guest uploads: owned by the service user (inside: inetsim)
        foreach (var w in new[] { "http/postdata", "ftp/upload" })
        {
            await Must(PrivilegedCommands.Sudo("rm", "-rf", "--", $"{data}/{w}"), ct);
            await Must(PrivilegedCommands.Sudo("install", "-d", "-m", "0750", "-o", IsUserUser, "-g", IsUserUser, $"{data}/{w}"), ct);
        }
        // mbox and log files live in root-owned dirs (nobody in the namespace can create/replace entries there) and are
        // pre-created owner maldex-isroot / group maldex-isuser 0660: INetSim requires group 'inetsim' + group rw on them.
        await Must(PrivilegedCommands.Sudo("rm", "-rf", "--", $"{data}/smtp"), ct);
        await Must(PrivilegedCommands.Sudo("install", "-d", "-m", "0755", "-o", "root", "-g", "root", $"{data}/smtp"), ct);
        foreach (var mbox in new[] { "smtp.mbox", "smtps.mbox" })
            await Must(PrivilegedCommands.Sudo("install", "-m", "0660", "-o", IsRootUser, "-g", IsUserUser, "/dev/null", $"{data}/smtp/{mbox}"), ct);
        await Must(PrivilegedCommands.Sudo("install", "-d", "-m", "0755", "-o", "root", "-g", "root", log), ct);
        foreach (var lf in new[] { "main.log", "service.log", "debug.log" })
            await Must(PrivilegedCommands.Sudo("install", "-m", "0660", "-o", IsRootUser, "-g", IsUserUser, "/dev/null", $"{log}/{lf}"), ct);
        await Must(PrivilegedCommands.Sudo("install", "-d", "-m", "0755", "-o", "root", "-g", "root", report), ct); // create_reports no
        await Must(PrivilegedCommands.Sudo("install", "-d", "-m", "0755", "-o", IsRootUser, "-g", IsRootUser, pid), ct);
        // the namespace's root has no CAP_NET_BIND_SERVICE on the host: allow low ports in this run's netns only
        await Must(PrivilegedCommands.InNs(ns, "sysctl", "-w", "net.ipv4.ip_unprivileged_port_start=0"), ct);
        // the namespace's root cannot traverse the owner's home: stage a root-owned copy of the config
        var conf = isDir + "/inetsim.conf";
        await Must(PrivilegedCommands.Sudo("install", "-m", "0644", "-o", "root", "-g", "root",
            Path.GetFullPath(Path.Combine(_o.DeployNetDir, "inetsim", "inetsim.conf")), conf), ct);
        await Must(PrivilegedCommands.DetachedInNs(ns, Path.Combine(dir, "inetsim.out"), ["unshare", "--ipc", "--mount", "--propagation", "private", "--",
            "python3", "-I", "-c", UserNsLauncherScript, uidMap, gidMap, isDir, InetSimSandboxScript, _o.InetSimBinary,
            "--config", conf, "--data-dir", data, "--log-dir", log, "--report-dir", report, "--bind-address", "0.0.0.0",
            "--pidfile", pid + "/inetsim.pid"]), ct); // per run: no shared/stale /var/run/inetsim.pid
        await WaitFor(async () =>
        {
            var r = await Run(PrivilegedCommands.InNs(ns, "curl", "-s", "-o", "/dev/null", "-m", "2", "-w", "%{http_code}", $"http://{_o.SinkholeIp}/"), ct);
            var code = r.Stdout.Trim();
            return r.ExitCode == 0 && code.Length == 3 && code != "000";
        }, _t.InetSimWait, () => $"INetSim did not answer HTTP on {_o.SinkholeIp}" + Tail(Path.Combine(dir, "inetsim.out")), ct);
    }

    private async Task StartVpnAsync(RunNetNames names, string dir, WireGuardConf wg, CancellationToken ct)
    {
        var ns = names.Namespace;
        // Created in the host namespace so its encrypted UDP socket stays there, then moved into the run namespace.
        await Must(PrivilegedCommands.Sudo("ip", "link", "add", names.WgIface, "type", "wireguard"), ct);
        await Must(PrivilegedCommands.Sudo("ip", "link", "set", names.WgIface, "netns", ns), ct);
        var confFile = Path.Combine(dir, "wg.conf");
        var fso = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) fso.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; // 0600 at creation, no chmod window
        await using (var fs = new FileStream(confFile, fso))
        await using (var w = new StreamWriter(fs))
            await w.WriteAsync(wg.SetConfText.AsMemory(), ct);
        try
        {
            await Must(PrivilegedCommands.InNs(ns, "wg", "setconf", names.WgIface, confFile), ct, redactStderr: true);
        }
        finally { try { File.Delete(confFile); } catch (IOException) { } }
        await Must(PrivilegedCommands.InNs(ns, "ip", "addr", "add", wg.Address, "dev", names.WgIface), ct);
        await Must(PrivilegedCommands.InNs(ns, "ip", "link", "set", names.WgIface, "up"), ct);
        await Must(PrivilegedCommands.InNs(ns, "ip", "route", "add", "default", "dev", names.WgIface), ct);

        await Run(PrivilegedCommands.InNs(ns, "ping", "-c", "1", "-W", "2", wg.Dns), ct); // best effort; the keepalive also starts the handshake
        await WaitFor(async () =>
        {
            var r = await Run(PrivilegedCommands.InNs(ns, "wg", "show", names.WgIface, "latest-handshakes"), ct);
            if (r.ExitCode != 0) return false;
            return r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Any(l =>
            {
                var last = l.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
                return long.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out var ts) && ts > 0;
            });
        }, _t.HandshakeWait, () => "WireGuard handshake did not complete; the exit cannot be verified", ct);
    }

    /// <summary>
    /// Tears the run's network down. Idempotent, and NOT cancellable: a half-released gateway is worse than a slow release, so
    /// every teardown step runs with <see cref="CancellationToken.None"/> (each is bounded by the runner's timeout and
    /// <see cref="NetnsRouterTuning.KillGrace"/>). <paramref name="ct"/> only cuts short the optional copy of the captured
    /// flows. The run's slot is always freed.
    /// </summary>
    public Task ReleaseAsync(NetworkLease lease, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return ReleaseCoreAsync(lease.TaskId, ct);
    }

    private async Task ReleaseCoreAsync(Guid taskId, CancellationToken flowsCt)
    {
        var none = CancellationToken.None;
        var names = RunNetNames.For(taskId);
        var ns = names.Namespace;
        try
        {
            var pids = await PidsAsync(ns, none);
            foreach (var pid in pids) await Run(PrivilegedCommands.Sudo("kill", "-TERM", pid.ToString(CultureInfo.InvariantCulture)), none);
            if (pids.Count > 0)
            {
                var deadline = DateTime.UtcNow + _t.KillGrace;
                while ((pids = await PidsAsync(ns, none)).Count > 0 && DateTime.UtcNow < deadline)
                    await Task.Delay(_t.PollInterval, none);
                foreach (var pid in pids) await Run(PrivilegedCommands.Sudo("kill", "-KILL", pid.ToString(CultureInfo.InvariantCulture)), none);
            }
            // Each step tolerates "does not exist".
            await Run(PrivilegedCommands.Sudo("ip", "link", "del", names.Bridge), none);
            await Run(PrivilegedCommands.Sudo("ip", "link", "del", names.Tap), none);
            await Run(PrivilegedCommands.Sudo("ip", "link", "del", names.VethHost), none);
            await Run(PrivilegedCommands.Sudo("ip", "link", "del", names.WgIface), none);
            await Run(PrivilegedCommands.Sudo("ip", "netns", "del", ns), none);
            await KeepFlowsAsync(taskId, ns, flowsCt);
            await Run(PrivilegedCommands.Sudo("rm", "-rf", "--", MitmDir(ns)), none);
            await Run(PrivilegedCommands.Sudo("rm", "-rf", "--", InetSimDir(ns)), none); // ns is mxn<12 hex>: derived from a Guid, never free text
        }
        finally
        {
            await _gate.WaitAsync(none);
            try { _active.Remove(taskId); }
            finally { _gate.Release(); }
        }
    }

    /// <summary>Keeps the captured flows for collection; best effort, never throws.</summary>
    private async Task KeepFlowsAsync(Guid taskId, string ns, CancellationToken ct)
    {
        var dir = RunNetDir(ns);
        if (!Directory.Exists(dir)) return;
        if (File.Exists(Path.Combine(dir, FlowsStoredMarker))) return; // the capture already stored them: no second copy
        var dest = Path.Combine(dir, "flows.jsonl");
        var partial = dest + ".partial"; // copied aside, then renamed: a cancelled/failed copy never clobbers flows kept earlier
        try
        {
            // Copied as the control-plane user (a planted symlink gets no root help). The source sits in a directory the
            // hostile-traffic-facing mitm user owns: never follow links, block on it or read more than the cap.
            var res = await UntrustedFile.CopyRegularFileAsync(_t.FlowsSource(taskId), partial, MaxFlowsBytes, ct);
            File.Move(partial, dest, overwrite: true);
            if (res.Truncated) warn?.Invoke($"Flows for {taskId:N} truncated at {MaxFlowsBytes} bytes.");
        }
        catch (FileNotFoundException) { } // capture never produced a file: nothing to keep
        catch (OperationCanceledException)
        {
            warn?.Invoke($"Keeping flows for {taskId:N} was cancelled; no new flows kept.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            warn?.Invoke($"Could not keep flows for {taskId:N}: {e.Message}");
        }
        finally { TryDelete(partial); } // never leave an unflagged partial copy
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private async Task<List<int>> PidsAsync(string ns, CancellationToken ct)
    {
        var r = await Run(PrivilegedCommands.Sudo("ip", "netns", "pids", ns), ct);
        return r.ExitCode != 0 ? [] : r.Stdout.Split([' ', '\n', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? p : 0).Where(p => p > 1).ToList();
    }

    public async Task<IReadOnlyList<NetworkLease>> ListAsync(CancellationToken ct)
    {
        await ReleaseOrphansAsync(ct);
        var result = new List<NetworkLease>();
        foreach (var ns in await NamespacesAsync(ct))
        {
            var lease = ReadLease(ns);
            if (lease is null) warn?.Invoke($"Namespace {ns} has no readable lease.json; skipping.");
            else result.Add(new NetworkLease(lease.TaskId, lease.Tap));
        }
        return result;
    }

    /// <summary>Deletes <c>mxb*</c>/<c>mxt*</c>/<c>mxv*</c> host links whose run namespace no longer exists.</summary>
    public async Task ReleaseOrphansAsync(CancellationToken ct)
    {
        // Under the gate so a create in flight (links exist, namespace not yet listed) is never mistaken for an orphan.
        await _gate.WaitAsync(ct);
        try
        {
            var live = (await NamespacesAsync(ct)).Select(n => n[3..]).ToHashSet();
            foreach (var id in _active.Keys) live.Add(id.ToString("N")[..12]);
            var links = await Run(PrivilegedCommands.Sudo("ip", "-o", "link", "show"), ct);
            if (links.ExitCode != 0) return;
            foreach (var line in links.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(':', 3);
                if (parts.Length < 2) continue;
                var name = parts[1].Trim().Split('@')[0];
                var m = HostLink().Match(name);
                if (!m.Success || live.Contains(m.Groups[1].Value)) continue;
                await Run(PrivilegedCommands.Sudo("ip", "link", "del", name), ct);
            }
        }
        finally { _gate.Release(); }
    }

    private async Task<List<string>> NamespacesAsync(CancellationToken ct)
    {
        var r = await Run(PrivilegedCommands.Sudo("ip", "netns", "list"), ct);
        return r.ExitCode != 0 ? [] : r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]).Where(n => NsName().IsMatch(n)).ToList();
    }

    private async Task<(HashSet<int> Indexes, HashSet<string> Countries, HashSet<string> Existing)> ScanAsync(CancellationToken ct)
    {
        var indexes = new HashSet<int>();
        var countries = new HashSet<string>();
        var existing = new HashSet<string>();
        foreach (var ns in await NamespacesAsync(ct))
        {
            existing.Add(ns);
            var lease = ReadLease(ns);
            if (lease is null) continue;
            indexes.Add(lease.Index);
            if (lease.ExitCountry is not null) countries.Add(lease.ExitCountry);
        }
        return (indexes, countries, existing);
    }

    /// <summary>Last bytes of a daemon log, for error messages (daemon logs never contain guest data or keys).</summary>
    private static string Tail(string path)
    {
        try
        {
            var t = File.ReadAllText(path);
            return t.Length == 0 ? "" : " | " + (t.Length > 600 ? t[^600..] : t).Replace('\n', ' ');
        }
        catch (IOException) { return ""; }
    }

    private static void Chmod(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, mode);
    }

    private LeaseFile? ReadLease(string ns)
    {
        try { return JsonSerializer.Deserialize<LeaseFile>(File.ReadAllText(Path.Combine(RunNetDir(ns), "lease.json"))); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    private Task<ProcessResult> Run((string File, IReadOnlyList<string> Args) cmd, CancellationToken ct, string? stdin = null) =>
        runner.RunAsync(cmd.File, cmd.Args, ct, stdin);

    /// <param name="redactStderr">Never echo stderr (commands that handle key material).</param>
    private async Task Must((string File, IReadOnlyList<string> Args) cmd, CancellationToken ct, string? stdin = null, bool redactStderr = false)
    {
        var r = await Run(cmd, ct, stdin);
        if (r.ExitCode != 0)
            throw new EgressUnavailableException(redactStderr
                ? $"'{string.Join(' ', cmd.Args.Take(5))}' failed ({r.ExitCode}); output withheld"
                : $"'{string.Join(' ', cmd.Args.Take(8))}' failed ({r.ExitCode}): {r.Stderr.Trim()}");
    }

    private async Task<string> IdAsync(string flag, CancellationToken ct, string? user = null)
    {
        var r = await Run(("id", user is null ? [flag] : [flag, user]), ct);
        var v = r.Stdout.Trim();
        if (r.ExitCode != 0 || !uint.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            throw new EgressUnavailableException(user is null ? "Cannot determine the control-plane uid/gid." : $"User '{user}' does not exist; run ./build.sh real-setup.");
        return v;
    }

    private async Task WaitFor(Func<Task<bool>> probe, TimeSpan limit, Func<string> failure, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + limit;
        while (true)
        {
            if (await probe()) return;
            if (DateTime.UtcNow >= deadline) throw new EgressUnavailableException(failure());
            await Task.Delay(_t.PollInterval, ct);
        }
    }
}
