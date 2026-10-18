namespace Maldex.Core.Domain;

public class Sample
{
    public string Sha256 { get; set; } = "";
    public string Sha1 { get; set; } = "";
    public string Md5 { get; set; } = "";
    public long Size { get; set; }
    public string? FileType { get; set; }
    public string? Mime { get; set; }
    public string? OriginalName { get; set; }
    public string BlobKey { get; set; } = "";
    public DateTimeOffset FirstSeen { get; set; }
}
