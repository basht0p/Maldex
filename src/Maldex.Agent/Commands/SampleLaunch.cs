namespace Maldex.Agent.Commands;

/// <summary>
/// How a delivered sample is started. Script types run in their interpreter explicitly: Windows 11's per-user default
/// (UserChoice) opens .ps1 in Notepad whatever the machine-wide class says, so ShellExecute would only show the script.
/// Everything else (executables, documents) goes through the shell, which picks the registered application.
/// </summary>
public sealed record SampleLaunch(string FileName, IReadOnlyList<string> Arguments, bool UseShellExecute)
{
    /// <param name="filePath">The delivered sample.</param>
    /// <param name="systemRoot">%SystemRoot%, e.g. <c>C:\Windows</c>.</param>
    public static SampleLaunch For(string filePath, string systemRoot)
    {
        var sys = systemRoot.TrimEnd('\\') + @"\System32\";
        var ext = filePath.LastIndexOf('.') is var dot and >= 0 && dot > filePath.LastIndexOf('\\') ? filePath[dot..].ToLowerInvariant() : "";
        return ext switch
        {
            ".ps1" => new(sys + @"WindowsPowerShell\v1.0\powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", filePath], false),
            ".bat" or ".cmd" => new(sys + "cmd.exe", ["/c", filePath], false),
            ".vbs" or ".vbe" or ".js" or ".jse" or ".wsf" => new(sys + "wscript.exe", [filePath], false),
            ".hta" => new(sys + "mshta.exe", [filePath], false),
            _ => new(filePath, [], true),
        };
    }
}
