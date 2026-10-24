namespace Maldex.Infrastructure.Net;

/// <summary>Bound from <c>Maldex:Real:Net</c>. Relative paths are resolved against the content root by the consumer.</summary>
public sealed class NetOptions
{
    public const string SectionName = "Maldex:Real:Net";

    /// <summary>The host's LAN IPs (IPv4). Guests may never reach these. DI fills this from configuration.</summary>
    public string[] ControlPlaneIps { get; set; } = [];

    public string SubnetBase { get; set; } = "10.213.0.0/16";
    public string SinkholeIp { get; set; } = "198.18.0.10";
    public int MitmPort { get; set; } = 8080;
    public int DnsPort { get; set; } = 53;
    public string InetSimBinary { get; set; } = "inetsim";
    public string MitmdumpBinary { get; set; } = "/opt/mitmproxy/mitmdump";
    public string DnsmasqBinary { get; set; } = "dnsmasq";
    public string WireGuardConfigDir { get; set; } = "/etc/maldex/wg";
    public string RunDir { get; set; } = ".data/vm/run";
    /// <summary>Fetched by the guest during the isolation self-test; the answer must match the run's network mode. The URL must return a bare IP address (the body is compared as an IP in LiveVpn mode).</summary>
    public string EchoUrl { get; set; } = "http://api.ipify.org/";

    public int MaxRuns { get; set; } = 64;

    /// <summary>The CA directory the guest image trusts (mitmproxy confdir). Resolved with Path.GetFullPath.</summary>
    public string MitmConfDir { get; set; } = ".data/mitm/ca";

    /// <summary>Directory holding the mitm addon (<c>mitm/maldex_jsonl.py</c>) and <c>inetsim/inetsim.conf</c>.</summary>
    public string DeployNetDir { get; set; } = "deploy/net";

    /// <summary>Template INetSim data (read-only content); each run gets its own copy.</summary>
    public string InetSimDataDir { get; set; } = "/var/lib/inetsim";

    /// <summary>Pool the per-run /30 uplinks of <c>Direct</c> leases are carved from (same index as the guest subnet).</summary>
    public string DirectUplinkPool { get; set; } = "100.127.0.0/16";

    /// <summary>
    /// Resolvers a <c>Direct</c> run's dnsmasq forwards to; the only private-range destinations a Direct namespace may reach, TCP/UDP 53
    /// only. Empty = the host's upstream resolvers from <c>/run/systemd/resolve/resolv.conf</c>.
    /// </summary>
    public string[] DirectDnsServers { get; set; } = [];

    /// <summary>Fetched by the guest during the Direct self-test; any HTTP answer proves a public address is reachable.</summary>
    public string DirectProbeUrl { get; set; } = "http://connectivitycheck.gstatic.com/generate_204";
}
