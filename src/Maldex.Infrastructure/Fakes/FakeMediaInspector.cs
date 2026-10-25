using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Maldex.Core.Media;

namespace Maldex.Infrastructure.Fakes;

/// <summary>Canned contents (the consumer multi-edition installer, or Office) for fake mode and tests; no file is read.</summary>
public sealed class FakeMediaInspector : IMediaInspector
{
    private static readonly (string Name, string Edition)[] Editions =
    [
        ("Windows 11 Home", "Core"), ("Windows 11 Home N", "CoreN"), ("Windows 11 Home Single Language", "CoreSingleLanguage"),
        ("Windows 11 Education", "Education"), ("Windows 11 Education N", "EducationN"), ("Windows 11 Pro", "Professional"),
        ("Windows 11 Pro N", "ProfessionalN"), ("Windows 11 Pro Education", "ProfessionalEducation"),
    ];

    /// <summary>When set, every inspection fails with this reason.</summary>
    public string? Error { get; set; }

    public Task<MediaInspection> InspectAsync(string isoPath, MediaKind kind, CancellationToken ct)
    {
        if (Error is { } error) return Task.FromResult(new MediaInspection(null, error));
        var contents = kind == MediaKind.OsInstaller
            ? new MediaContents(Editions.Select((e, i) => new InstallerImage(i + 1, e.Name, e.Edition, "26300.1", "x86_64")).ToList(), null)
            : new MediaContents([], "Office");
        return Task.FromResult(new MediaInspection(contents, null));
    }
}
