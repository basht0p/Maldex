using System.Diagnostics;

namespace Maldex.Infrastructure.Process;

public sealed class ProcessRunner : IProcessRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    public async Task<ProcessResult> RunAsync(string file, IReadOnlyList<string> args, CancellationToken ct,
        string? stdin = null, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new System.Diagnostics.Process { StartInfo = psi };
        proc.Start();

        using var timeoutCts = new CancellationTokenSource(timeout ?? DefaultTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        var stdout = proc.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = proc.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            if (stdin is not null) await proc.StandardInput.WriteAsync(stdin.AsMemory(), linked.Token);
            proc.StandardInput.Close();
        }
        catch (IOException) { /* the child exited without reading stdin */ }
        catch (OperationCanceledException) { }

        try
        {
            await proc.WaitForExitAsync(linked.Token);
            return new ProcessResult(proc.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            try { await proc.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { }
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException($"'{file}' did not exit within {(timeout ?? DefaultTimeout).TotalSeconds:0.###}s and was killed.");
        }
    }
}
