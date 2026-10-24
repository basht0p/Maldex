using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Maldex.Core.Agent;

namespace Maldex.Orchestrator.Templates;

/// <summary>The current Maldex components that sealing installs into a template, loaded once per job.</summary>
public sealed class ComponentBundle
{
    public required byte[] AgentExe { get; init; }
    /// <summary>The agent's build identity, <c>version+first 12 hex of the exe's SHA-256</c>, as the agent itself reports it.</summary>
    public required string AgentBuildId { get; init; }
    public required byte[] SysmonConfig { get; init; }
    public required string SysmonConfigSha256 { get; init; }
    /// <summary>The mitmproxy CA certificate, DER-encoded.</summary>
    public required byte[] CaDer { get; init; }
    public required string MitmCaSha256 { get; init; }

    public string AgentSha256 => Hex(AgentExe);

    public static async Task<ComponentBundle> LoadAsync(TemplateJobOptions options, CancellationToken ct)
    {
        var agent = await ReadAsync(options.AgentExePath, "the guest agent", "run ./build.sh agent", ct).ConfigureAwait(false);
        var versionText = await ReadAsync(options.AgentVersionPath, "the guest agent's version record", "run ./build.sh agent", ct).ConfigureAwait(false);
        var version = AgentIdentity.VersionOf(System.Text.Encoding.UTF8.GetString(versionText));
        var sysmon = await ReadAsync(options.SysmonConfigPath, "the Sysmon configuration", "run from the Maldex repository root", ct).ConfigureAwait(false);
        var pem = await ReadAsync(options.MitmCaPemPath, "the mitmproxy CA certificate", "run ./build.sh real-setup", ct).ConfigureAwait(false);

        byte[] der;
        try
        {
            using var cert = X509Certificate2.CreateFromPem(System.Text.Encoding.ASCII.GetString(pem));
            der = cert.RawData;
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException($"the mitmproxy CA certificate {options.MitmCaPemPath} is not a valid PEM certificate ({ex.Message}); run ./build.sh real-setup", ex);
        }

        return new ComponentBundle
        {
            AgentExe = agent,
            AgentBuildId = AgentIdentity.BuildId(version, agent),
            SysmonConfig = sysmon,
            SysmonConfigSha256 = Hex(sysmon),
            CaDer = der,
            MitmCaSha256 = Hex(der),
        };
    }

    private static async Task<byte[]> ReadAsync(string path, string what, string fix, CancellationToken ct)
    {
        try
        {
            return await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new InvalidOperationException($"{what} is missing: {path} does not exist; {fix}", ex);
        }
    }

    private static string Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}
