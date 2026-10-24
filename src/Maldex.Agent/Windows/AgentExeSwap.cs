namespace Maldex.Agent.Windows;

/// <summary>Replaces the agent exe while it runs (Windows lets a running exe be renamed, not overwritten).</summary>
public static class AgentExeSwap
{
    public const string OldSuffix = ".old";
    public const string NewSuffix = ".new";

    /// <summary>
    /// Stages <paramref name="newExePath"/> as <c>exe.new</c>, renames the current exe to <c>exe.old</c> (replacing a stale one) and
    /// renames <c>exe.new</c> to the exe name. If the last rename fails the old exe is put back. Throws on failure.
    /// </summary>
    public static void Swap(string exePath, string newExePath)
    {
        var staged = exePath + NewSuffix;
        var old = exePath + OldSuffix;
        File.Move(newExePath, staged, overwrite: true);
        try
        {
            File.Move(exePath, old, overwrite: true);
        }
        catch
        {
            TryDelete(staged);
            throw;
        }
        try
        {
            File.Move(staged, exePath);
        }
        catch
        {
            File.Move(old, exePath, overwrite: true); // put the running build back
            TryDelete(staged);
            throw;
        }
    }

    /// <summary>Deletes leftovers of an earlier swap (<c>exe.old</c>, <c>exe.new</c>); never throws.</summary>
    public static void DeleteStale(string exePath)
    {
        TryDelete(exePath + OldSuffix);
        TryDelete(exePath + NewSuffix);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
