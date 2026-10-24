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

    [Fact]
    public async Task PoweredOffGuest_IsNotRunning_ButStaysListedUntilDestroyed()
    {
        var guest = new FakeGuest();
        var h = new FakeHypervisor(Options.Create(new FakeHypervisorOptions()), guest);
        var id = Guid.NewGuid();
        var vm = await h.StartAsync(new VmSpec(id, "win10", 4096, 2, "net"), TestContext.Current.CancellationToken);
        Assert.True(await h.IsRunningAsync(vm, TestContext.Current.CancellationToken));

        guest.NotifyPoweredOff(id);

        Assert.False(await h.IsRunningAsync(vm, TestContext.Current.CancellationToken));
        Assert.Single(await h.ListAsync(TestContext.Current.CancellationToken));
        await h.DestroyAsync(vm, TestContext.Current.CancellationToken);
        Assert.Empty(await h.ListAsync(TestContext.Current.CancellationToken));
        Assert.False(await h.IsRunningAsync(vm, TestContext.Current.CancellationToken));
    }
}
