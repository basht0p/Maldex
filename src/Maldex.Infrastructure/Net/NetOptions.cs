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
    public int MaxRuns { get; set; } = 64;
}
