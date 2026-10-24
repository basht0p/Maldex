namespace Maldex.Core.Abstractions;

/// <summary>
/// Edits the files inside a template working copy's guest disk while no VM runs on it. Used once per template job, before the
/// first boot, so the guest starts with the current agent whatever the base version carried.
/// </summary>
public interface IGuestDiskEditor
{
    /// <summary>
    /// Writes the agent exe at <paramref name="agentExePath"/> to <c>C:\Program Files\Maldex\Maldex.Agent.exe</c> inside
    /// <c>base/{fileStem}.qcow2</c>, and nothing else. Only a writable working copy is accepted, never a sealed version disk.
    /// </summary>
    /// <exception cref="TemplateAgentInstallException">The agent could not be written; the message is safe as a job's failure reason.</exception>
    Task InstallAgentAsync(string fileStem, string agentExePath, CancellationToken ct);
}

/// <summary>The current agent could not be written into a working copy. The message is safe to store as the job's failure reason.</summary>
public sealed class TemplateAgentInstallException(string message, Exception? inner = null) : Exception(message, inner);
