using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Maldex.Infrastructure.Fakes;
using Microsoft.Extensions.Options;
using Xunit;

namespace Maldex.Core.Tests.Fakes;

public class NoopEgressRouterTests
{
    private static NoopEgressRouter Make(bool live = false) =>
        new(Options.Create(new FakeEgressOptions { LiveAvailable = live }));

    [Theory]
    [InlineData(NetworkMode.LiveVpn)]
    [InlineData(NetworkMode.LiveTor)]
    public async Task Egress_LiveUnavailable_Throws(NetworkMode mode)
    {
        var router = Make();
        var ex = await Assert.ThrowsAsync<EgressUnavailableException>(() => router.CreateAsync(Guid.NewGuid(), mode, null, TestContext.Current.CancellationToken));
        Assert.Equal("live egress is not available in fake mode", ex.Message);
        Assert.Empty(await router.ListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Egress_LiveAvailable_Succeeds()
    {
        var r = Make(live: true);
        var lease = await r.CreateAsync(Guid.NewGuid(), NetworkMode.LiveTor, "NL", TestContext.Current.CancellationToken);
        Assert.Single(await r.ListAsync(TestContext.Current.CancellationToken));
        Assert.NotEmpty(lease.NetworkId);
    }

    [Theory]
    [InlineData(NetworkMode.None)]
    [InlineData(NetworkMode.Simulated)]
    public async Task Egress_None_AndSimulated_Succeed(NetworkMode mode)
    {
        var r = Make();
        var id = Guid.NewGuid();
        var lease = await r.CreateAsync(id, mode, null, TestContext.Current.CancellationToken);
        Assert.Equal(id, lease.TaskId);
        Assert.Single(await r.ListAsync(TestContext.Current.CancellationToken));
        await r.ReleaseAsync(lease, TestContext.Current.CancellationToken);
        await r.ReleaseAsync(lease, TestContext.Current.CancellationToken);
        Assert.Empty(await r.ListAsync(TestContext.Current.CancellationToken));
    }
}
