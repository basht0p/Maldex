using Maldex.Core.Abstractions;

namespace Maldex.Infrastructure.Fakes;

/// <summary>Fake mode has no guest disks: installs are only recorded (and fail when a test sets <see cref="Failure"/>).</summary>
public sealed class FakeGuestDiskEditor : IGuestDiskEditor
{
    private readonly List<(string Stem, string AgentExePath)> _installs = [];

    public IReadOnlyList<(string Stem, string AgentExePath)> Installs
    {
        get { lock (_installs) return _installs.ToArray(); }
    }

    /// <summary>When set, every install throws this.</summary>
    public Exception? Failure { get; set; }

    /// <summary>Awaited after the call is recorded (a test can hold the install, or watch what has happened by then).</summary>
    public Func<string, CancellationToken, Task>? OnInstall { get; set; }

    public async Task InstallAgentAsync(string fileStem, string agentExePath, CancellationToken ct)
    {
        lock (_installs) _installs.Add((fileStem, agentExePath));
        if (OnInstall is not null) await OnInstall(fileStem, ct).ConfigureAwait(false);
        if (Failure is not null) throw Failure;
    }
}
