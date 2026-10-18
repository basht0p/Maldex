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

    private static readonly string[] ValidBodies =
    [
        Ready,
        """{"type":"process_started","at":"2026-01-01T00:00:00Z","pid":4,"parentPid":1,"imagePath":"c:\\a.exe","commandLine":"a","user":"u","integrity":"high"}""",
        """{"type":"process_exited","at":"2026-01-01T00:00:00Z","pid":4,"exitCode":0}""",
        """{"type":"file","at":"2026-01-01T00:00:00Z","pid":4,"operation":"write","path":"c:\\x","newPath":null}""",
        """{"type":"registry","at":"2026-01-01T00:00:00Z","pid":4,"operation":"set","key":"HKLM\\k","valueName":"v","data":"d"}""",
        """{"type":"network","at":"2026-01-01T00:00:00Z","pid":4,"protocol":"tcp","remoteAddress":"1.2.3.4","remotePort":443,"localAddress":null,"localPort":5}""",
        """{"type":"dns","at":"2026-01-01T00:00:00Z","pid":4,"query":"a.com","queryType":"A","answers":["1.1.1.1"]}""",
        """{"type":"screenshot","at":"2026-01-01T00:00:00Z","format":"png","imageBase64":"AAAA"}""",
        """{"type":"command_result","commandId":"c","success":true,"error":null}""",
    ];

    [Fact]
    public async Task ValidBodies_AllRead()
    {
        foreach (var b in ValidBodies)
            Assert.IsType<FrameReadResult.Frame>(await new AgentFrameReader(Concat(Frame(b))).ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task MutationFuzz_NeverThrows_AndNextFrameStillReads()
    {
        var rnd = new Random(1234);
        var good = Frame(Ready);
        for (var i = 0; i < 6000; i++)
        {
            var body = Encoding.UTF8.GetBytes(ValidBodies[i % ValidBodies.Length]);
            var edits = rnd.Next(1, 6);
            for (var e = 0; e < edits; e++)
            {
                switch (rnd.Next(3))
                {
                    case 0: body[rnd.Next(body.Length)] = (byte)rnd.Next(256); break;
                    case 1: body[rnd.Next(body.Length)] ^= (byte)(1 << rnd.Next(8)); break;
                    default:
                        if (body.Length > 2) body = body[..rnd.Next(1, body.Length)];
                        break;
                }
            }
            var hdr = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(hdr, (uint)body.Length);
            var r = new AgentFrameReader(Concat(hdr, body, good));
            var first = await r.ReadAsync(CancellationToken.None);
            Assert.True(first is FrameReadResult.Frame or FrameReadResult.Dropped);
            Assert.IsType<FrameReadResult.Frame>(await r.ReadAsync(CancellationToken.None));
        }
    }

    private static byte[] RawFrame(byte[] body)
    {
        var buf = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(buf, (uint)body.Length);
        body.CopyTo(buf, 4);
        return buf;
    }

    public static TheoryData<string> HostileBodies() => new()
    {
        "{\"type\":\"ready\",\"agentVersion\":\"1\",\"hostname\":\"h\",\"x\":" + new string('[', 100) + new string(']', 100) + "}",
        "{\"type\":5,\"agentVersion\":\"1\",\"hostname\":\"h\"}",
        "{\"type\":\"ready\",\"agentVersion\":\"1\",\"hostname\":null}",
        "{\"type\":\"process_exited\",\"at\":\"2026-01-01T00:00:00Z\",\"exitCode\":0}",
        "{\"type\":\"command_result\",\"commandId\":\"c\"}",
        "{\"type\":\"process_exited\",\"at\":\"not a date\",\"pid\":1,\"exitCode\":0}",
        "{\"type\":\"process_exited\",\"at\":\"2026-01-01T00:00:00Z\",\"pid\":99999999999999,\"exitCode\":0}",
        "{\"type\":\"process_exited\",\"at\":\"2026-01-01T00:00:00Z\",\"pid\":\"7\",\"exitCode\":0}",
        "{\"Type\":\"ready\",\"AgentVersion\":\"1\",\"Hostname\":\"h\"}",
    };

    [Theory]
    [MemberData(nameof(HostileBodies))]
    public async Task HostileBodies_AreDropped(string json)
    {
        var r = new AgentFrameReader(Concat(Frame(json), Frame(Ready)));
        Assert.IsType<FrameReadResult.Dropped>(await r.ReadAsync(CancellationToken.None));
        Assert.IsType<FrameReadResult.Frame>(await r.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task InvalidUtf8_IsDropped()
    {
        byte[] body = [.. "{\"type\":\"ready\",\"agentVersion\":\""u8, 0xFF, 0xC0, 0x80, .. "\",\"hostname\":\"h\"}"u8];
        var r = new AgentFrameReader(Concat(RawFrame(body), Frame(Ready)));
        Assert.IsType<FrameReadResult.Dropped>(await r.ReadAsync(CancellationToken.None));
        Assert.IsType<FrameReadResult.Frame>(await r.ReadAsync(CancellationToken.None));
    }
}
