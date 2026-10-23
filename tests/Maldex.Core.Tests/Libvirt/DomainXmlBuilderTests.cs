using System.Xml.Linq;
using Maldex.Core.Abstractions;
using Maldex.Infrastructure.Libvirt;
using Xunit;

namespace Maldex.Core.Tests.Libvirt;

public class DomainXmlBuilderTests
{
    private static readonly Guid TaskId = Guid.Parse("0123456789abcdef0123456789abcdef");
    private static readonly string Tap = "mxt" + TaskId.ToString("N")[..12];
    private static readonly VmSpec Spec = new(TaskId, "win11-26h2", 4096, 2, Tap);
    private static readonly DomainPaths Paths = new("/run/x/overlay.qcow2", "/run/x/nvram.fd", "/run/x/agent.sock", 5901, "s3cret");
    private static readonly LibvirtOptions Options = new();

    private static XElement Domain(VmSpec? spec = null) =>
        XDocument.Parse(DomainXmlBuilder.Build(spec ?? Spec, Paths, Options)).Root!;

    [Fact]
    public void Domain_IsUefiSecureBoot()
    {
        var d = Domain();
        var os = d.Element("os")!;
        var loader = os.Element("loader")!;
        Assert.Equal("yes", loader.Attribute("readonly")!.Value);
        Assert.Equal("yes", loader.Attribute("secure")!.Value);
        Assert.Equal("pflash", loader.Attribute("type")!.Value);
        Assert.Equal(Options.OvmfCodePath, loader.Value);
        Assert.Equal(Paths.NvramPath, os.Element("nvram")!.Value);
        Assert.Equal("on", d.Element("features")!.Element("smm")!.Attribute("state")!.Value);
        Assert.StartsWith("q35", os.Element("type")!.Attribute("machine")!.Value);
    }

    [Fact]
    public void Domain_HasLibvirtManagedTpm2()
    {
        var tpm = Domain().Element("devices")!.Element("tpm")!;
        Assert.Equal("tpm-crb", tpm.Attribute("model")!.Value);
        var backend = tpm.Element("backend")!;
        Assert.Equal("emulator", backend.Attribute("type")!.Value);
        Assert.Equal("2.0", backend.Attribute("version")!.Value);
        Assert.Empty(tpm.Descendants("source"));
    }

    [Fact]
    public void Domain_HasVirtioSerialAgentChannel()
    {
        var ch = Domain().Element("devices")!.Elements("channel").Single(c => (string?)c.Attribute("type") == "unix");
        var src = ch.Element("source")!;
        Assert.Equal("bind", src.Attribute("mode")!.Value);
        Assert.Equal(Paths.AgentSocketPath, src.Attribute("path")!.Value);
        var target = ch.Element("target")!;
        Assert.Equal("virtio", target.Attribute("type")!.Value);
        Assert.Equal("org.maldex.agent", target.Attribute("name")!.Value);
    }

    [Fact]
    public void Domain_VncOnLoopbackWithPassword()
    {
        var g = Domain().Element("devices")!.Elements("graphics").Single();
        Assert.Equal("vnc", g.Attribute("type")!.Value);
        Assert.Equal("5901", g.Attribute("port")!.Value);
        Assert.Equal("no", g.Attribute("autoport")!.Value);
        Assert.Equal("127.0.0.1", g.Attribute("listen")!.Value);
        Assert.Equal("s3cret", g.Attribute("passwd")!.Value);
        var l = g.Element("listen")!;
        Assert.Equal("address", l.Attribute("type")!.Value);
        Assert.Equal("127.0.0.1", l.Attribute("address")!.Value);
    }

    [Fact]
    public void Domain_DiskIsQcow2OverlayOnVirtio()
    {
        var disk = Domain().Element("devices")!.Elements("disk").Single(x => (string?)x.Attribute("device") == "disk");
        Assert.Equal("file", disk.Attribute("type")!.Value);
        Assert.Equal("qemu", disk.Element("driver")!.Attribute("name")!.Value);
        Assert.Equal("qcow2", disk.Element("driver")!.Attribute("type")!.Value);
        Assert.Equal(Paths.OverlayDiskPath, disk.Element("source")!.Attribute("file")!.Value);
        Assert.Equal("virtio", disk.Element("target")!.Attribute("bus")!.Value);
    }

    [Fact]
    public void Domain_NicIsUnmanagedTapOnly()
    {
        var d = Domain();
        var nic = Assert.Single(d.Descendants("interface"));
        Assert.Equal("ethernet", nic.Attribute("type")!.Value);
        var target = nic.Element("target")!;
        Assert.Equal(Tap, target.Attribute("dev")!.Value);
        Assert.Equal("no", target.Attribute("managed")!.Value);
        Assert.Equal("virtio", nic.Element("model")!.Attribute("type")!.Value);
        // deterministic from the task id: a retry attempt reuses the same MAC, so the per-run DHCP reservation still matches
        Assert.Equal("52:54:00:01:23:45", nic.Element("mac")!.Attribute("address")!.Value);
        Assert.Equal(nic.ToString(), Assert.Single(Domain().Descendants("interface")).ToString());
        Assert.DoesNotContain(d.Descendants("interface"),
            i => (string?)i.Attribute("type") is "user" or "network" or "bridge");
    }

    [Fact]
    public void Domain_NameAndResources()
    {
        var d = Domain();
        Assert.Equal(VmHandle.For(TaskId).Name, d.Element("name")!.Value);
        var mem = d.Element("memory")!;
        Assert.Equal("MiB", mem.Attribute("unit")!.Value);
        Assert.Equal("4096", mem.Value);
        Assert.Equal("2", d.Element("vcpu")!.Value);
    }

    [Fact]
    public void Domain_NoHostPassthrough()
    {
        var d = Domain();
        Assert.Empty(d.Descendants("hostdev"));
        Assert.Empty(d.Descendants("filesystem"));
        Assert.Empty(d.Descendants("redirdev"));
        Assert.DoesNotContain(d.Descendants("channel"), c => (string?)c.Attribute("type") == "spicevmc");
        Assert.Equal("none", d.Element("devices")!.Element("memballoon")!.Attribute("model")!.Value);
    }

    [Theory]
    [InlineData("eth0")]
    [InlineData("mxt0123")]
    [InlineData("mxt0123456789AB")]
    [InlineData("mxb0123456789ab")]
    [InlineData("mxt0123456789abc")]
    public void Build_RejectsNetworkIdThatIsNotATap(string id) =>
        Assert.Throws<ArgumentException>(() => DomainXmlBuilder.Build(Spec with { NetworkId = id }, Paths, Options));
}
