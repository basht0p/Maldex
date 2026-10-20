namespace Maldex.Core.Agent;

public abstract record FrameReadResult
{
    private FrameReadResult() { }

    public sealed record Frame(AgentMessage Message) : FrameReadResult;
    public sealed record Dropped(string Reason) : FrameReadResult;
    public sealed record Fatal(string Reason) : FrameReadResult;
    public sealed record EndOfStream : FrameReadResult
    {
        public static EndOfStream Instance { get; } = new();
    }
}

/// <summary>
/// Reads 4-byte big-endian length-prefixed UTF-8 JSON frames from an untrusted guest.
/// Never throws on hostile input (cancellation and underlying I/O errors excepted).
/// </summary>
public sealed class AgentFrameReader(Stream stream)
{
    public const int MaxFrameBytes = 1_048_576;

    public async ValueTask<FrameReadResult> ReadAsync(CancellationToken ct)
    {
        var (status, value, reason) = await FrameCodec.ReadAsync<AgentMessage>(stream, ct).ConfigureAwait(false);
        return status switch
        {
            FrameStatus.Ok => new FrameReadResult.Frame(value!),
            FrameStatus.Dropped => new FrameReadResult.Dropped(reason!),
            FrameStatus.Fatal => new FrameReadResult.Fatal(reason!),
            _ => FrameReadResult.EndOfStream.Instance,
        };
    }
}
