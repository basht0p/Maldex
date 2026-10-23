using System.Text.Json;
using System.Text.RegularExpressions;
using Maldex.Core.Domain;

namespace Maldex.Core.Verdicts;

public static class BehaviorHeuristics
{
    public const string OfficeSpawnsProcess = "office_spawns_process";
    public const string PersistenceRunKey = "persistence_run_key";
    public const string DroppedExecutableExecuted = "dropped_executable_executed";

    private static readonly HashSet<string> OfficeParents = new(StringComparer.OrdinalIgnoreCase)
    { "winword.exe", "excel.exe", "powerpnt.exe", "outlook.exe", "msaccess.exe", "mspub.exe", "visio.exe", "onenote.exe" };

    private static readonly HashSet<string> ChildAllowlist = new(StringComparer.OrdinalIgnoreCase)
    { "splwow64.exe", "werfault.exe" };

    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".exe", ".dll", ".scr", ".com" };

    // Hive prefixes: abbreviated/long names, Sysmon's per-user form (HKU\<SID>\..., how it reports HKCU writes) and the kernel
    // forms (\REGISTRY\MACHINE, \REGISTRY\USER\<SID>). A user's *_Classes hive has no Run key.
    private static readonly Regex RunKey = new(
        @"(?i)^(HKLM|HKCU|HKEY_LOCAL_MACHINE|HKEY_CURRENT_USER|\\REGISTRY\\MACHINE|(HKU|HKEY_USERS|\\REGISTRY\\USER)\\(?![^\\]*_Classes\\)[^\\]+)" +
        @"\\Software\\(WOW6432Node\\)?Microsoft\\Windows\\CurrentVersion\\Run(Once)?(\\|$)",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static IReadOnlyList<DetectionResult> Evaluate(IReadOnlyList<ProcessNode> processes, IReadOnlyList<BehaviorEvent> events)
    {
        var found = new List<DetectionResult>();
        OfficeSpawns(processes, found);
        EventRules(processes, events, found);

        var seen = new HashSet<(string, string)>();
        return found
            .Where(d => seen.Add((d.Rule, d.Target.ToLowerInvariant())))
            .OrderBy(d => d.Rule, StringComparer.Ordinal)
            .ThenBy(d => d.Target, StringComparer.Ordinal)
            .ToList();
    }

    private static DetectionResult Make(string rule, string target, string? details) =>
        new(DetectionSource.Heuristic, rule, 2, ["heuristic"], target, details);

    private static string FileName(string path) => Path.GetFileName(path.Replace('\\', '/'));

    private static void OfficeSpawns(IReadOnlyList<ProcessNode> processes, List<DetectionResult> found)
    {
        var byId = processes.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());
        foreach (var child in processes)
        {
            ProcessNode? parent = null;
            if (child.ParentNodeId is { } pid) byId.TryGetValue(pid, out parent);
            else
                parent = processes.Where(p => p.Pid == child.ParentPid && p.StartedAt <= child.StartedAt && !ReferenceEquals(p, child))
                                  .OrderByDescending(p => p.StartedAt).FirstOrDefault();
            if (parent is null) continue;
            var pf = FileName(parent.ImagePath);
            var cf = FileName(child.ImagePath);
            if (OfficeParents.Contains(pf) && !ChildAllowlist.Contains(cf))
                found.Add(Make(OfficeSpawnsProcess, child.ImagePath, $"{pf} → {cf}"));
        }
    }

    private static void EventRules(IReadOnlyList<ProcessNode> processes, IReadOnlyList<BehaviorEvent> events, List<DetectionResult> found)
    {
        foreach (var e in events)
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(e.Data); }
            catch (Exception ex) when (ex is JsonException or ArgumentNullException) { continue; }
            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                var root = doc.RootElement;
                if (e.Type == EventType.Registry)
                {
                    var op = Str(root, "operation");
                    var key = Str(root, "key");
                    if (key is null || op is null) continue;
                    if (!(op.Equals("set", StringComparison.OrdinalIgnoreCase) || op.Equals("create", StringComparison.OrdinalIgnoreCase))) continue;
                    if (!RunKey.IsMatch(key)) continue;
                    var val = Str(root, "valueName");
                    found.Add(Make(PersistenceRunKey, key + (val != null ? "\\" + val : ""), null));
                }
                else if (e.Type == EventType.File)
                {
                    var op = Str(root, "operation");
                    if (op is null) continue;
                    string? path = op.ToLowerInvariant() switch
                    {
                        "create" or "write" => Str(root, "path"),
                        "rename" => Str(root, "newPath"),
                        _ => null,
                    };
                    if (path is null) continue;
                    var ext = Path.GetExtension(path.Replace('\\', '/'));
                    if (!ExecutableExtensions.Contains(ext)) continue;
                    var run = processes.FirstOrDefault(p =>
                        string.Equals(p.ImagePath, path, StringComparison.OrdinalIgnoreCase) && p.StartedAt >= e.At);
                    if (run is not null)
                        found.Add(Make(DroppedExecutableExecuted, run.ImagePath, $"{FileName(path)} written then executed"));
                }
            }
        }
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
