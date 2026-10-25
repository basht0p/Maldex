namespace Maldex.Web.Endpoints;

/// <summary>CSV cell escaping for the event export, hardened against spreadsheet formula injection.</summary>
public static class EventCsv
{
    public const string Header = "id,type,at,pid,processId,summary,data";

    public static string Cell(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        return value.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
