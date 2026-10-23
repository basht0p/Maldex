namespace Maldex.Infrastructure.Fakes;

/// <summary>Bound from <c>Maldex:Fake:Replay</c>.</summary>
public sealed class ReplayOptions
{
    public const string SectionName = "Maldex:Fake:Replay";

    /// <summary>Empty = shipped replays next to the binaries. A relative path resolves against AppContext.BaseDirectory.</summary>
    public string Directory { get; set; } = "";
    public string DefaultFile { get; set; } = "default.jsonl";
    public double Speed { get; set; } = 1.0;
    public bool HoldOpen { get; set; } = true;

    public string ResolveDirectory() =>
        string.IsNullOrWhiteSpace(Directory)
            ? Path.Combine(AppContext.BaseDirectory, "Fakes", "Replays")
            : Path.GetFullPath(Directory, AppContext.BaseDirectory);
}

/// <summary>Bound from <c>Maldex:Fake:Hypervisor</c>.</summary>
public sealed class FakeHypervisorOptions
{
    public const string SectionName = "Maldex:Fake:Hypervisor";

    /// <summary>The first N calls to StartAsync throw VmStartException.</summary>
    public int FailStarts { get; set; }
}

/// <summary>Bound from <c>Maldex:Fake:Egress</c>.</summary>
public sealed class FakeEgressOptions
{
    public const string SectionName = "Maldex:Fake:Egress";

    public bool LiveAvailable { get; set; }

    /// <summary>The "VPN exit IP" of fake LiveVpn runs: the fake expected-egress source returns it and the fake guest echoes it, so the self-test passes.</summary>
    public string LiveEgressIp { get; set; } = "203.0.113.7";
}
