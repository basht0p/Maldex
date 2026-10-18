using System.Buffers.Binary;
using System.Text;
using Maldex.Core.Agent;
using Xunit;

namespace Maldex.Core.Tests.Agent;

public class AgentFrameReaderTests
{
    private static byte[] Frame(string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var buf = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(buf, (uint)body.Length);
        body.CopyTo(buf, 4);
        return buf;
    }

    private static MemoryStream Concat(params byte[][] parts)
    {
        var ms = new MemoryStream();
        foreach (var p in parts) ms.Write(p);
        ms.Position = 0;
        return ms;
    }

    private const string Ready = """{"type":"ready","agentVersion":"1.0","hostname":"vm"}""";

    [Fact]
    public async Task ReadsValidFrame()
    {
        var r = new AgentFrameReader(Concat(Frame(Ready)));
        var res = await r.ReadAsync(CancellationToken.None);
        var f = Assert.IsType<FrameReadResult.Frame>(res);
        var m = Assert.IsType<ReadyMessage>(f.Message);
        Assert.Equal("vm", m.Hostname);
        Assert.IsType<FrameReadResult.EndOfStream>(await r.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TypeNotFirst_StillReads()
    {
        var r = new AgentFrameReader(Concat(Frame("""{"hostname":"vm","agentVersion":"1","type":"ready"}""")));
        Assert.IsType<FrameReadResult.Frame>(await r.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task LengthOverMax_IsFatal()
    {
        var r = new AgentFrameReader(Concat([0x00, 0x10, 0x00, 0x01]));
        Assert.IsType<FrameReadResult.Fatal>(await r.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TruncatedBody_IsEndOfStream()
    {
        var f = Frame(Ready);
        var r = new AgentFrameReader(Concat(f[..^3]));
        Assert.IsType<FrameReadResult.EndOfStream>(await r.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TruncatedHeader_IsEndOfStream()
    {
        var r = new AgentFrameReader(Concat([0x00, 0x00]));
        Assert.IsType<FrameReadResult.EndOfStream>(await r.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task MalformedJson_IsDropped_AndNextFrameStillReads()
    {
        var r = new AgentFrameReader(Concat(Frame("{not json"), Frame(Ready)));
        Assert.IsType<FrameReadResult.Dropped>(await r.ReadAsync(CancellationToken.None));
        Assert.IsType<FrameReadResult.Frame>(await r.ReadAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("""{"type":"bogus"}""")]
    [InlineData("""{"hostname":"x"}""")]
    [InlineData("null")]
    [InlineData("""{"type":"ready","hostname":"x"}""")]
    public async Task BadShapes_AreDropped(string json)
    {
        var r = new AgentFrameReader(Concat(Frame(json), Frame(Ready)));
        Assert.IsType<FrameReadResult.Dropped>(await r.ReadAsync(CancellationToken.None));
        Assert.IsType<FrameReadResult.Frame>(await r.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task UnknownType_IsDropped()
    {
        var r = new AgentFrameReader(Concat(Frame("""{"type":"nope","x":1}""")));
        Assert.IsType<FrameReadResult.Dropped>(await r.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ZeroLength_IsDropped()
    {
        var r = new AgentFrameReader(Concat([0, 0, 0, 0], Frame(Ready)));
        Assert.IsType<FrameReadResult.Dropped>(await r.ReadAsync(CancellationToken.None));
        Assert.IsType<FrameReadResult.Frame>(await r.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RandomBytes_NeverThrows()
    {
        var rnd = new Random(42);
        for (var i = 0; i < 500; i++)
        {
            var data = new byte[rnd.Next(0, 300)];
            rnd.NextBytes(data);
            var r = new AgentFrameReader(new MemoryStream(data));
            for (var n = 0; n < 50; n++)
            {
                var res = await r.ReadAsync(CancellationToken.None);
                Assert.True(res is FrameReadResult.Frame or FrameReadResult.Dropped
                    or FrameReadResult.Fatal or FrameReadResult.EndOfStream);
                if (res is FrameReadResult.Fatal or FrameReadResult.EndOfStream) break;
            }
        }
    }

    [Fact]
    public async Task Writer_RoundTripsLengthAndType()
    {
        var ms = new MemoryStream();
        await AgentFrameWriter.WriteAsync(ms, new ShutdownCommand("c1"), CancellationToken.None);
        var b = ms.ToArray();
        Assert.Equal((uint)(b.Length - 4), BinaryPrimitives.ReadUInt32BigEndian(b));
        var json = Encoding.UTF8.GetString(b, 4, b.Length - 4);
        Assert.Contains("\"type\":\"shutdown\"", json);
        Assert.Contains("\"commandId\":\"c1\"", json);
    }

    [Fact]
    public async Task Writer_RefusesOversize()
    {
        var big = new OpenUrlCommand("c", new string('a', AgentFrameReader.MaxFrameBytes));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AgentFrameWriter.WriteAsync(new MemoryStream(), big, CancellationToken.None).AsTask());
    }
}
