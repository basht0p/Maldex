using Maldex.Core.Abstractions;
using Maldex.Infrastructure.Fakes;
using Microsoft.Extensions.Options;
using Xunit;

namespace Maldex.Core.Tests.Fakes;

public class FakeHypervisorTests
{
    [Fact]
    public async Task FailStarts_ThrowsForFirstN_ThenSucceeds_AndDestroyIsIdempotent()
    {
        var h = new FakeHypervisor(Options.Create(new FakeHypervisorOptions { FailStarts = 2 }));
        var id = Guid.NewGuid();
        var spec = new VmSpec(id, "win10", 4096, 2, "net");
        await Assert.ThrowsAsync<VmStartException>(() => h.StartAsync(spec, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<VmStartException>(() => h.StartAsync(spec, TestContext.Current.CancellationToken));
        var vm = await h.StartAsync(spec, TestContext.Current.CancellationToken);
        Assert.Equal($"maldex-{id:N}", vm.Name);
        Assert.Single(await h.ListAsync(TestContext.Current.CancellationToken));
        await h.DestroyAsync(vm, TestContext.Current.CancellationToken);
        await h.DestroyAsync(vm, TestContext.Current.CancellationToken);
        Assert.Empty(await h.ListAsync(TestContext.Current.CancellationToken));
    }
}
