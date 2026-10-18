namespace Maldex.Orchestrator;

/// <summary>Bound from <c>Maldex:Orchestrator</c>.</summary>
public sealed class OrchestratorOptions
{
    public const string SectionName = "Maldex:Orchestrator";

    public int MaxConcurrentVms { get; set; } = 1;
    public int VmMemoryMiB { get; set; } = 4096;
    public int VmVCpus { get; set; } = 2;
    public TimeSpan AgentReadyTimeout { get; set; } = TimeSpan.FromSeconds(90);
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromMilliseconds(250);
    public int FlushBatchSize { get; set; } = 200;
    public TimeSpan ControlPollInterval { get; set; } = TimeSpan.FromSeconds(1);
    public long MinFreeDiskMiB { get; set; } = 5120;
}
