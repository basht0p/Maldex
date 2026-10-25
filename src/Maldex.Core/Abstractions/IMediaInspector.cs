using Maldex.Core.Domain;
using Maldex.Core.Media;

namespace Maldex.Core.Abstractions;

/// <summary>What looking inside a media file found. <c>Error</c> is a short reason safe to show to the admin.</summary>
public sealed record MediaInspection(MediaContents? Contents, string? Error);

/// <summary>Reads media content without ever running it.</summary>
public interface IMediaInspector
{
    Task<MediaInspection> InspectAsync(string isoPath, MediaKind kind, CancellationToken ct);
}
