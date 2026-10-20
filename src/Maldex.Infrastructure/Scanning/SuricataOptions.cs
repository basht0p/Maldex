namespace Maldex.Infrastructure.Scanning;

/// <summary>Bound from <c>Maldex:Real:Suricata</c>.</summary>
public sealed class SuricataOptions
{
    public const string SectionName = "Maldex:Real:Suricata";

    /// <summary>The suricata executable (a name on PATH or a full path).</summary>
    public string Binary { get; set; } = "suricata";

    public string ConfigPath { get; set; } = "deploy/net/suricata/suricata.yaml";

    public string RulesPath { get; set; } = "deploy/net/suricata/maldex-dev.rules";

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(120);
}
