namespace Maldex.Agent.Commands;

/// <summary>What the guest reports about itself; the agent's own version is added by the dispatcher.</summary>
public sealed record ProvenanceReport(string WindowsEdition, string WindowsBuild, bool? OfficePresent, bool? RealTimeProtectionEnabled);

/// <summary>Reads the guest's provenance. Read-only: an implementation never changes the guest.</summary>
public interface IProvenanceProbe
{
    ProvenanceReport Probe();
}
