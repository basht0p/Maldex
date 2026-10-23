using System.Globalization;
using System.Text.Json;
using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Maldex.Core.Text;
using Maldex.Core.Verdicts;

namespace Maldex.Infrastructure.Scanning;

/// <summary>
/// Turns Suricata's <c>eve.json</c> (JSON lines) into detections (<c>alert</c> events) and flows (<c>flow</c> events). Guest
/// traffic is hostile, so output is capped at <see cref="MaxAlerts"/> (1,000) alerts and <see cref="MaxFlows"/> (10,000)
/// flows: reading stops once both caps are reached and the remaining lines are ignored (no marker result is added).
/// </summary>
public static class SuricataEveParser
{
    /// <summary>The most alerts a single parse returns.</summary>
    public const int MaxAlerts = 1000;

    /// <summary>The most flows a single parse returns.</summary>
    public const int MaxFlows = 10_000;

    public static IReadOnlyList<DetectionResult> Parse(string eveJsonl) => ParseEve(eveJsonl.Split('\n'), MaxAlerts, 0).Alerts;

    /// <summary>Alerts only; enumeration stops as soon as <paramref name="maxAlerts"/> alerts were found.</summary>
    public static IReadOnlyList<DetectionResult> Parse(IEnumerable<string> lines, int maxAlerts = MaxAlerts) => ParseEve(lines, maxAlerts, 0).Alerts;

    /// <summary>Parses lazily; enumeration stops as soon as both caps are reached.</summary>
    public static SuricataResult ParseEve(IEnumerable<string> lines, int maxAlerts = MaxAlerts, int maxFlows = MaxFlows)
    {
        var result = new List<DetectionResult>();
        var flows = new List<SuricataFlow>();
        foreach (var raw in lines)
        {
            if (result.Count >= maxAlerts && flows.Count >= maxFlows) break;
            var line = raw.Trim();
            if (line.Length == 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                var type = Str(root, "event_type");
                if (type == "flow")
                {
                    if (flows.Count < maxFlows && ToFlow(root) is { } flow) flows.Add(flow);
                    continue;
                }
                if (type != "alert" || result.Count >= maxAlerts) continue;
                if (!root.TryGetProperty("alert", out var alert) || alert.ValueKind != JsonValueKind.Object) continue;
                var signature = Str(alert, "signature");
                if (string.IsNullOrWhiteSpace(signature)) continue;

                var severity = alert.TryGetProperty("severity", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out var sv)
                    ? sv switch { 1 => 1, 2 => 2, _ => 3 }
                    : 3;
                var category = Str(alert, "category");
                IReadOnlyList<string> tags = string.IsNullOrWhiteSpace(category) ? [] : [GuestText.Clean(category)];
                var target = $"{Str(root, "proto")} {Str(root, "src_ip")}:{Str(root, "src_port")} → {Str(root, "dest_ip")}:{Str(root, "dest_port")}";

                result.Add(new DetectionResult(
                    DetectionSource.Suricata,
                    GuestText.Clean(signature),
                    severity,
                    tags,
                    GuestText.Clean(target.Trim()),
                    GuestText.Clean($"sid {Str(alert, "signature_id")} rev {Str(alert, "rev")}")));
            }
            catch (JsonException) { }
        }
        return new SuricataResult(result, flows);
    }

    private static SuricataFlow? ToFlow(JsonElement root)
    {
        if (!root.TryGetProperty("flow", out var flow) || flow.ValueKind != JsonValueKind.Object) return null;
        var proto = Str(root, "proto");
        var src = Str(root, "src_ip");
        var dst = Str(root, "dest_ip");
        if (proto.Length == 0 || src.Length == 0 || dst.Length == 0) return null;
        var port = 0; // ICMP and friends have none
        if (root.TryGetProperty("dest_port", out var p) && !(p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out port) && port is >= 0 and <= 65535))
            return null;
        if (!TryTime(Str(flow, "start"), out var start)) return null;
        var bytes = Count(flow, "bytes_toserver") + Count(flow, "bytes_toclient");
        return new SuricataFlow(GuestText.Clean(proto, 16), GuestText.Clean(src, 128), GuestText.Clean(dst, 256), port, bytes, start);
    }

    private static long Count(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) && n > 0 ? n : 0;

    // Suricata writes "2026-10-23T19:33:12.123456+0000" (no colon in the offset).
    private static bool TryTime(string s, out DateTimeOffset at)
    {
        if (s.Length > 5 && (s[^5] == '+' || s[^5] == '-') && char.IsDigit(s[^1])) s = s[..^2] + ":" + s[^2..];
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out at);
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() ?? "",
                JsonValueKind.Number => v.GetRawText(),
                _ => "",
            }
            : "";
}
