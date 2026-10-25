using Maldex.Infrastructure.Fakes;
using Xunit;

namespace Maldex.Core.Tests.Fakes;

public class FakeTemplateStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CreateBlank_OverAWorkingCopy_IsRefused_AsTheRealStoreRefusesAnyExistingStem()
    {
        var s = new FakeTemplateStore();
        await s.CreateWorkingCopyAsync("base", "copy", Ct);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => s.CreateBlankAsync("copy", 1024, "vars.fd", Ct));
        Assert.Equal("Template file stem 'copy' already exists.", ex.Message);
        Assert.Empty(s.Blanks);
    }

    [Fact]
    public async Task CreateBlank_OverItsOwnBlank_IsRefused_ButAfterADeleteItCanBeCreatedAgain()
    {
        var s = new FakeTemplateStore();
        await s.CreateBlankAsync("new", 1024, "vars.fd", Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.CreateBlankAsync("new", 1024, "vars.fd", Ct));

        await s.DeleteAsync("new", Ct);
        await s.CreateBlankAsync("new", 1024, "vars.fd", Ct);
        Assert.Equal(2, s.Blanks.Count);
    }
}
