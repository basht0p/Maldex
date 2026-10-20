namespace Maldex.Agent.Commands;

public interface IUrlOpener
{
    Task OpenAsync(string url, CancellationToken ct);
}
