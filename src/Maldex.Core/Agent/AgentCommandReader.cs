namespace Maldex.Core.Agent;

public abstract record CommandReadResult
{
    private CommandReadResult() { }

    public sealed record Frame(AgentCommand Command) : CommandReadResult;
    public sealed record Dropped(string Reason) : CommandReadResult;
    public sealed record Fatal(string Reason) : CommandReadResult;
    public sealed record EndOfStream : CommandReadResult
    {
        public static EndOfStream Instance { get; } = new();
    }
}

/// <summary>Guest-side reader for host commands; same framing as <see cref="AgentFrameReader"/>.</summary>
public sealed class AgentCommandReader(Stream stream)
{
    public async ValueTask<CommandReadResult> ReadAsync(CancellationToken ct)
    {
        var (status, value, reason) = await FrameCodec.ReadAsync<AgentCommand>(stream, ct).ConfigureAwait(false);
        return status switch
        {
            FrameStatus.Ok => new CommandReadResult.Frame(value!),
            FrameStatus.Dropped => new CommandReadResult.Dropped(reason!),
            FrameStatus.Fatal => new CommandReadResult.Fatal(reason!),
            _ => CommandReadResult.EndOfStream.Instance,
        };
    }
}
