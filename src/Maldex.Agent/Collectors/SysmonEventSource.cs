using System.Diagnostics.Eventing.Reader;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Xml.Linq;
using Maldex.Core.Agent;

namespace Maldex.Agent.Collectors;

/// <summary>
/// Streams Sysmon operational events as protocol messages. Windows only; exercised on the real guest.
/// <para>
/// Polls with a bookmark instead of subscribing (<see cref="EventLogWatcher"/>): on the real guest the push subscription went
/// silent for good after the first burst (thousands of registry events within seconds of an open), without raising anything
/// the handler logged. Polling reads every record after the last one forwarded, so a stall or a read error loses nothing:
/// the reader is reopened at the bookmark and the error is logged.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SysmonEventSource(TimeProvider time) : IEventSource
{
    public const string LogName = "Microsoft-Windows-Sysmon/Operational";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ErrorBackoff = TimeSpan.FromSeconds(1);

    public async IAsyncEnumerable<AgentMessage> Events([EnumeratorCancellation] CancellationToken ct)
    {
        var query = new EventLogQuery(LogName, PathType.LogName, "*");
        // Start after the newest record that exists now: the boot backlog is not part of the run.
        var bookmark = Newest(query);
        while (!ct.IsCancellationRequested)
        {
            var batch = new List<AgentMessage>();
            try
            {
                using var reader = bookmark is null ? new EventLogReader(query) : new EventLogReader(query, bookmark);
                while (batch.Count < 512 && reader.ReadEvent() is { } record)
                {
                    using (record)
                    {
                        bookmark = record.Bookmark;
                        try
                        {
                            if (Convert(record) is { } msg) batch.Add(msg);
                        }
                        catch (Exception ex)
                        {
                            AgentLog.Write("Sysmon record skipped: " + ex.Message);
                        }
                    }
                }
            }
            catch (EventLogException ex)
            {
                AgentLog.Write("Sysmon read failed, retrying at the bookmark: " + ex.Message);
                await Task.Delay(ErrorBackoff, time, ct).ConfigureAwait(false);
                continue;
            }

            foreach (var m in batch) yield return m;
            // A full batch means more is waiting: read on at once.
            if (batch.Count < 512) await Task.Delay(PollInterval, time, ct).ConfigureAwait(false);
        }
    }

    private static EventBookmark? Newest(EventLogQuery query)
    {
        try
        {
            using var reader = new EventLogReader(query);
            reader.Seek(System.IO.SeekOrigin.End, -1);
            using var last = reader.ReadEvent();
            return last?.Bookmark;
        }
        catch (EventLogException ex)
        {
            AgentLog.Write("Sysmon log position unknown, reading from its start: " + ex.Message);
            return null;
        }
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
