namespace Maldex.Core.Abstractions;

/// <summary>The only code that touches template version disk files (a qcow2 plus its NVRAM, named by one file stem).</summary>
public interface ITemplateStore
{
    // CreateWorkingCopyAsync/SealAsync have throwing default bodies so test doubles that predate them keep compiling; real stores override.

    /// <summary>Both files of the stem exist (fake: always true).</summary>
    bool Exists(string fileStem);

    /// <summary>Size of the disk file, or null when unknown/absent.</summary>
    long? SizeOf(string fileStem);

    /// <summary>Full copy of both files of <paramref name="fromStem"/> to <paramref name="toStem"/> as writable (0600) files; throws if the target exists.</summary>
    Task CreateWorkingCopyAsync(string fromStem, string toStem, CancellationToken ct) => throw new NotSupportedException();

    /// <summary>Makes both files read-only (0444): from now on they are a version disk.</summary>
    Task SealAsync(string fileStem, CancellationToken ct) => throw new NotSupportedException();

    /// <summary>Deletes both files (version disk or working copy) and any temporary files of an interrupted copy; idempotent.</summary>
    Task DeleteAsync(string fileStem, CancellationToken ct);
}
