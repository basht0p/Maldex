using System.Text.Json;
using Maldex.Core.Domain;
using Maldex.Core.Text;
using Maldex.Core.Verdicts;

namespace Maldex.Infrastructure.Scanning;

/// <summary>
/// Turns Suricata's <c>eve.json</c> (JSON lines) into detections. Only <c>alert</c> events count. Guest traffic is
/// hostile, so output is capped at <see cref="MaxAlerts"/> (1,000) alerts: parsing stops once the cap is reached and
/// the remaining lines are ignored (no marker result is added).
/// </summary>
public static class SuricataEveParser
{
    /// <summary>The most alerts a single parse returns.</summary>
    public const int MaxAlerts = 1000;

    public static IReadOnlyList<DetectionResult> Parse(string eveJsonl) => Parse(eveJsonl.Split('\n'), MaxAlerts);

    /// <summary>Parses lazily; enumeration stops as soon as <paramref name="maxAlerts"/> alerts were found.</summary>
    public static IReadOnlyList<DetectionResult> Parse(IEnumerable<string> lines, int maxAlerts = MaxAlerts)
    {
        var result = new List<DetectionResult>();
        foreach (var raw in lines)
        {
            if (result.Count >= maxAlerts) break;
            var line = raw.Trim();
            if (line.Length == 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (Str(root, "event_type") != "alert") continue;
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
        return result;
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
