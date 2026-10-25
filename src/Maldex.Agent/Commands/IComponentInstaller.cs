namespace Maldex.Agent.Commands;

/// <summary>
/// The guest-changing steps of <c>apply_components</c>, one method per step so the dispatcher can run them in order and name the
/// one that failed. Each throws on failure. Implementations are Windows-only; tests use a fake.
/// </summary>
public interface IComponentInstaller
{
    /// <summary>Imports the DER certificate into the machine Root store, replacing an older certificate with the same subject.</summary>
    void ImportCaCertificate(string derPath);

    /// <summary>Applies a Sysmon configuration (<c>Sysmon64.exe -c</c>).</summary>
    void ApplySysmonConfig(string configPath);

    /// <summary>Replaces the installed agent exe with <paramref name="newExePath"/>; the new build runs from the next start.</summary>
    void SwapAgent(string newExePath);

    /// <summary>Clears every Windows event log and the contents of the temp directories it can delete.</summary>
    void ClearLogsAndTemp();
}

/// <summary>Shuts the guest operating system down.</summary>
public interface IPowerController
{
    void PowerOff();
}
