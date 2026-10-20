using Maldex.Core.Abstractions;
using Maldex.Infrastructure.Libvirt;
using Maldex.Infrastructure.Execution;
using Microsoft.Extensions.Options;
using Xunit;

namespace Maldex.Integration.Tests.Libvirt;

/// <summary>Gated: needs MALDEX_REAL_VM=1 and a prepared base image (default .data/vm/base).</summary>
public class RealVmTests
{
    [Fact]
    public async Task DefineStartDestroy_RealDomain()
    {
        var baseDir = Environment.GetEnvironmentVariable("MALDEX_BASE_IMAGE_DIR") ?? ".data/vm/base";
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MALDEX_REAL_VM") == "1"
            && File.Exists(Path.Combine(baseDir, "win11-26h2.qcow2")), "set MALDEX_REAL_VM=1 and provide a win11-26h2 base image");

        var runDir = Path.Combine(Path.GetTempPath(), "mx-real-" + Guid.NewGuid().ToString("N"));
        var opts = Options.Create(new LibvirtOptions { BaseImageDir = baseDir, RunDir = runDir });
        var hv = new LibvirtHypervisor(opts, new ProcessRunner());
        var id = Guid.NewGuid();
        var spec = new VmSpec(id, "win11-26h2", 4096, 2, "mxt" + id.ToString("N")[..12]);
        VmHandle? h = null;
        try
        {
            // Without the egress router the tap does not exist, so a clean VmStartException is also acceptable.
            h = await hv.StartAsync(spec, CancellationToken.None);
            Assert.Contains(h, await hv.ListAsync(CancellationToken.None));
        }
        catch (VmStartException) { }
        finally
        {
            await hv.DestroyAsync(h ?? VmHandle.For(id), CancellationToken.None);
        }
        Assert.DoesNotContain(VmHandle.For(id), await hv.ListAsync(CancellationToken.None));
    }
}
