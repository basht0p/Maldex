namespace Maldex.Orchestrator.Templates.Build;

/// <summary>Bound from <c>Maldex:Templates:Build</c>. File paths are resolved against the Maldex root in the Orchestrator DI; <see cref="XorrisoBinary"/> is looked up on PATH.</summary>
public sealed class BuildOptions
{
    public const string SectionName = "Maldex:Templates:Build";

    /// <summary>Where the guest writes the provisioning result marker (<c>{"ok":…,"failedSteps":[…]}</c>).</summary>
    public const string ProvisionResultGuestPath = @"C:\ProgramData\Maldex\provision-result.json";
    public const string ProvisionLogGuestPath = @"C:\ProgramData\Maldex\provision.log";

    public string VirtioIsoPath { get; set; } = ".data/vm/iso/virtio-win.iso";
    public string SysmonZipPath { get; set; } = ".data/vm/iso/Sysmon.zip";
    public string AutounattendPath { get; set; } = "deploy/guest/autounattend/Autounattend.xml";
    public string ProvisionScriptPath { get; set; } = "deploy/guest/provision/Provision.ps1";
    public string OfficeConfigPath { get; set; } = "deploy/guest/provision/office-config.xml";
    public string WorkDir { get; set; } = ".data/vm/build";
    public string JobLogDir { get; set; } = ".data/templates/jobs";

    public int DiskGiB { get; set; } = 64;
    public TimeSpan InstallTimeout { get; set; } = TimeSpan.FromHours(3);
    public TimeSpan ProvisionTimeout { get; set; } = TimeSpan.FromHours(2);
    public TimeSpan ProvisionPollInterval { get; set; } = TimeSpan.FromSeconds(15);
    /// <summary>How many times the console is sent a key at boot, so the "press any key to boot from CD" prompt is answered.</summary>
    public int BootKeyTaps { get; set; } = 25;
    public TimeSpan BootKeyInterval { get; set; } = TimeSpan.FromSeconds(2);
    public int ProvisionLogMaxBytes { get; set; } = 1024 * 1024;
    public string XorrisoBinary { get; set; } = "xorriso";
}
