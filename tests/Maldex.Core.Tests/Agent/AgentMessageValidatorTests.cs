using Maldex.Core.Agent;
using Xunit;

namespace Maldex.Core.Tests.Agent;

public class AgentMessageValidatorTests
{
    private static readonly DateTimeOffset T = DateTimeOffset.UnixEpoch;

    [Fact]
    public void Ready_Pid_IsOptionalButPositive()
    {
        Assert.Null(AgentMessageValidator.Validate(new ReadyMessage("1", "h")));
        Assert.Null(AgentMessageValidator.Validate(new ReadyMessage("1", "h", 4242)));
        Assert.NotNull(AgentMessageValidator.Validate(new ReadyMessage("1", "h", 0)));
        Assert.NotNull(AgentMessageValidator.Validate(new ReadyMessage("1", "h", -5)));
    }

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

    [Fact]
    public void DnsAnswersWithNullElement_Invalid()
    {
        Assert.NotNull(AgentMessageValidator.Validate(new DnsEventMessage(T, 1, "a.com", null, ["1.1.1.1", null!])));
        Assert.Null(AgentMessageValidator.Validate(new DnsEventMessage(T, 1, "a.com", null, ["1.1.1.1"])));
    }

    [Fact]
    public void Provenance_ValidatesFields()
    {
        Assert.Null(AgentMessageValidator.Validate(new ProvenanceMessage("c", "Windows 11 Pro", "26300.1", "1.0.0+3fa9c1d2e4b5", null, null)));
        Assert.Null(AgentMessageValidator.Validate(new ProvenanceMessage("c", new string('e', 100), new string('1', 32), new string('v', 32), true, false)));
        Assert.NotNull(AgentMessageValidator.Validate(new ProvenanceMessage("", "e", "1", "v", null, null)));
        Assert.NotNull(AgentMessageValidator.Validate(new ProvenanceMessage("c", new string('e', 101), "1", "v", null, null)));
        Assert.NotNull(AgentMessageValidator.Validate(new ProvenanceMessage("c", "e", new string('1', 33), "v", null, null)));
        Assert.NotNull(AgentMessageValidator.Validate(new ProvenanceMessage("c", "e", "1", new string('v', 33), null, null)));
        Assert.NotNull(AgentMessageValidator.Validate(new ProvenanceMessage("c", "  ", "1", "v", null, null)));
        Assert.NotNull(AgentMessageValidator.Validate(new ProvenanceMessage("c", "e", "", "v", null, null)));
        Assert.NotNull(AgentMessageValidator.Validate(new ProvenanceMessage("c", "e", "1", " ", null, null)));
        Assert.NotNull(AgentMessageValidator.Validate(new ProvenanceMessage("c", "e", "1", "v\u0007x", null, null)));
        Assert.NotNull(AgentMessageValidator.Validate(new ProvenanceMessage("c", null!, "1", "v", null, null)));
    }
}
