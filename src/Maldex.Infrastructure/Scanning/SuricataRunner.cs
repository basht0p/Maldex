using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Maldex.Core.Abstractions;
using Maldex.Core.Verdicts;
using Microsoft.Extensions.Options;

namespace Maldex.Infrastructure.Scanning;

/// <summary>Replays a capture with <c>suricata -r</c> and parses the resulting <c>eve.json</c>.</summary>
public sealed class SuricataRunner(IOptions<SuricataOptions> options) : ISuricataRunner
{
    private readonly SuricataOptions _options = options.Value;

    public async Task<IReadOnlyList<DetectionResult>> RunAsync(string pcapPath, CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "maldex-suricata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var psi = new ProcessStartInfo(_options.Binary)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var arg in new[]
            {
                "-r", pcapPath, "-l", tmp, "-c", Resolve(_options.ConfigPath), "-S", Resolve(_options.RulesPath),
                "-k", "none", "--runmode", "single",
            }) psi.ArgumentList.Add(arg);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_options.Timeout);

            Process process;
            try
            {
                process = Process.Start(psi) ?? throw new InvalidOperationException($"could not start '{_options.Binary}'");
            }
            catch (Win32Exception ex)
            {
                throw new InvalidOperationException($"could not start '{_options.Binary}': {ex.Message}", ex);
            }

            using (process)
            {
                try
                {
                    var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
                    var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                    await stdout.ConfigureAwait(false);
                    var error = await stderr.ConfigureAwait(false);
                    if (process.ExitCode != 0)
                        throw new InvalidOperationException($"suricata exited with code {process.ExitCode}: {error.Trim()}");
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException($"suricata did not finish within {_options.Timeout.TotalSeconds:0} s");
                }
                finally
                {
                    if (!process.HasExited)
                    {
                        try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                    }
                }
            }

            var eve = Path.Combine(tmp, "eve.json");
            if (!File.Exists(eve)) return [];
            return SuricataEveParser.Parse(ReadLines(eve));
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Streams eve.json line by line (never loading it whole). The parser stops after
    /// <see cref="SuricataEveParser.MaxAlerts"/> alerts; reading also stops after 64 MiB of characters.
    /// </summary>
    private static IEnumerable<string> ReadLines(string path)
    {
        const long MaxChars = 64L * 1024 * 1024;
        long read = 0;
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            read += line.Length + 1;
            if (read > MaxChars) yield break;
            yield return line;
        }
    }

    /// <summary>A relative path is looked up next to the binaries, then in the current directory.</summary>
    private static string Resolve(string path)
    {
        if (Path.IsPathRooted(path)) return path;
        var beside = Path.Combine(AppContext.BaseDirectory, path);
        if (File.Exists(beside)) return beside;
        var cwd = Path.GetFullPath(path);
        return File.Exists(cwd) ? cwd : beside;
    }
}
