using System.Text.Json;

namespace Maldex.Infrastructure.Scanning;

/// <summary>One HTTP(S) exchange from the mitmproxy addon's JSONL. Everything here is still untrusted guest-derived data.</summary>
public sealed record ParsedHttpTransaction(
    DateTimeOffset At,
    string Method,
    string Url,
    string Host,
    int? Status,
    IReadOnlyList<KeyValuePair<string, string>> RequestHeaders,
    IReadOnlyList<KeyValuePair<string, string>> ResponseHeaders,
    byte[]? ResponseBody,
    bool Tls);

public sealed record MitmParseResult(IReadOnlyList<ParsedHttpTransaction> Flows, int Ignored, int BodiesDropped = 0, long BodyBytes = 0, int LinesSkipped = 0);

/// <summary>Parses the mitmproxy flow JSONL (one object per line). Malformed lines are skipped; at most <see cref="MaxFlows"/> flows are kept.</summary>
public static class MitmFlowParser
{
    public const int MaxFlows = 10_000;
    public const int MaxLineBytes = 48 * 1024 * 1024;
    public const long MaxBodyBytes = 32L * 1024 * 1024;
    public const long MaxTotalBodyBytes = 256L * 1024 * 1024;
    public const int MaxHeaders = 200;

    public static IReadOnlyList<ParsedHttpTransaction> Parse(Stream jsonl) => ParseWithStats(jsonl).Flows;

    /// <summary>
    /// <see cref="MitmParseResult.Ignored"/> counts non-blank lines past <paramref name="maxFlows"/> (not validated).
    /// Lines over <paramref name="maxLineBytes"/> are skipped without being buffered. Bodies over <paramref name="maxBodyBytes"/>,
    /// or beyond the remaining <paramref name="bodyBudget"/>, are dropped (the flow is kept).
    /// </summary>
    public static MitmParseResult ParseWithStats(Stream jsonl, int maxFlows = MaxFlows, long bodyBudget = MaxTotalBodyBytes,
        int maxLineBytes = MaxLineBytes, long maxBodyBytes = MaxBodyBytes)
    {
        var flows = new List<ParsedHttpTransaction>();
        int ignored = 0, dropped = 0, skipped = 0;
        long used = 0;
        foreach (var (line, tooLong) in ReadLines(jsonl, maxLineBytes))
        {
            if (tooLong) { skipped++; continue; }
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (flows.Count >= maxFlows) { ignored++; continue; }
            var flow = ParseLine(line!, maxBodyBytes, bodyBudget - used, ref dropped, out var bodyLen);
            if (flow is null) { skipped++; continue; }
            used += bodyLen;
            flows.Add(flow);
        }
        return new MitmParseResult(flows, ignored, dropped, used, skipped);
    }

    /// <summary>Splits on LF without ever holding more than <paramref name="maxLineBytes"/> of one line.</summary>
    private static IEnumerable<(string? Line, bool TooLong)> ReadLines(Stream stream, int maxLineBytes)
    {
        var buffer = new byte[81920];
        var line = new MemoryStream();
        var overflow = false;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            var start = 0;
            while (start < read)
            {
                var nl = Array.IndexOf(buffer, (byte)'\n', start, read - start);
                var end = nl < 0 ? read : nl;
                if (!overflow)
                {
                    if (line.Length + (end - start) > maxLineBytes) { overflow = true; line.SetLength(0); }
                    else line.Write(buffer, start, end - start);
                }
                if (nl < 0) break;
                yield return overflow ? (null, true) : (System.Text.Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length), false);
                line.SetLength(0);
                overflow = false;
                start = nl + 1;
            }
        }
        if (overflow) yield return (null, true);
        else if (line.Length > 0) yield return (System.Text.Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length), false);
    }

    private static ParsedHttpTransaction? ParseLine(string line, long maxBodyBytes, long budgetLeft, ref int dropped, out long bodyLen)
    {
        bodyLen = 0;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (!root.TryGetProperty("ts", out var ts) || ts.ValueKind != JsonValueKind.Number || !ts.TryGetDouble(out var seconds)) return null;
            if (double.IsNaN(seconds) || Math.Abs(seconds) > 253_402_300_000) return null;
            var method = Str(root, "method");
            var url = Str(root, "url");
            var host = Str(root, "host");
            if (method is null || url is null || host is null) return null;

            int? status = root.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Number && st.TryGetInt32(out var code) ? code : null;
            var tls = root.TryGetProperty("tls", out var t) && t.ValueKind == JsonValueKind.True;
            var reqHeaders = Headers(root, "req_headers");
            var respHeaders = Headers(root, "resp_headers");
            var body = Body(root, "resp_body_b64", maxBodyBytes, budgetLeft, ref dropped);
            bodyLen = body?.Length ?? 0;

            return new ParsedHttpTransaction(
                DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(seconds * 1000)),
                method, url, host, status, reqHeaders, respHeaders, body, tls);
        }
        catch (Exception) { return null; } // any hostile line (bad JSON, lone surrogates, ...) is skipped
    }

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static List<KeyValuePair<string, string>> Headers(JsonElement o, string name)
    {
        var list = new List<KeyValuePair<string, string>>();
        if (!o.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var pair in arr.EnumerateArray())
        {
            if (list.Count >= MaxHeaders) break;
            if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() != 2) continue;
            var k = pair[0];
            var v = pair[1];
            if (k.ValueKind != JsonValueKind.String || v.ValueKind != JsonValueKind.String) continue;
            list.Add(new(k.GetString()!, v.GetString()!));
        }
        return list;
    }

    private static byte[]? Body(JsonElement o, string name, long maxBodyBytes, long budgetLeft, ref int dropped)
    {
        var s = Str(o, name);
        if (string.IsNullOrEmpty(s)) return null;
        if (s.Length > maxBodyBytes * 4 / 3 + 4) { dropped++; return null; }
        byte[] bytes;
        try { bytes = Convert.FromBase64String(s); }
        catch (FormatException) { return null; }
        if (bytes.Length > maxBodyBytes || bytes.Length > budgetLeft) { dropped++; return null; }
        return bytes;
    }
}
