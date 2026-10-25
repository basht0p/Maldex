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
    /// <summary>How long writing the current agent into the working copy (libguestfs) may take.</summary>
    public TimeSpan AgentInstallTimeout { get; set; } = TimeSpan.FromMinutes(5);
    /// <summary>How long a booted working copy's agent may take to answer the provenance check (after it said ready).</summary>
    public TimeSpan ProvenanceTimeout { get; set; } = TimeSpan.FromMinutes(1);
    /// <summary>
    /// How often the provenance request is sent again until any reply arrives. After a host disconnect the agent reconnects within
    /// 1-10 s, and a request sent in that gap is lost (QEMU does not hold it).
    /// </summary>
    public TimeSpan ProvenanceRetryInterval { get; set; } = TimeSpan.FromSeconds(3);
    /// <summary>
    /// How long the Defender checks (before a seal, and the verification boot) keep reading real-time protection. Right after a boot
    /// Defender briefly reports it off before it settles on, so one reading is not enough: any "on" within the window counts.
    /// Zero takes a single reading.
    /// </summary>
    public TimeSpan DefenderSettleWindow { get; set; } = TimeSpan.FromSeconds(60);
    /// <summary>How often real-time protection is read within <see cref="DefenderSettleWindow"/>.</summary>
    public TimeSpan DefenderSettleInterval { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>
    /// How many readings (on or off, not unreadable) "off" takes: with fewer, or none near the window's end, the state did not
    /// settle (R19). Not used when <see cref="DefenderSettleWindow"/> is zero (one reading decides).
    /// </summary>
    public int DefenderSettleMinReadings { get; set; } = 8;

    /// <summary>
    /// Why the Defender settle options cannot work, or null. A zero window (one reading) is allowed only in fake mode; otherwise
    /// the window is at least 30 s, the interval positive, and the minimum at least 3 and no more than the window holds.
    /// </summary>
    public static string? DefenderSettleProblem(TemplateJobOptions o, bool fakeMode)
    {
        var key = SectionName + ":";
        if (o.DefenderSettleWindow == TimeSpan.Zero && fakeMode) return null;
        if (o.DefenderSettleWindow < TimeSpan.FromSeconds(30))
            return $"{key}DefenderSettleWindow is {o.DefenderSettleWindow.TotalSeconds:0} s; it must be at least 30 s (0 only in fake mode)";
        if (o.DefenderSettleInterval <= TimeSpan.Zero)
            return $"{key}DefenderSettleInterval is {o.DefenderSettleInterval.TotalSeconds:0} s; it must be positive";
        if (o.DefenderSettleMinReadings < 3) return $"{key}DefenderSettleMinReadings is {o.DefenderSettleMinReadings}; it must be at least 3";
        var slots = (long)(o.DefenderSettleWindow / o.DefenderSettleInterval) + 1;
        if (o.DefenderSettleMinReadings > slots)
            return $"{key}DefenderSettleMinReadings is {o.DefenderSettleMinReadings}, but a {o.DefenderSettleWindow.TotalSeconds:0} s window read every "
                + $"{o.DefenderSettleInterval.TotalSeconds:0} s holds at most {slots} readings";
        return null;
    }
    /// <summary>How long the coordinator waits for the open consoles of a job to close once a seal is requested, before sealing anyway.</summary>
    public TimeSpan ConsoleCloseTimeout { get; set; } = TimeSpan.FromSeconds(10);
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
