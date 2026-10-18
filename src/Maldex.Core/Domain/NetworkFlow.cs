namespace Maldex.Core.Domain;

public class NetworkFlow
{
    public long Id { get; set; }
    public Guid TaskId { get; set; }
    public string Proto { get; set; } = "";
    public string Src { get; set; } = "";
    public string Dst { get; set; } = "";
    public int DstPort { get; set; }
    public long Bytes { get; set; }
    public DateTimeOffset StartedAt { get; set; }
}
