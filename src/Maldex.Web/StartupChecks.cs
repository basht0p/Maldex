namespace Maldex.Web;

public static class StartupChecks
{
    /// <summary>
    /// EX_CONFIG (sysexits.h): the exit code of every configuration fail-fast. The packaged unit lists it in
    /// <c>RestartPreventExitStatus</c>, so systemd does not restart an unconfigured service every few seconds.
    /// </summary>
    public const int ConfigErrorExitCode = 78;

    public const string MissingConnectionMessage =
        "ConnectionStrings:Maldex is not set; edit /etc/maldex/appsettings.Production.json and restart maldex";

    /// <summary>
    /// Null when <paramref name="root"/> (<c>Maldex:Real:Root</c>) is a usable data directory: set, absolute, an existing
    /// directory and writable by this process. Otherwise the one-line reason, with a hint.
    /// </summary>
    public static string? RealRootProblem(string? root)
    {
        const string hint = "set Maldex:Real:Root in /etc/maldex/appsettings.Production.json to the data directory (the package uses /var/lib/maldex) and restart maldex";
        if (string.IsNullOrWhiteSpace(root)) return $"Maldex:Real:Root is not set; {hint}";
        if (!Path.IsPathRooted(root)) return $"Maldex:Real:Root must be an absolute path (got '{root}'); {hint}";
        if (!Directory.Exists(root))
            return $"Maldex:Real:Root '{root}' does not exist; create it owned by maldex, mode 0750 (sudo maldex-setup does), or {hint}";
        try
        {
            // DeleteOnClose: the probe file is gone as soon as the handle closes, even if the process dies right after.
            using var _ = new FileStream(Path.Combine(root, $".maldex-write-check-{Environment.ProcessId}"), FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"Maldex:Real:Root '{root}' is not writable by the service user ({e.Message}); make it owned by maldex, mode 0750 (sudo maldex-setup does), or {hint}";
        }
        return null;
    }
}
