using System.Globalization;
using Maldex.Core.Abstractions;
using Maldex.Infrastructure.Execution;
using Microsoft.Extensions.Options;

namespace Maldex.Infrastructure.Libvirt;

public sealed class VncConsoleLocator(IOptions<LibvirtOptions> options, IProcessRunner runner) : IVmConsoleLocator
{
    private readonly LibvirtOptions _o = options.Value;

    public async Task<VncEndpoint?> GetAsync(VmHandle vm, CancellationToken ct)
    {
        var runDir = LibvirtPaths.RunDirFor(_o, vm);
        var r = await runner.RunAsync(_o.VirshBinary, ["-c", _o.Uri, "vncdisplay", vm.Name], ct);
        if (r.ExitCode != 0) return null;

        // "127.0.0.1:N" -> 5900 + N
        var text = r.Stdout.Trim();
        var colon = text.LastIndexOf(':');
        if (colon < 0 || !int.TryParse(text[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var display))
            return null;
        var host = text[..colon];
        if (host.Length == 0) host = "127.0.0.1";

        var passFile = Path.Combine(runDir, "vnc.pass");
        if (!File.Exists(passFile)) return null;
        var password = (await File.ReadAllTextAsync(passFile, ct)).Trim();
        return new VncEndpoint(host, 5900 + display, password);
    }
}
