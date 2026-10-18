namespace Maldex.Core.Domain;

public class HttpTransaction
{
    public long Id { get; set; }
    public Guid TaskId { get; set; }
    public string Method { get; set; } = "";
    public string Url { get; set; } = "";
    public string Host { get; set; } = "";
    public int? Status { get; set; }
    public string ReqHeaders { get; set; } = "";
    public string RespHeaders { get; set; } = "";
    public string? BodyBlobKey { get; set; }
}
