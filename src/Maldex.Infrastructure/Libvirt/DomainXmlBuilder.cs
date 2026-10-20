using System.Text.RegularExpressions;
using System.Xml.Linq;
using Maldex.Core.Abstractions;

namespace Maldex.Infrastructure.Libvirt;

public sealed record DomainPaths(string OverlayDiskPath, string NvramPath, string AgentSocketPath, int VncPort, string VncPassword);

/// <summary>Builds the libvirt domain XML for the Windows 11 guest (UEFI Secure Boot, managed TPM 2.0, tap-only NIC).</summary>
public static partial class DomainXmlBuilder
{
    public const string AgentChannelName = "org.maldex.agent";

    [GeneratedRegex("^mxt[0-9a-f]{12}$")]
    private static partial Regex TapName();

    public static string Build(VmSpec spec, DomainPaths paths, LibvirtOptions options)
    {
        if (spec.NetworkId is null || !TapName().IsMatch(spec.NetworkId))
            throw new ArgumentException($"NetworkId must be a tap name (mxt + 12 hex), got '{spec.NetworkId}'.", nameof(spec));

        var domain = new XElement("domain", new XAttribute("type", "kvm"),
            new XElement("name", VmHandle.For(spec.TaskId).Name),
            new XElement("memory", new XAttribute("unit", "MiB"), spec.MemoryMiB),
            new XElement("vcpu", new XAttribute("placement", "static"), spec.VCpus),
            new XElement("os",
                new XElement("type", new XAttribute("arch", "x86_64"), new XAttribute("machine", options.MachineType), "hvm"),
                new XElement("loader",
                    new XAttribute("readonly", "yes"), new XAttribute("secure", "yes"), new XAttribute("type", "pflash"),
                    options.OvmfCodePath),
                new XElement("nvram", paths.NvramPath),
                new XElement("boot", new XAttribute("dev", "hd"))),
            new XElement("features",
                new XElement("acpi"),
                new XElement("apic"),
                new XElement("hyperv",
                    new XAttribute("mode", "custom"),
                    new XElement("relaxed", new XAttribute("state", "on")),
                    new XElement("vapic", new XAttribute("state", "on")),
                    new XElement("spinlocks", new XAttribute("state", "on"), new XAttribute("retries", "8191")),
                    new XElement("vpindex", new XAttribute("state", "on")),
                    new XElement("synic", new XAttribute("state", "on")),
                    new XElement("stimer", new XAttribute("state", "on"))),
                new XElement("smm", new XAttribute("state", "on"))),
            new XElement("cpu", new XAttribute("mode", "host-passthrough"), new XAttribute("check", "none")),
            new XElement("clock", new XAttribute("offset", "localtime"),
                new XElement("timer", new XAttribute("name", "hypervclock"), new XAttribute("present", "yes"))),
            new XElement("on_poweroff", "destroy"),
            new XElement("on_reboot", "restart"),
            new XElement("on_crash", "destroy"),
            new XElement("devices",
                new XElement("emulator", "/usr/bin/qemu-system-x86_64"),
                new XElement("disk", new XAttribute("type", "file"), new XAttribute("device", "disk"),
                    new XElement("driver", new XAttribute("name", "qemu"), new XAttribute("type", "qcow2")),
                    new XElement("source", new XAttribute("file", paths.OverlayDiskPath)),
                    new XElement("target", new XAttribute("dev", "vda"), new XAttribute("bus", "virtio"))),
                new XElement("interface", new XAttribute("type", "ethernet"),
                    new XElement("target", new XAttribute("dev", spec.NetworkId), new XAttribute("managed", "no")),
                    new XElement("model", new XAttribute("type", "virtio"))),
                new XElement("channel", new XAttribute("type", "unix"),
                    new XElement("source", new XAttribute("mode", "bind"), new XAttribute("path", paths.AgentSocketPath)),
                    new XElement("target", new XAttribute("type", "virtio"), new XAttribute("name", AgentChannelName))),
                new XElement("tpm", new XAttribute("model", "tpm-crb"),
                    new XElement("backend", new XAttribute("type", "emulator"), new XAttribute("version", "2.0"))),
                new XElement("graphics", new XAttribute("type", "vnc"),
                    new XAttribute("port", paths.VncPort), new XAttribute("autoport", "no"),
                    new XAttribute("listen", "127.0.0.1"), new XAttribute("passwd", paths.VncPassword),
                    new XElement("listen", new XAttribute("type", "address"), new XAttribute("address", "127.0.0.1"))),
                new XElement("video", new XElement("model", new XAttribute("type", "vga"), new XAttribute("vram", "16384"))),
                new XElement("input", new XAttribute("type", "tablet"), new XAttribute("bus", "usb")),
                new XElement("memballoon", new XAttribute("model", "none"))));

        return new XDocument(domain).ToString();
    }
}
