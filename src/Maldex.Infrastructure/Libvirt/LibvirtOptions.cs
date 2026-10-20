namespace Maldex.Infrastructure.Libvirt;

/// <summary>Bound from <c>Maldex:Real:Libvirt</c>. Relative paths are resolved against the content root by the consumer.</summary>
public sealed class LibvirtOptions
{
    public const string SectionName = "Maldex:Real:Libvirt";

    public string Uri { get; set; } = "qemu:///session";
    public string VirshBinary { get; set; } = "virsh";
    public string QemuImgBinary { get; set; } = "qemu-img";
    public string OvmfCodePath { get; set; } = "/usr/share/OVMF/OVMF_CODE_4M.secboot.fd";
    public string BaseImageDir { get; set; } = ".data/vm/base";
    public string RunDir { get; set; } = ".data/vm/run";
    public string MachineType { get; set; } = "q35";
}
