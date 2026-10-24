namespace Maldex.Orchestrator.Templates;

/// <summary>Bound from <c>Maldex:Templates:Jobs</c>.</summary>
public sealed class TemplateJobOptions
{
    public const string SectionName = "Maldex:Templates:Jobs";

    /// <summary>The published guest agent (<c>./build.sh agent</c>).</summary>
    public string AgentExePath { get; set; } = ".data/agent/Maldex.Agent.exe";
    /// <summary>The agent's version, written next to the exe by <c>./build.sh agent</c> from the project properties the exe was compiled with.</summary>
    public string AgentVersionPath { get; set; } = ".data/agent/Maldex.Agent.version";
    public string SysmonConfigPath { get; set; } = "deploy/guest/provision/sysmon-config.xml";
    /// <summary>The mitmproxy CA certificate as PEM (created by <c>./build.sh real-setup</c> / <c>build-guest</c>).</summary>
    public string MitmCaPemPath { get; set; } = ".data/mitm/ca/mitmproxy-ca-cert.pem";

    public TimeSpan AgentReadyTimeout { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan PowerOffTimeout { get; set; } = TimeSpan.FromMinutes(3);
    public TimeSpan VerifyTimeout { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan ConfigureIdleTimeout { get; set; } = TimeSpan.FromHours(2);
    /// <summary><c>apply_components</c> clears every Windows event log (about 1000 channels, about a minute), so its reply gets a generous timeout.</summary>
    public TimeSpan ApplyComponentsTimeout { get; set; } = TimeSpan.FromMinutes(10);
    /// <summary>How long the guest may take to acknowledge one chunk of a component file.</summary>
    public TimeSpan ChunkTimeout { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>How often the hypervisor is asked whether a powered-off guest is gone.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
    public int MinFreeGiB { get; set; } = 30;
}

/// <summary>The template could not be sealed. The message is safe to store as the job's failure reason.</summary>
public sealed class TemplateSealException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The sealed template failed verification. The message is safe to store as the job's failure reason.</summary>
public sealed class TemplateVerifyException(string message) : Exception(message);

/// <summary>What a verification boot learned about the sealed disk. <c>Note</c> is set when something could not be determined.</summary>
public sealed record VerifiedProvenance(string WindowsEdition, string WindowsBuild, string AgentVersion, bool? OfficePresent, bool? RealTimeProtectionEnabled, string? Note);
