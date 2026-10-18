using Maldex.Core.Agent;
using Xunit;

namespace Maldex.Core.Tests.Agent;

public class AgentMessageValidatorTests
{
    private static readonly DateTimeOffset T = DateTimeOffset.UnixEpoch;

    [Fact]
    public void Valid_ReturnsNull()
    {
        Assert.Null(AgentMessageValidator.Validate(new ProcessStartedMessage(T, 1, 0, "c:\\a.exe", "", null, null)));
        Assert.Null(AgentMessageValidator.Validate(new FileEventMessage(T, 1, "write", "c:\\x", null)));
        Assert.Null(AgentMessageValidator.Validate(new RegistryEventMessage(T, 1, "set", "HKLM\\x", null, null)));
        Assert.Null(AgentMessageValidator.Validate(new NetworkEventMessage(T, 1, "tcp", "1.2.3.4", 443, null, 0)));
        Assert.Null(AgentMessageValidator.Validate(new ScreenshotMessage(T, "png", "AAAA")));
    }

    [Fact]
    public void NegativePid_Invalid()
    {
        Assert.NotNull(AgentMessageValidator.Validate(new ProcessExitedMessage(T, -1, 0)));
        Assert.NotNull(AgentMessageValidator.Validate(new ProcessStartedMessage(T, 1, -1, "a", "", null, null)));
    }

    [Fact]
    public void BadOperation_Invalid()
    {
        Assert.NotNull(AgentMessageValidator.Validate(new FileEventMessage(T, 1, "WRITE", "p", null)));
        Assert.NotNull(AgentMessageValidator.Validate(new FileEventMessage(T, 1, "chmod", "p", null)));
        Assert.NotNull(AgentMessageValidator.Validate(new RegistryEventMessage(T, 1, "rename", "k", null, null)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    [InlineData(-5)]
    public void PortOutOfRange_Invalid(int port)
    {
        Assert.NotNull(AgentMessageValidator.Validate(new NetworkEventMessage(T, 1, "tcp", "1.1.1.1", port, null, null)));
    }

    [Fact]
    public void BadLocalPortOrProtocol_Invalid()
    {
        Assert.NotNull(AgentMessageValidator.Validate(new NetworkEventMessage(T, 1, "tcp", "1.1.1.1", 80, null, 70000)));
        Assert.NotNull(AgentMessageValidator.Validate(new NetworkEventMessage(T, 1, "icmp", "1.1.1.1", 80, null, null)));
    }

    [Fact]
    public void NullRequiredString_Invalid()
    {
        Assert.NotNull(AgentMessageValidator.Validate(new DnsEventMessage(T, 1, null!, null, null)));
    }

    [Theory]
    [InlineData("!!notbase64!!")]
    [InlineData("")]
    public void ScreenshotNotBase64_Invalid(string data)
    {
        Assert.NotNull(AgentMessageValidator.Validate(new ScreenshotMessage(T, "png", data)));
    }

    [Fact]
    public void ScreenshotBadFormat_Invalid()
    {
        Assert.NotNull(AgentMessageValidator.Validate(new ScreenshotMessage(T, "gif", "AAAA")));
    }

    [Fact]
    public void Tracker_TripsAtTen()
    {
        var t = new AgentViolationTracker();
        for (var i = 0; i < 9; i++) Assert.False(t.Record("x"));
        Assert.True(t.Record("x"));
        Assert.Equal(10, t.Count);
    }
}
