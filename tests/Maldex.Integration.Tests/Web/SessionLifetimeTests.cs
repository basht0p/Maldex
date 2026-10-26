using Maldex.Web.Auth;
using Xunit;

namespace Maldex.Integration.Tests.Web;

public sealed class SessionLifetimeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Just_inside_seven_days_is_not_beyond_the_cap() =>
        Assert.False(SessionLifetime.IsBeyondAbsoluteCap(Now - TimeSpan.FromDays(7) + TimeSpan.FromSeconds(1), Now));

    [Fact]
    public void Just_past_seven_days_is_beyond_the_cap() =>
        Assert.True(SessionLifetime.IsBeyondAbsoluteCap(Now - TimeSpan.FromDays(7) - TimeSpan.FromSeconds(1), Now));
}
