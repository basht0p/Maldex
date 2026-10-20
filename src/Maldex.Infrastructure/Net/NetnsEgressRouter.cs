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

    private static string InetSimDir(string ns) => "/run/maldex/inetsim/" + ns;

    // mitmdump cannot traverse the owner's home (the repo and .data live there), so everything it touches is staged here.
    private static string MitmDir(string ns) => "/run/maldex/mitm/" + ns;

    /// <summary>Live flows file written by mitmdump (world-readable). After release it is copied to <c>{runNetDir}/flows.jsonl</c>.</summary>
    public static string MitmFlowsPath(Guid taskId) => MitmDir(RunNetNames.For(taskId).Namespace) + "/out/flows.jsonl";

    /// <summary>Capture is on while this file exists; the control plane owns the directory, mitmdump only stats it.</summary>
    public static string MitmCaptureEnablePath(Guid taskId) => MitmDir(RunNetNames.For(taskId).Namespace) + "/ctl/capture.on";

    /// <summary>
    /// The per-run INetSim data directory (guest uploads land in <c>http/postdata</c>, <c>ftp/upload</c> and <c>smtp</c>
    /// under it). Root-managed: read it with sudo. Removed by <see cref="ReleaseAsync"/>.
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
        Directory.CreateDirectory(dir);
        Chmod(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
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
        await Must(PrivilegedCommands.InNs(ns, [_o.DnsmasqBinary, "--conf-file=/dev/null", "--no-hosts", $"--interface={names.VethNs}", "--bind-interfaces",
            $"--dhcp-range={subnet.GuestIp},{subnet.GuestIp},255.255.255.252,12h", $"--dhcp-option=3,{gw}", $"--dhcp-option=6,{gw}",
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
        await Must(PrivilegedCommands.Sudo("install", "-d", "-m", "0755", "-o", MitmUser, "-g", MitmUser, mdir + "/out"), ct);
        await Must(PrivilegedCommands.Sudo("install", "-d", "-m", "0755", "-o", myUid, "-g", myGid, mdir + "/ctl"), ct);
        await Must(PrivilegedCommands.DetachedInNs(ns, Path.Combine(dir, "mitmdump.log"), ["setpriv", $"--reuid={MitmUser}", $"--regid={MitmUser}",
            "--clear-groups", "--no-new-privs", "--inh-caps=-all", "--bounding-set=-all", "--", _o.MitmdumpBinary,
            "--mode", "transparent", "--listen-host", gw, "--listen-port", _o.MitmPort.ToString(CultureInfo.InvariantCulture),
            "--set", $"confdir={mdir}/conf", "--set", "block_global=false", "--set", "http3=false",
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
        // INetSim's service processes drop to the unprivileged 'inetsim' user (a guest can take it over). Root therefore only
        // ever follows paths whose every parent is root-owned: the template is verified (root-owned, not writable by others,
        // no symlinks) and copied without following links, and 'inetsim' owns just the empty leaf directories below.
        var isDir = InetSimDir(ns);
        var tmpl = _o.InetSimDataDir.TrimEnd('/');
        var bad = await Run(PrivilegedCommands.Sudo("find", tmpl, "(", "-type", "l", "-o", "!", "-user", "root", "-o", "-perm", "/022", ")", "-print", "-quit"), ct);
        if (bad.ExitCode != 0 || bad.Stdout.Trim().Length > 0)
            throw new EgressUnavailableException($"INetSim template {tmpl} is not root-owned/read-only/symlink-free ({bad.Stdout.Trim()}); run ./build.sh real-setup.");
        var data = isDir + "/data";
        var log = isDir + "/log";
        await Must(PrivilegedCommands.Sudo("install", "-d", "-m", "0755", "-o", "root", "-g", "root", "/run/maldex", "/run/maldex/inetsim", isDir, data), ct);
        await Must(PrivilegedCommands.Sudo("cp", "-rP", "--no-preserve=ownership", tmpl + "/.", data + "/"), ct);
        // the TLS key must be readable by the dropped-privilege service user
        await Must(PrivilegedCommands.Sudo("chgrp", "inetsim", data + "/certs/default_key.pem"), ct);
        await Must(PrivilegedCommands.Sudo("chmod", "0640", data + "/certs/default_key.pem"), ct);
        foreach (var w in new[] { "http/postdata", "ftp/upload", "smtp" })
        {
            await Must(PrivilegedCommands.Sudo("rm", "-rf", "--", $"{data}/{w}"), ct);
            await Must(PrivilegedCommands.Sudo("install", "-d", "-m", "0750", "-o", "inetsim", "-g", "inetsim", $"{data}/{w}"), ct);
        }
        await Must(PrivilegedCommands.Sudo("install", "-d", "-m", "0750", "-o", "inetsim", "-g", "inetsim", log), ct);
        foreach (var lf in new[] { "main.log", "service.log", "debug.log" })
            await Must(PrivilegedCommands.Sudo("install", "-m", "0660", "-o", "inetsim", "-g", "inetsim", "/dev/null", $"{log}/{lf}"), ct);
        var conf = Path.GetFullPath(Path.Combine(_o.DeployNetDir, "inetsim", "inetsim.conf"));
        await Must(PrivilegedCommands.DetachedInNs(ns, Path.Combine(dir, "inetsim.out"), [_o.InetSimBinary,
            "--config", conf, "--data-dir", data, "--log-dir", log, "--report-dir", log, "--bind-address", "0.0.0.0"]), ct);
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

    public Task ReleaseAsync(NetworkLease lease, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return ReleaseCoreAsync(lease.TaskId, ct);
    }

    private async Task ReleaseCoreAsync(Guid taskId, CancellationToken ct)
    {
        var names = RunNetNames.For(taskId);
        var ns = names.Namespace;
        var pids = await PidsAsync(ns, ct);
        foreach (var pid in pids) await Run(PrivilegedCommands.Sudo("kill", "-TERM", pid.ToString(CultureInfo.InvariantCulture)), ct);
        if (pids.Count > 0)
        {
            var deadline = DateTime.UtcNow + _t.KillGrace;
            while ((pids = await PidsAsync(ns, ct)).Count > 0 && DateTime.UtcNow < deadline)
                await Task.Delay(_t.PollInterval, ct);
            foreach (var pid in pids) await Run(PrivilegedCommands.Sudo("kill", "-KILL", pid.ToString(CultureInfo.InvariantCulture)), ct);
        }
        // Each step tolerates "does not exist".
        await Run(PrivilegedCommands.Sudo("ip", "link", "del", names.Bridge), ct);
        await Run(PrivilegedCommands.Sudo("ip", "link", "del", names.Tap), ct);
        await Run(PrivilegedCommands.Sudo("ip", "link", "del", names.VethHost), ct);
        await Run(PrivilegedCommands.Sudo("ip", "link", "del", names.WgIface), ct);
        await Run(PrivilegedCommands.Sudo("ip", "netns", "del", ns), ct);
        // Keep the captured flows for collection (copied as the control-plane user, so a planted symlink gets no root help).
        try
        {
            var flows = MitmFlowsPath(taskId);
            if (File.Exists(flows) && Directory.Exists(RunNetDir(ns))) File.Copy(flows, Path.Combine(RunNetDir(ns), "flows.jsonl"), true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { warn?.Invoke($"Could not keep flows for {taskId:N}: {e.Message}"); }
        await Run(PrivilegedCommands.Sudo("rm", "-rf", "--", MitmDir(ns)), ct);
        await Run(PrivilegedCommands.Sudo("rm", "-rf", "--", InetSimDir(ns)), ct); // ns is mxn<12 hex>: derived from a Guid, never free text
        await _gate.WaitAsync(CancellationToken.None);
        try { _active.Remove(taskId); }
        finally { _gate.Release(); }
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
