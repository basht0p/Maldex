namespace Maldex.Integration.Tests.E2E;

/// <summary>What a run left on the host.</summary>
internal sealed record LeftOver(IReadOnlyList<string> Domains, IReadOnlyList<string> Links, IReadOnlyList<string> Namespaces)
{
    public static LeftOver None { get; } = new([], [], []);
    public bool IsEmpty => Domains.Count + Links.Count + Namespaces.Count == 0;
    public override string ToString() => $"domains [{string.Join(", ", Domains)}], links [{string.Join(", ", Links)}], namespaces [{string.Join(", ", Namespaces)}]";
}

/// <summary>
/// The E2E cleanup sequence with an independent time bound per phase, so a hang in one phase cannot skip the next:
/// stop the task (own bound, a timeout is recorded), dispose the host (always), check for leftovers (own bound), and
/// force-remove whatever was found, even when the check's wait timed out (it uses what was found so far).
/// Returns null when everything was clean, otherwise a message to report in addition to any test failure.
/// </summary>
internal sealed class E2ECleanup(
    Func<CancellationToken, Task> stop,
    Action dispose,
    Func<CancellationToken, Task<LeftOver>> find,
    Func<LeftOver, CancellationToken, Task> forceRemove,
    TimeSpan stopBound, TimeSpan checkBound, TimeSpan removeBound, TimeSpan pollInterval, int checkAttempts)
{
    public async Task<string?> RunAsync()
    {
        var problems = new List<string>();

        try
        {
            using var cts = new CancellationTokenSource(stopBound);
            await stop(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { problems.Add($"task did not reach a terminal state within {stopBound.TotalSeconds:0} s"); }
        catch (Exception ex) { problems.Add($"stopping the task failed: {ex.Message}"); }

        try { dispose(); }
        catch (Exception ex) { problems.Add($"disposing the host failed: {ex.Message}"); }

        var found = LeftOver.None;
        var checkRan = true;
        try
        {
            using var cts = new CancellationTokenSource(checkBound);
            for (var attempt = 0; attempt < checkAttempts; attempt++)
            {
                found = await find(cts.Token).ConfigureAwait(false);
                if (found.IsEmpty) break;
                await Task.Delay(pollInterval, cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { problems.Add($"leak check timed out after {checkBound.TotalSeconds:0} s"); }
        catch (Exception ex) { checkRan = false; problems.Add($"leak check could not run: {ex.Message}"); }

        if (!found.IsEmpty)
        {
            problems.Add($"left behind: {found} (force-removed)");
            try
            {
                using var cts = new CancellationTokenSource(removeBound);
                await forceRemove(found, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) { problems.Add($"force-removal failed: {ex.Message}"); }
        }
        _ = checkRan;
        return problems.Count == 0 ? null : string.Join("; ", problems);
    }
}
