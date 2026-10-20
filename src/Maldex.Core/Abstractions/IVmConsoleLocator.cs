namespace Maldex.Core.Abstractions;

/// <summary>Locates the loopback VNC console of a running analysis VM (consumed by the live-view tunnel).</summary>
public interface IVmConsoleLocator
{
    /// <summary>Returns the VM's VNC endpoint, or null when the VM is not running.</summary>
    Task<VncEndpoint?> GetAsync(VmHandle vm, CancellationToken ct);
}

/// <summary>A VNC endpoint. The password never leaves the server.</summary>
public sealed record VncEndpoint(string Host, int Port, string Password);
