using Maldex.Infrastructure.Guacamole;
using Xunit;

namespace Maldex.Core.Tests.Guacamole;

public sealed class GuacamoleProtocolTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Encode_CountsCodePointsNotUtf16Units()
    {
        Assert.Equal("6.select,3.vnc;", GuacamoleProtocol.Encode("select", "vnc"));
        // U+1F600 is one code point but two UTF-16 chars.
        Assert.Equal("4.echo,2.a\U0001F600;", GuacamoleProtocol.Encode("echo", "a\U0001F600"));
        Assert.Equal("0.,4.ping,0.;", GuacamoleProtocol.Encode("", "ping", ""));
    }

    [Fact]
    public async Task ParseAll_ReadsMultipleInstructionsAndEmptyOpcode()
    {
        var all = await GuacamoleInstructionReader.ParseAllAsync("0.,4.ping,1.5;4.sync,2.a\U0001F600;", Ct);
        Assert.Equal(2, all.Count);
        Assert.Equal(["", "ping", "5"], all[0]);
        Assert.Equal(["sync", "a\U0001F600"], all[1]);
    }

    [Theory]
    [InlineData("4.sync")]          // truncated
    [InlineData("x.sync;")]         // bad length
    [InlineData("4.sync!")]         // bad terminator
    [InlineData(".sync;")]          // missing length
    [InlineData("99999999.a;")]     // over the element cap
    public async Task ParseAll_RejectsMalformedInput(string text) =>
        await Assert.ThrowsAsync<FormatException>(() => GuacamoleInstructionReader.ParseAllAsync(text, Ct));
}
