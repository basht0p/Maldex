using System.Text;

namespace Maldex.Agent;

/// <summary>
/// Minimal size-capped file log: appends lines to <c>path</c>; once the file would exceed <c>maxBytes</c> it is
/// moved to <c>path + ".1"</c> (replacing any previous old file), keeping exactly one old file. Never throws.
/// </summary>
public sealed class RollingFileLog(string path, long maxBytes)
{
    private readonly object _gate = new();

    public string Path { get; } = path;

    public void Write(string line)
    {
        try
        {
            lock (_gate)
            {
                var dir = System.IO.Path.GetDirectoryName(Path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var text = line + Environment.NewLine;
                var info = new FileInfo(Path);
                if (info.Exists && info.Length > 0 && info.Length + Encoding.UTF8.GetByteCount(text) > maxBytes)
                    File.Move(Path, Path + ".1", overwrite: true);
                File.AppendAllText(Path, text);
            }
        }
        catch
        {
            // Logging must never take the agent down.
        }
    }
}

/// <summary>Process-wide agent log: C:\ProgramData\Maldex\agent.log, rolled at 5 MiB, one old file kept.</summary>
public static class AgentLog
{
    public const long MaxBytes = 5L * 1024 * 1024;

    private static readonly Lazy<RollingFileLog> Sink = new(() => new RollingFileLog(
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Maldex", "agent.log"),
        MaxBytes));

    public static void Write(string text, DateTimeOffset? at = null)
    {
        var line = $"{at ?? DateTimeOffset.UtcNow:O} {text}";
        Sink.Value.Write(line);
        try { Console.Error.WriteLine(line); } catch { /* WinExe has no console; ignore */ }
    }
}
