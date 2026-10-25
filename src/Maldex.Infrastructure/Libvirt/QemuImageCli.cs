using Maldex.Infrastructure.Execution;

namespace Maldex.Infrastructure.Libvirt;

/// <summary>Thin wrapper over <c>qemu-img</c>.</summary>
public sealed class QemuImageCli(IProcessRunner runner, string binary)
{
    /// <summary>Creates a qcow2 copy-on-write overlay backed by <paramref name="basePath"/>.</summary>
    public async Task CreateOverlayAsync(string basePath, string overlayPath, CancellationToken ct)
    {
        var r = await runner.RunAsync(binary, ["create", "-f", "qcow2", "-F", "qcow2", "-b", basePath, overlayPath], ct);
        if (r.ExitCode != 0)
            throw new InvalidOperationException($"qemu-img create failed ({r.ExitCode}): {r.Stderr.Trim()}");
    }

    /// <summary>Creates an empty qcow2 disk of <paramref name="bytes"/> at <paramref name="path"/>.</summary>
    public async Task CreateBlankAsync(string path, long bytes, CancellationToken ct)
    {
        var r = await runner.RunAsync(binary, ["create", "-f", "qcow2", path, bytes.ToString(System.Globalization.CultureInfo.InvariantCulture)], ct);
        if (r.ExitCode != 0)
            throw new InvalidOperationException($"qemu-img create failed ({r.ExitCode}): {r.Stderr.Trim()}");
    }
}
