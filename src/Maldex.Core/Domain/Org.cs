namespace Maldex.Core.Domain;

public class Org
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public int RetentionDays { get; set; }
}
