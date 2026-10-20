namespace Maldex.Infrastructure.Isolation;

/// <summary>Bound from <c>Maldex:Real:SelfTest</c>.</summary>
public sealed class SelfTestOptions
{
    public const string SectionName = "Maldex:Real:SelfTest";

    /// <summary>
    /// When true (the default) a self-test plan without any control_plane_* probe fails the gate: an empty
    /// <c>ControlPlaneIps</c> must never silently drop those probes. Fake mode turns it off.
    /// </summary>
    public bool RequireControlPlaneProbes { get; set; } = true;
}
