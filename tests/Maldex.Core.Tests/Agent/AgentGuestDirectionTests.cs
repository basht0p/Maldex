using Maldex.Core.Agent;
using Xunit;

namespace Maldex.Core.Tests.Agent;

public class AgentGuestDirectionTests
{
    [Fact]
    public async Task MessageWriter_RoundTripsThroughHostReader()
    {
        var ms = new MemoryStream();
        await AgentFrameWriter.WriteAsync(ms, new ReadyMessage("1.0", "vm"), CancellationToken.None);
        ms.Position = 0;
        var res = await new AgentFrameReader(ms).ReadAsync(CancellationToken.None);
        var f = Assert.IsType<FrameReadResult.Frame>(res);
        Assert.Equal("vm", Assert.IsType<ReadyMessage>(f.Message).Hostname);
    }

    [Fact]
    public async Task CommandReader_RoundTripsThroughHostWriter()
    {
        var ms = new MemoryStream();
        await AgentFrameWriter.WriteAsync(ms, new OpenUrlCommand("c1", "http://x/"), CancellationToken.None);
        ms.Position = 0;
        var r = new AgentCommandReader(ms);
        var f = Assert.IsType<CommandReadResult.Frame>(await r.ReadAsync(CancellationToken.None));
        Assert.Equal("http://x/", Assert.IsType<OpenUrlCommand>(f.Command).Url);
        Assert.IsType<CommandReadResult.EndOfStream>(await r.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CommandReader_MalformedIsDropped_OversizeIsFatal()
    {
        var bad = new byte[] { 0, 0, 0, 2, (byte)'{', (byte)'}' };
        Assert.IsType<CommandReadResult.Dropped>(await new AgentCommandReader(new MemoryStream(bad)).ReadAsync(CancellationToken.None));
        var huge = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
        Assert.IsType<CommandReadResult.Fatal>(await new AgentCommandReader(new MemoryStream(huge)).ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task MessageWriter_RejectsOversize()
    {
        var big = new ScreenshotMessage(DateTimeOffset.UnixEpoch, "png", new string('A', AgentFrameReader.MaxFrameBytes));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await AgentFrameWriter.WriteAsync(new MemoryStream(), big, CancellationToken.None));
    }
}
