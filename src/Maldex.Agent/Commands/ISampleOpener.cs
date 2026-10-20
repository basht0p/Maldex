namespace Maldex.Agent.Commands;

public interface ISampleOpener
{
    /// <summary>Opens (detonates) a delivered sample file.</summary>
    Task OpenAsync(string filePath, CancellationToken ct);
}
