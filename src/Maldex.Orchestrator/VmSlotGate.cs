namespace Maldex.Orchestrator;

/// <summary>
/// Serializes "count the busy VM slots, then take one" between the analysis claimer and the template job coordinator (both
/// run in this one host), so two of them can never take the same free slot. Held only around the count and the save that
/// takes the slot, never around a VM boot.
/// </summary>
public sealed class VmSlotGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IDisposable> EnterAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        return new Releaser(_gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release();
        }
    }
}
