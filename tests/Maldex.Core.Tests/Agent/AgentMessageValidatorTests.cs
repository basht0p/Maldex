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

    private const string GoodV = "1.0.0+3fa9c1d2e4b5";

    [Fact]
    public void Provenance_ValidatesFields()
    {
        Assert.Null(AgentMessageValidator.Validate(new ProvenanceMessage("c", "Windows 11 Pro", "26300.1", GoodV, null, null)));
        Assert.Null(AgentMessageValidator.Validate(new ProvenanceMessage("c", new string('e', 100), new string('1', 32), "1.0.0-preview.1+3fa9c1d2e4b5", true, false)));
        Assert.NotNull(AgentMessageValidator.Validate(new ProvenanceMessage("", "e", "1", GoodV, null, null)));
        Assert.NotNull(AgentMessageValidator.Validate(new ProvenanceMessage("c", new string('e', 101), "1", GoodV, null, null)));
        Assert.NotNull(AgentMessageValidator.Validate(new ProvenanceMessage("c", "e", new string('1', 33), GoodV, null, null)));
        Assert.NotNull(AgentMessageValidator.Validate(new ProvenanceMessage("c", "  ", "1", GoodV, null, null)));
        Assert.NotNull(AgentMessageValidator.Validate(new ProvenanceMessage("c", "e", "", GoodV, null, null)));
        Assert.NotNull(AgentMessageValidator.Validate(new ProvenanceMessage("c", null!, "1", GoodV, null, null)));
    }

    [Theory]
    [InlineData("Windows \u202E11")]
    [InlineData("Windows\u200B11")]
    [InlineData("Windows\u0007")]
    [InlineData(" x")]
    [InlineData("x ")]
    public void Provenance_RejectsUnsafeText(string bad)
    {
        Assert.NotNull(AgentMessageValidator.Validate(new ProvenanceMessage("c", bad, "1", GoodV, null, null)));
        Assert.NotNull(AgentMessageValidator.Validate(new ProvenanceMessage("c", "e", bad, GoodV, null, null)));
    }

    [Fact]
    public void Provenance_RejectsUnpairedSurrogates_AcceptsPairs()
    {
        Assert.NotNull(AgentMessageValidator.Validate(new ProvenanceMessage("c", "Windows " + (char)0xD800, "1", GoodV, null, null)));
        Assert.NotNull(AgentMessageValidator.Validate(new ProvenanceMessage("c", "e", ((char)0xDC00).ToString(), GoodV, null, null)));
        Assert.Null(AgentMessageValidator.Validate(new ProvenanceMessage("c", "Windows \U0001F600", "1", GoodV, null, null)));
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("1.0.0+3FA9C1D2E4B5")]
    [InlineData("1.0.0+3fa9c1d2e4b")]
    [InlineData("1.0.0+3fa9c1d2e4b5x")]
    [InlineData("+3fa9c1d2e4b5")]
    [InlineData("1.0.0 +3fa9c1d2e4b5")]
    [InlineData("12345678901234567890+3fa9c1d2e4b5")]
    public void Provenance_AgentVersion_MustBeVersionPlusHash(string v) =>
        Assert.NotNull(AgentMessageValidator.Validate(new ProvenanceMessage("c", "e", "1", v, null, null)));
}
