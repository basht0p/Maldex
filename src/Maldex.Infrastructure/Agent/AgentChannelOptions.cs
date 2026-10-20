namespace Maldex.Infrastructure.Agent;

/// <summary>Bound from <c>Maldex:Real:Agent</c>.</summary>
public sealed class AgentChannelOptions
{
    public const string SectionName = "Maldex:Real:Agent";

    /// <summary>Per-run files live under <c>{RunDir}/{vmName}/</c>; the agent socket is <c>agent.sock</c>.</summary>
    public string RunDir { get; set; } = ".data/vm/run";

    /// <summary>How long to wait for QEMU to create the agent socket.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
