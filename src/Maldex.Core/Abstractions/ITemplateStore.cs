namespace Maldex.Core.Abstractions;

/// <summary>The only code that touches template version disk files (a qcow2 plus its NVRAM, named by one file stem).</summary>
public interface ITemplateStore
{
    /// <summary>Both files of the stem exist (fake: always true).</summary>
    bool Exists(string fileStem);

    /// <summary>Size of the disk file, or null when unknown/absent.</summary>
    long? SizeOf(string fileStem);

    /// <summary>Deletes both files; idempotent.</summary>
    Task DeleteAsync(string fileStem, CancellationToken ct);
}
