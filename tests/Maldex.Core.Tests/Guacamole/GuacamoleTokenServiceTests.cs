using Maldex.Infrastructure.Guacamole;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Maldex.Core.Tests.Guacamole;

public sealed class GuacamoleTokenServiceTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly Guid _task = Guid.NewGuid();
    private readonly Guid _user = Guid.NewGuid();

    private GuacamoleTokenService Service() => new(_time, Options.Create(new GuacamoleOptions()));

    [Fact]
    public void Tunnel_Token_Expires_SingleUse_BoundToTaskAndUser()
    {
        var svc = Service();

        // Valid once, bound to the task it was issued for; the second use is rejected.
        var t1 = svc.Issue(_task, _user);
        Assert.True(svc.TryConsume(t1, _user, out var task));
        Assert.Equal(_task, task);
        Assert.False(svc.TryConsume(t1, _user, out _));

        // Expired after the lifetime (60 s default).
        var t2 = svc.Issue(_task, _user);
        _time.Advance(TimeSpan.FromSeconds(61));
        Assert.False(svc.TryConsume(t2, _user, out _));

        // Wrong user is rejected, and burns the token.
        var t3 = svc.Issue(_task, _user);
        Assert.False(svc.TryConsume(t3, Guid.NewGuid(), out _));
        Assert.False(svc.TryConsume(t3, _user, out _));

        // Unknown / empty tokens.
        Assert.False(svc.TryConsume("nope", _user, out _));
        Assert.False(svc.TryConsume("", _user, out _));
    }

    [Fact]
    public void Issue_ReturnsDistinct32ByteBase64UrlTokens()
    {
        var svc = Service();
        var a = svc.Issue(_task, _user);
        var b = svc.Issue(_task, _user);
        Assert.NotEqual(a, b);
        Assert.Equal(43, a.Length);
        Assert.DoesNotContain('+', a + b);
        Assert.DoesNotContain('/', a + b);
        Assert.DoesNotContain('=', a + b);
    }

    [Fact]
    public void Issue_PrunesExpiredTokens()
    {
        var svc = Service();
        for (var i = 0; i < 5; i++) svc.Issue(_task, _user);
        _time.Advance(TimeSpan.FromSeconds(120));
        svc.Issue(_task, _user);
        Assert.Equal(1, svc.PendingCount);
    }
}
