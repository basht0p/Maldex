using Maldex.Agent.Collectors;
using Maldex.Core.Agent;
using Xunit;

namespace Maldex.Agent.Tests;

public class SysmonEventMapperTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch;

    private static AgentMessage? Map(int id, params (string, string)[] f) =>
        SysmonEventMapper.Map(id, At, f.ToDictionary(x => x.Item1, x => x.Item2));

    [Fact]
    public void MapsProcessStart()
    {
        var m = Assert.IsType<ProcessStartedMessage>(Map(1, ("ProcessId", "42"), ("ParentProcessId", "4"), ("Image", @"C:\a.exe"), ("CommandLine", "a.exe -x"), ("User", "U"), ("IntegrityLevel", "High")));
        Assert.Equal((42, 4, "High"), (m.Pid, m.ParentPid, m.Integrity));
    }

    [Fact]
    public void UnknownEventAndRenameKey_AreNull()
    {
        Assert.Null(Map(99));
        Assert.Null(Map(14, ("ProcessId", "7"), ("TargetObject", @"HKLM\a")));
        Assert.Null(Map(12, ("ProcessId", "7"), ("EventType", "RenameKey")));
    }

    [Fact]
    public void Registry_MapsToValidatorOperations()
    {
        var c = Assert.IsType<RegistryEventMessage>(Map(12, ("ProcessId", "7"), ("EventType", "CreateKey"), ("TargetObject", @"HKLM\S\K")));
        Assert.Equal("create", c.Operation);
        Assert.Equal("delete", Assert.IsType<RegistryEventMessage>(Map(12, ("ProcessId", "7"), ("EventType", "DeleteValue"), ("TargetObject", @"HKLM\S\K\v"))).Operation);
        var s = Assert.IsType<RegistryEventMessage>(Map(13, ("ProcessId", "7"), ("TargetObject", @"HKLM\S\Run\evil"), ("Details", "c:\\x.exe")));
        Assert.Equal(("set", @"HKLM\S\Run", "evil", "c:\\x.exe"), (s.Operation, s.Key, s.ValueName, s.Data));
    }

    /// <summary>Sysmon reports HKCU writes under the user's SID (HKU\&lt;SID&gt;\...); the mapper keeps that form and the run-key heuristic must fire on it.</summary>
    [Fact]
    public void Registry_RealSysmonHkuRunValue_MapsAndTriggersPersistenceHeuristic()
    {
        const string target = @"HKU\S-1-5-21-3623811015-3361044348-30300820-1013\Software\Microsoft\Windows\CurrentVersion\Run\MaldexTest";
        var s = Assert.IsType<RegistryEventMessage>(Map(13, ("ProcessId", "4242"), ("EventType", "SetValue"), ("TargetObject", target), ("Details", @"C:\Users\u\AppData\Local\Temp\x.exe")));
        Assert.Equal(@"HKU\S-1-5-21-3623811015-3361044348-30300820-1013\Software\Microsoft\Windows\CurrentVersion\Run", s.Key);
        Assert.Equal("MaldexTest", s.ValueName);
        Assert.Null(AgentMessageValidator.Validate(s));
        var ev = new Maldex.Core.Domain.BehaviorEvent
        {
            Type = Maldex.Core.Domain.EventType.Registry, At = At,
            Data = System.Text.Json.JsonSerializer.Serialize<AgentMessage>(s, AgentJson.Options),
        };
        var d = Assert.Single(Maldex.Core.Verdicts.BehaviorHeuristics.Evaluate([], [ev]));
        Assert.Equal(Maldex.Core.Verdicts.BehaviorHeuristics.PersistenceRunKey, d.Rule);
        Assert.Equal(target, d.Target);
    }

    [Fact]
    public void Dns_NormalisesRealisticQueryResults()
    {
        var m = Assert.IsType<DnsEventMessage>(Map(22, ("ProcessId", "7"), ("QueryName", "a.test"), ("QueryStatus", "0"),
            ("QueryResults", "type:  5 www.a.test;::ffff:93.184.216.34;2001:db8::1;")));
        Assert.Null(m.QueryType);
        Assert.Equal(["93.184.216.34", "2001:db8::1"], m.Answers!);
    }

    [Fact]
    public void Network_HonoursInitiated()
    {
        var outb = Assert.IsType<NetworkEventMessage>(Map(3, ("ProcessId", "7"), ("Protocol", "tcp"), ("Initiated", "true"),
            ("SourceIp", "10.0.0.2"), ("SourcePort", "50000"), ("DestinationIp", "1.2.3.4"), ("DestinationPort", "443")));
        Assert.Equal(("1.2.3.4", 443, "10.0.0.2", 50000), (outb.RemoteAddress, outb.RemotePort, outb.LocalAddress, outb.LocalPort));
        var inb = Assert.IsType<NetworkEventMessage>(Map(3, ("ProcessId", "7"), ("Protocol", "TCP"), ("Initiated", "false"),
            ("SourceIp", "1.2.3.4"), ("SourcePort", "5555"), ("DestinationIp", "10.0.0.2"), ("DestinationPort", "80")));
        Assert.Equal(("1.2.3.4", 5555, "10.0.0.2", 80, "tcp"), (inb.RemoteAddress, inb.RemotePort, inb.LocalAddress, inb.LocalPort, inb.Protocol));
    }

    [Fact]
    public void OwnPid_IsFiltered() =>
        Assert.Null(SysmonEventMapper.Map(11, At, new Dictionary<string, string> { ["ProcessId"] = "99", ["TargetFilename"] = "x" }, ignorePid: 99));

    [Fact]
    public void EveryMappedMessage_PassesValidator()
    {
        var common = new (string, string)[] { ("ProcessId", "7"), ("Image", @"C:\a.exe"), ("CommandLine", ""), ("TargetFilename", @"C:\f"),
            ("TargetObject", @"HKLM\S\K\v"), ("Protocol", "udp"), ("DestinationIp", "1.1.1.1"), ("DestinationPort", "53"),
            ("QueryName", "a.test"), ("QueryResults", "::ffff:1.1.1.1;") };
        var mapped = 0;
        foreach (var id in new[] { 1, 5, 11, 23, 12, 13, 14, 3, 22 })
        {
            var extra = id == 12 ? new[] { ("EventType", "CreateKey") } : [];
            var m = Map(id, [.. common, .. extra]);
            if (m is null) { Assert.Equal(14, id); continue; }
            mapped++;
            Assert.Null(AgentMessageValidator.Validate(m));
        }
        Assert.Equal(8, mapped);
    }
}
