using Maldex.Core.Abstractions;

namespace Maldex.Infrastructure.Fakes;

/// <summary>Fake-mode Suricata: finds nothing. The real runner arrives in Plan 1b.</summary>
public sealed class NoopSuricataRunner : ISuricataRunner
{
    public Task<SuricataResult> RunAsync(string pcapPath, CancellationToken ct) => Task.FromResult(SuricataResult.Empty);
}
