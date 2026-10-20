namespace Maldex.Infrastructure.Process;

public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

public interface IProcessRunner
{
    /// <summary>Runs <paramref name="file"/> with discrete arguments (never a shell string). Default timeout is 60 s;
    /// on timeout the whole process tree is killed and <see cref="TimeoutException"/> is thrown.</summary>
    Task<ProcessResult> RunAsync(string file, IReadOnlyList<string> args, CancellationToken ct,
        string? stdin = null, TimeSpan? timeout = null);
}
