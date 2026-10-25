namespace Maldex.Infrastructure.Libvirt;

/// <summary>Bound from <c>Maldex:Real:Libvirt</c>. Relative paths are resolved against the content root by the consumer.</summary>
public sealed class LibvirtOptions
{
    public const string SectionName = "Maldex:Real:Libvirt";

    public string Uri { get; set; } = "qemu:///session";
    public string VirshBinary { get; set; } = "virsh";
    public string QemuImgBinary { get; set; } = "qemu-img";
    public string GuestfishBinary { get; set; } = "guestfish";
    /// <summary>
    /// The libguestfs fixed appliance (<c>LIBGUESTFS_PATH</c>), built by <c>./build.sh real-setup</c>: Ubuntu's 0600 kernel image
    /// stops libguestfs from building its own appliance as a non-root user.
    /// </summary>
    public string GuestfsAppliancePath { get; set; } = "/var/lib/maldex/guestfs-appliance";
    public string OvmfCodePath { get; set; } = "/usr/share/OVMF/OVMF_CODE_4M.secboot.fd";
    /// <summary>The UEFI variable store a new guest starts from (Microsoft keys enrolled, as build-guest uses).</summary>
    public string OvmfVarsTemplatePath { get; set; } = "/usr/share/OVMF/OVMF_VARS_4M.ms.fd";
    public string BaseImageDir { get; set; } = ".data/vm/base";
    public string RunDir { get; set; } = ".data/vm/run";
    public string MachineType { get; set; } = "q35";
}
