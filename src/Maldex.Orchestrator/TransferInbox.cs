using System.Threading.Channels;
using Maldex.Core.Agent;

namespace Maldex.Orchestrator;

/// <summary>
/// Routes the control-plane replies of a bulk transfer (<see cref="CommandResultMessage"/>, <see cref="FileChunkMessage"/> and <see cref="SelfTestResultMessage"/>)
/// from the run's single read loop to whoever is waiting for them, and remembers which files the guest created.
/// Only one transfer activity at a time opens the inbox. File chunks nobody asked for are swallowed, never ingested.
/// </summary>
public sealed class TransferInbox
{
    // Up to this many 512 KiB chunks may wait for the consumer; beyond that the read loop waits (back-pressure).
    private const int Capacity = 8;
    private const int MaxTrackedPaths = 1000;

    /// <summary>Longer paths are never asked for: the <c>collect_file</c> command must fit one frame (a compromised agent can report a path of any length).</summary>
    public const int MaxPathChars = 32_767;

    private readonly object _gate = new();
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _created = [];
    private Channel<AgentMessage>? _open;
    private bool _ended;

    /// <summary>Paths of files the guest reported creating, in order, de-duplicated case-insensitively.</summary>
    public IReadOnlyList<string> CreatedPaths
    {
        get { lock (_gate) return _created.ToArray(); }
    }

    /// <summary>Starts routing replies to the returned reader. Dispose the lease to stop.</summary>
    public Lease Open()
    {
        var channel = Channel.CreateBounded<AgentMessage>(new BoundedChannelOptions(Capacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        lock (_gate)
        {
            if (_open is not null) throw new InvalidOperationException("A transfer is already in progress.");
            _open = channel;
            if (_ended) channel.Writer.TryComplete();
        }
        return new Lease(this, channel);
    }

    /// <summary>The read loop calls this for every validated message. Returns true when the message was consumed here.</summary>
    public async ValueTask<bool> OfferAsync(AgentMessage message, CancellationToken ct)
    {
        if (message is FileEventMessage { Operation: "create" } f) Track(f.Path);
        if (message is not (CommandResultMessage or FileChunkMessage or SelfTestResultMessage)) return false;

        Channel<AgentMessage>? open;
        lock (_gate) open = _open;
        if (open is null) return message is FileChunkMessage or SelfTestResultMessage; // unsolicited chunks and self-test answers are dropped; a stray command result goes on to the ingestor

        try
        {
            await open.Writer.WriteAsync(message, ct).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            // The consumer just finished; nothing is waiting for this any more.
        }
        return true;
    }

    /// <summary>Tells a waiting consumer that no more messages will arrive (the read loop ended).</summary>
    public void Complete(Exception? error = null)
    {
        Channel<AgentMessage>? open;
        lock (_gate)
        {
            _ended = true;
            open = _open;
        }
        open?.Writer.TryComplete(error);
    }

    private void Track(string path)
    {
        lock (_gate)
        {
            if (_created.Count < MaxTrackedPaths && !string.IsNullOrWhiteSpace(path) && path.Length <= MaxPathChars && _seen.Add(path)) _created.Add(path);
        }
    }

    public sealed class Lease : IDisposable
    {
        private readonly TransferInbox _owner;

        internal Lease(TransferInbox owner, Channel<AgentMessage> channel)
        {
            _owner = owner;
            Reader = channel.Reader;
            Channel = channel;
        }

        public ChannelReader<AgentMessage> Reader { get; }

        private Channel<AgentMessage> Channel { get; }

        public void Dispose()
        {
            lock (_owner._gate)
            {
                if (ReferenceEquals(_owner._open, Channel)) _owner._open = null;
            }
            Channel.Writer.TryComplete();
        }
    }
}
