using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using Maldex.Agent;
using Maldex.Agent.Collectors;
using Maldex.Agent.Commands;
using Maldex.Core.Agent;
using Xunit;

namespace Maldex.Agent.Tests;

/// <summary>Reads from one stream, writes to another: the guest's view of the channel.</summary>
internal sealed class DuplexStream(Stream read, Stream write) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => write.Flush();
    public override Task FlushAsync(CancellationToken ct) => write.FlushAsync(ct);
    public override int Read(byte[] buffer, int offset, int count) => read.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => read.ReadAsync(buffer, ct);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => write.Write(buffer, offset, count);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => write.WriteAsync(buffer, ct);
}

/// <summary>Host end of the channel plus the guest stream handed to AgentHost.</summary>
internal sealed class ChannelPair
{
    private readonly Pipe _toGuest = new();
    private readonly Pipe _toHost = new();

    public Stream GuestSide { get; }
    public AgentFrameReader HostReader { get; }
    public Stream HostWriter { get; }

    public ChannelPair()
    {
        GuestSide = new DuplexStream(_toGuest.Reader.AsStream(), _toHost.Writer.AsStream());
        HostReader = new AgentFrameReader(_toHost.Reader.AsStream());
        HostWriter = _toGuest.Writer.AsStream();
    }

    public ValueTask SendAsync(AgentCommand c) => AgentFrameWriter.WriteAsync(HostWriter, c, CancellationToken.None);

    public async Task<AgentMessage> NextAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var r = await HostReader.ReadAsync(cts.Token);
        return Assert.IsType<FrameReadResult.Frame>(r).Message;
    }

    public void CloseHostSide() => _toGuest.Writer.Complete();
}

internal sealed class ScriptedSource(params AgentMessage[] messages) : IEventSource
{
    public async IAsyncEnumerable<AgentMessage> Events([EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var m in messages)
        {
            await Task.Yield();
            yield return m;
        }
        await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }
}

internal sealed class RecordingUrlOpener : IUrlOpener
{
    public List<string> Opened { get; } = [];
    public Exception? Throw { get; set; }

    public Task OpenAsync(string url, CancellationToken ct)
    {
        if (Throw is not null) throw Throw;
        Opened.Add(url);
        return Task.CompletedTask;
    }
}

internal sealed class NoopSampleOpener : ISampleOpener
{
    public Task OpenAsync(string filePath, CancellationToken ct) => Task.CompletedTask;
}
