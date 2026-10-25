using System.Security.Cryptography;

namespace Maldex.Core.Agent;

/// <summary>
/// The guest agent's build identity, shared by the agent (which reports it) and the host (which computes what it expects):
/// <c>version+first 12 lowercase hex characters of the SHA-256 of the exe</c>.
/// </summary>
public static class AgentIdentity
{
    /// <summary>The version without build metadata: <c>1.0.0+abc</c> becomes <c>1.0.0</c>.</summary>
    public static string VersionOf(string? informationalVersion)
    {
        var v = informationalVersion?.Split('+')[0].Trim();
        return string.IsNullOrEmpty(v) ? "0.0.0" : v;
    }

    public static string BuildId(string version, byte[] exe) => version + "+" + Prefix(SHA256.HashData(exe));

    public static string BuildId(string version, Stream exe) => version + "+" + Prefix(SHA256.HashData(exe));

    /// <summary>
    /// The build identity of the agent that <c>./build.sh agent</c> left on disk: the version from its version record (build metadata
    /// dropped) plus the hash of the exe.
    /// </summary>
    public static string BuildIdOfFiles(string exePath, string versionPath)
    {
        var version = VersionOfRecord(File.ReadAllBytes(versionPath));
        using var exe = File.OpenRead(exePath); // hashed as a stream: the exe is large
        return BuildId(version, exe);
    }

    /// <summary>The same identity from the files' bytes (a caller that already holds them, so the id is of exactly these bytes).</summary>
    public static string BuildIdOf(byte[] versionRecord, byte[] exe) => BuildId(VersionOfRecord(versionRecord), exe);

    private static string VersionOfRecord(byte[] versionRecord) => VersionOf(System.Text.Encoding.UTF8.GetString(versionRecord));

    /// <summary>What the agent reports when it cannot read its own exe.</summary>
    public static string UnknownBuildId(string version) => version + "+" + new string('0', 12);

    private static string Prefix(byte[] hash) => Convert.ToHexString(hash)[..12].ToLowerInvariant();
}
