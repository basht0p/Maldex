using System.Diagnostics.Eventing.Reader;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using System.Xml.Linq;
using Maldex.Core.Agent;

namespace Maldex.Agent.Collectors;

/// <summary>Streams Sysmon operational events as protocol messages. Windows only; exercised on the real guest.</summary>
[SupportedOSPlatform("windows")]
public sealed class SysmonEventSource : IEventSource
{
    public const string LogName = "Microsoft-Windows-Sysmon/Operational";

    public async IAsyncEnumerable<AgentMessage> Events([EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<AgentMessage>();
        using var watcher = new EventLogWatcher(new EventLogQuery(LogName, PathType.LogName, "*"));
        watcher.EventRecordWritten += (_, e) =>
        {
            if (e.EventRecord is null) return;
            using var record = e.EventRecord;
            try
            {
                var msg = Convert(record);
                if (msg is not null) channel.Writer.TryWrite(msg);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Sysmon record skipped: " + ex.Message);
            }
        };
        watcher.Enabled = true;

        await foreach (var m in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return m;
    }

    private static AgentMessage? Convert(EventRecord record)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var doc = XDocument.Parse(record.ToXml());
        foreach (var d in doc.Descendants().Where(x => x.Name.LocalName == "Data"))
        {
            var name = (string?)d.Attribute("Name");
            if (name is not null) fields[name] = d.Value;
        }

        var at = record.TimeCreated is { } t ? new DateTimeOffset(t.ToUniversalTime(), TimeSpan.Zero) : DateTimeOffset.UtcNow;
        return SysmonEventMapper.Map(record.Id, at, fields, Environment.ProcessId);
    }
}
