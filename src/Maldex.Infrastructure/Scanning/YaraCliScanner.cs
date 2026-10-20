using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Maldex.Core.Text;
using Maldex.Core.Verdicts;
using Microsoft.Extensions.Options;

namespace Maldex.Infrastructure.Scanning;

/// <summary>Bound from <c>Maldex:Yara</c>.</summary>
public sealed class YaraOptions
{
    public const string SectionName = "Maldex:Yara";

    /// <summary>The yara executable (a name on PATH or a full path).</summary>
    public string Binary { get; set; } = "yara";

    /// <summary>
    /// The rules index. A relative path is looked up next to the binaries (the <c>rules/</c> tree is copied to the
    /// output directory) and then in the current directory.
    /// </summary>
    public string RulesIndex { get; set; } = "rules/yara/index.yar";

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);
}

/// <summary>Runs <c>yara -g -w -N {rulesIndex} {file}</c> and turns its output into detections.</summary>
public sealed partial class YaraCliScanner(IOptions<YaraOptions> options) : IYaraScanner
{
    // `yara -g` prints "rule [tag1,tag2] path" ("[]" when there are no tags). Warnings and errors never look like that.
    [GeneratedRegex(@"^(?<rule>[A-Za-z_][A-Za-z0-9_]*) \[(?<tags>[^\]]*)\] ", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MatchLine();

    private readonly YaraOptions _options = options.Value;

    public static IReadOnlyList<(string Rule, string[] Tags)> ParseOutput(string stdout)
    {
        var result = new List<(string, string[])>();
        foreach (var raw in stdout.Split('\n'))
        {
            var m = MatchLine().Match(raw.TrimEnd('\r'));
            if (!m.Success) continue;
            var tags = m.Groups["tags"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            result.Add((m.Groups["rule"].Value, tags));
        }
        return result;
    }

    public async Task<IReadOnlyList<DetectionResult>> ScanAsync(string filePath, string target, CancellationToken ct)
    {
        var rules = ResolveRulesIndex();
        var psi = new ProcessStartInfo(_options.Binary)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in new[] { "-g", "-w", "-N", rules, filePath }) psi.ArgumentList.Add(arg);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.Timeout);

        System.Diagnostics.Process process;
        try
        {
            process = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException($"could not start '{_options.Binary}'");
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

                var output = await stdout.ConfigureAwait(false);
                var error = await stderr.ConfigureAwait(false);
                if (process.ExitCode != 0)
                    throw new InvalidOperationException($"yara exited with code {process.ExitCode}: {error.Trim()}");

                return ParseOutput(output)
                    .Select(m => new DetectionResult(
                        DetectionSource.Yara,
                        m.Rule,
                        m.Tags.Contains("malicious", StringComparer.OrdinalIgnoreCase) ? 1 : 2,
                        m.Tags,
                        GuestText.Clean(target),
                        $"YARA rule {m.Rule} matched"))
                    .ToList();
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"yara did not finish within {_options.Timeout.TotalSeconds:0} s");
            }
            finally
            {
                if (!process.HasExited)
                {
                    try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                }
            }
        }
    }

    private string ResolveRulesIndex()
    {
        var path = _options.RulesIndex;
        if (Path.IsPathRooted(path)) return path;
        var beside = Path.Combine(AppContext.BaseDirectory, path);
        if (File.Exists(beside)) return beside;
        var cwd = Path.GetFullPath(path);
        return File.Exists(cwd) ? cwd : beside;
    }
}
