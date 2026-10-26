namespace Maldex.Core.Domain;

/// <summary>Installation-wide settings. Exactly one row exists, with <c>Id = 1</c>.</summary>
public class SystemSettings
{
    public int Id { get; set; } = 1;
    public bool RequireTotpForLocal { get; set; }
}
