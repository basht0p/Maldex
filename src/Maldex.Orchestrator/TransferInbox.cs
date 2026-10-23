using System.Threading.Channels;
using Maldex.Core.Agent;

namespace Maldex.Orchestrator;

/// <summary>
/// Routes the control-plane replies of a bulk transfer (<see cref="CommandResultMessage"/>, <see cref="FileChunkMessage"/> and <see cref="SelfTestResultMessage"/>)
/// from the run's single read loop to whoever is waiting for them, and remembers which files the SAMPLE created.
/// Only one transfer activity at a time opens the inbox. File chunks nobody asked for are swallowed, never ingested.
/// <para>
/// Dropped-file rule: only files created after <see cref="MarkOpened"/> (the open_sample/open_url command) by a process of the
/// sample's process tree count. The tree is a pid set built from process_started events seen after the open: a process joins
/// when its parent is the agent itself (the opener; its pid comes from the ready message) or is already in the set. Without an
/// agent pid (older agent, replay), the first process started after the open seeds the set. An exited pid leaves the set, so
/// a reused pid is not mistaken for the sample. Well-known OS/browser noise (prefetch, INetCache, Edge caches, PowerShell's
/// __PSScriptPolicyTest_ files) is never collected. Anything else on the system (updates, Defender, indexing) is ignored.
/// </para>
/// </summary>
public sealed class TransferInbox(int? agentPid = null)
{
    // Up to this many 512 KiB chunks may wait for the consumer; beyond that the read loop waits (back-pressure).
    private const int Capacity = 8;
    private const int MaxTrackedPaths = 1000;
    private const int MaxTreePids = 4096;

    // Case-insensitive path fragments that are never the sample's drop, even when the sample's tree wrote them.
    private static readonly string[] NoiseFragments =
    [
        @"\Windows\Prefetch\",
        @"\AppData\Local\Microsoft\Windows\INetCache\",
    ];

    private static readonly string[] EdgeCacheDirs = [@"\Cache\", @"\Code Cache\", @"\GPUCache\", @"\Service Worker\CacheStorage\", @"\Service Worker\ScriptCache\"];

    /// <summary>Longer paths are never asked for: the <c>collect_file</c> command must fit one frame (a compromised agent can report a path of any length).</summary>
    public const int MaxPathChars = 32_767;

    private readonly object _gate = new();
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _created = [];
    private readonly HashSet<int> _tree = [];
    private Channel<AgentMessage>? _open;
    private bool _ended;
    private bool _opened;

    /// <summary>
    /// The sample is being opened now: from here on, process starts build the sample's process tree and its file creates are
    /// tracked. Call it right before the open command is sent.
    /// </summary>
    public void MarkOpened()
    {
        lock (_gate) _opened = true;
    }

    /// <summary>Paths of files the sample's process tree reported creating, in order, de-duplicated case-insensitively.</summary>
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
        switch (message)
        {
            case ProcessStartedMessage p: Join(p); break;
            case ProcessExitedMessage x: lock (_gate) _tree.Remove(x.Pid); break;
            case FileEventMessage { Operation: "create" } f: Track(f.Pid, f.Path); break;
        }
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

    private void Join(ProcessStartedMessage p)
    {
        lock (_gate)
        {
            if (!_opened || _tree.Count >= MaxTreePids || p.Pid <= 0) return;
            var seed = agentPid is null && _tree.Count == 0;
            if (seed || p.ParentPid == agentPid || _tree.Contains(p.ParentPid)) _tree.Add(p.Pid);
        }
    }

    private void Track(int pid, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > MaxPathChars || IsNoise(path)) return;
        lock (_gate)
        {
            if (_opened && _tree.Contains(pid) && _created.Count < MaxTrackedPaths && _seen.Add(path)) _created.Add(path);
        }
    }

    internal static bool IsNoise(string path)
    {
        foreach (var n in NoiseFragments)
            if (path.Contains(n, StringComparison.OrdinalIgnoreCase)) return true;
        if (path.Contains(@"\Microsoft\Edge\User Data\", StringComparison.OrdinalIgnoreCase))
            foreach (var c in EdgeCacheDirs)
                if (path.Contains(c, StringComparison.OrdinalIgnoreCase)) return true;
        var name = path[(path.LastIndexOfAny(['\\', '/']) + 1)..];
        return name.StartsWith("__PSScriptPolicyTest_", StringComparison.OrdinalIgnoreCase);
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
