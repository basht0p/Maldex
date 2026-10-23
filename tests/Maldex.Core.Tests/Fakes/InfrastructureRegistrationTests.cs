using Maldex.Core.Abstractions;
using Maldex.Infrastructure;
using Maldex.Infrastructure.Fakes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Maldex.Core.Tests.Fakes;

public class InfrastructureRegistrationTests
{
    private static IServiceCollection Add(string? mode)
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(mode is null ? [] : new Dictionary<string, string?> { ["Maldex:Mode"] = mode })
            .Build();
        return new ServiceCollection().AddMaldexInfrastructure(cfg);
    }

    [Theory]
    [InlineData("Fake")]
    [InlineData("")]
    [InlineData(null)]
    public void FakeMode_RegistersFakes(string? mode)
    {
        using var sp = Add(mode).BuildServiceProvider();
        Assert.IsType<FakeHypervisor>(sp.GetRequiredService<IHypervisor>());
        Assert.IsType<NoopEgressRouter>(sp.GetRequiredService<IEgressRouter>());
        Assert.IsType<NoopCaptureService>(sp.GetRequiredService<ICaptureService>());
        Assert.IsType<FakeAgentChannelFactory>(sp.GetRequiredService<IAgentChannelFactory>());
        Assert.NotNull(sp.GetRequiredService<IDiskSpaceProbe>());
        Assert.Same(sp.GetRequiredService<IHypervisor>(), sp.GetRequiredService<IHypervisor>());
    }

    [Fact]
    public void UnknownMode_Throws() => Assert.Throws<InvalidOperationException>(() => Add("Bogus"));

    [Fact]
    public void DiskSpaceProbe_ReturnsPositive()
    {
        var dir = Path.Combine(Path.GetTempPath(), "maldex-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.True(new Maldex.Infrastructure.Storage.DiskSpaceProbe().FreeMiB(dir) > 0);
            Assert.True(Directory.Exists(dir));
        }
        finally { Directory.Delete(dir, true); }
    }
}
