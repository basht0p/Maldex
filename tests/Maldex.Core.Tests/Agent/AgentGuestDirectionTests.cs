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

    [Fact]
    public async Task ComponentCommands_RoundTrip_WithTheirWireDiscriminators()
    {
        var ms = new MemoryStream();
        await AgentFrameWriter.WriteAsync(ms, new ApplyComponentsCommand("a1", "components/agent/h1", "components/sysmon/h2", "components/ca/h3"), CancellationToken.None);
        await AgentFrameWriter.WriteAsync(ms, new PowerOffCommand("p1"), CancellationToken.None);
        var wire = System.Text.Encoding.UTF8.GetString(ms.ToArray());
        Assert.Contains("\"type\":\"apply_components\"", wire);
        Assert.Contains("\"type\":\"power_off\"", wire);
        Assert.Contains("\"agentTransferId\":\"components/agent/h1\"", wire);

        ms.Position = 0;
        var r = new AgentCommandReader(ms);
        var apply = Assert.IsType<ApplyComponentsCommand>(Assert.IsType<CommandReadResult.Frame>(await r.ReadAsync(CancellationToken.None)).Command);
        Assert.Equal(("a1", "components/agent/h1", "components/sysmon/h2", "components/ca/h3"),
            (apply.CommandId, apply.AgentTransferId, apply.SysmonConfigTransferId, apply.CaCertTransferId));
        Assert.Equal("p1", Assert.IsType<PowerOffCommand>(Assert.IsType<CommandReadResult.Frame>(await r.ReadAsync(CancellationToken.None)).Command).CommandId);
    }

    [Theory]
    [InlineData("""{"type":"apply_components","commandId":"a","agentTransferId":"components/x","sysmonConfigTransferId":"components/y"}""")] // no CA id
    [InlineData("""{"type":"apply_components","commandId":"a","agentTransferId":null,"sysmonConfigTransferId":"components/y","caCertTransferId":"components/z"}""")]
    [InlineData("""{"type":"power_off"}""")] // no command id
    public async Task ComponentCommands_MissingFields_AreDropped(string json)
    {
        var body = System.Text.Encoding.UTF8.GetBytes(json);
        var frame = new byte[4 + body.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)body.Length);
        body.CopyTo(frame, 4);
        Assert.IsType<CommandReadResult.Dropped>(await new AgentCommandReader(new MemoryStream(frame)).ReadAsync(CancellationToken.None));
    }
}
