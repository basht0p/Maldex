namespace Maldex.Orchestrator.Templates;

/// <summary>
/// The configure-console tunnels that are open, by job (in this process). A tunnel registers before it checks that the job is
/// still open for consoles and unregisters when it ends; the coordinator tells a job's tunnels to close before it seals or
/// destroys the guest (R22), so that no console input reaches a guest that is being sealed.
/// </summary>
public sealed class JobConsoleRegistry(TimeProvider time)
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, List<Entry>> _open = [];
    private readonly Dictionary<Guid, TaskCompletionSource> _drained = [];

    private sealed class Entry(JobConsoleRegistry owner, Guid jobId) : IDisposable
    {
        public CancellationTokenSource Closing { get; } = new();
        public void Dispose() => owner.Unregister(jobId, this);
    }

    /// <summary>An open console: <see cref="Closing"/> is cancelled when it must stop forwarding input; dispose it when the tunnel ends.</summary>
    public sealed class Registration(CancellationToken closing, IDisposable inner) : IDisposable
    {
        public CancellationToken Closing { get; } = closing;
        public void Dispose() => inner.Dispose();
    }

    public int OpenCount(Guid jobId)
    {
        lock (_gate) return _open.TryGetValue(jobId, out var list) ? list.Count : 0;
    }

    public Registration Register(Guid jobId)
    {
        var entry = new Entry(this, jobId);
        lock (_gate)
        {
            if (!_open.TryGetValue(jobId, out var list)) _open[jobId] = list = [];
            list.Add(entry);
        }
        return new Registration(entry.Closing.Token, entry);
    }

    private void Unregister(Guid jobId, Entry entry)
    {
        TaskCompletionSource? drained = null;
        lock (_gate)
        {
            if (!_open.TryGetValue(jobId, out var list) || !list.Remove(entry)) return;
            if (list.Count == 0)
            {
                _open.Remove(jobId);
                _drained.Remove(jobId, out drained);
            }
        }
        entry.Closing.Dispose();
        drained?.TrySetResult();
    }

    /// <summary>Tells every open console of the job to close. Returns at once.</summary>
    public void Close(Guid jobId)
    {
        List<Entry> entries;
        lock (_gate) entries = _open.TryGetValue(jobId, out var list) ? [.. list] : [];
        foreach (var entry in entries)
        {
            try { entry.Closing.Cancel(); }
            catch (ObjectDisposedException) { /* it just ended */ }
        }
    }

    /// <summary>Closes the job's consoles and waits until none is open. False when some are still open after <paramref name="timeout"/>.</summary>
    public async Task<bool> CloseAsync(Guid jobId, TimeSpan timeout, CancellationToken ct)
    {
        Task wait;
        lock (_gate)
        {
            if (!_open.ContainsKey(jobId)) return true;
            if (!_drained.TryGetValue(jobId, out var tcs))
                _drained[jobId] = tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            wait = tcs.Task;
        }
        Close(jobId);
        try
        {
            await wait.WaitAsync(timeout, time, ct).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}
