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

public sealed record MitmParseResult(IReadOnlyList<ParsedHttpTransaction> Flows, int Ignored);

/// <summary>Parses the mitmproxy flow JSONL (one object per line). Malformed lines are skipped; at most <see cref="MaxFlows"/> flows are kept.</summary>
public static class MitmFlowParser
{
    public const int MaxFlows = 10_000;

    public static IReadOnlyList<ParsedHttpTransaction> Parse(Stream jsonl) => ParseWithStats(jsonl).Flows;

    /// <summary><see cref="MitmParseResult.Ignored"/> counts non-blank lines past the cap (not validated).</summary>
    public static MitmParseResult ParseWithStats(Stream jsonl)
    {
        var flows = new List<ParsedHttpTransaction>();
        var ignored = 0;
        using var reader = new StreamReader(jsonl, System.Text.Encoding.UTF8, true, 81920, leaveOpen: true);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (flows.Count >= MaxFlows) { ignored++; continue; }
            var flow = ParseLine(line);
            if (flow is not null) flows.Add(flow);
        }
        return new MitmParseResult(flows, ignored);
    }

    private static ParsedHttpTransaction? ParseLine(string line)
    {
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

            return new ParsedHttpTransaction(
                DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(seconds * 1000)),
                method, url, host, status,
                Headers(root, "req_headers"), Headers(root, "resp_headers"),
                Body(root, "resp_body_b64"), tls);
        }
        catch (JsonException) { return null; }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static List<KeyValuePair<string, string>> Headers(JsonElement o, string name)
    {
        var list = new List<KeyValuePair<string, string>>();
        if (!o.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var pair in arr.EnumerateArray())
        {
            if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() != 2) continue;
            var k = pair[0];
            var v = pair[1];
            if (k.ValueKind != JsonValueKind.String || v.ValueKind != JsonValueKind.String) continue;
            list.Add(new(k.GetString()!, v.GetString()!));
        }
        return list;
    }

    private static byte[]? Body(JsonElement o, string name)
    {
        var s = Str(o, name);
        if (string.IsNullOrEmpty(s)) return null;
        try { return Convert.FromBase64String(s); }
        catch (FormatException) { return null; }
    }
}
