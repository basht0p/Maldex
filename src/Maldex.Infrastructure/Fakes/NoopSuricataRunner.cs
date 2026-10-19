using Maldex.Core.Abstractions;
using Maldex.Core.Verdicts;

namespace Maldex.Infrastructure.Fakes;

/// <summary>Fake-mode Suricata: finds nothing. The real runner arrives in Plan 1b.</summary>
public sealed class NoopSuricataRunner : ISuricataRunner
{
    public Task<IReadOnlyList<DetectionResult>> RunAsync(string pcapPath, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DetectionResult>>([]);
}
