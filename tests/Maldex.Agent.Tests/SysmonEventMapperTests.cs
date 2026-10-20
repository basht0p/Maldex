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

    [Theory]
    [InlineData(5, typeof(ProcessExitedMessage))]
    [InlineData(11, typeof(FileEventMessage))]
    [InlineData(23, typeof(FileEventMessage))]
    [InlineData(12, typeof(RegistryEventMessage))]
    [InlineData(13, typeof(RegistryEventMessage))]
    [InlineData(14, typeof(RegistryEventMessage))]
    [InlineData(3, typeof(NetworkEventMessage))]
    [InlineData(22, typeof(DnsEventMessage))]
    public void MapsEventIds(int id, Type expected) => Assert.IsType(expected, Map(id, ("ProcessId", "7")));

    [Fact]
    public void UnknownEvent_IsNull() => Assert.Null(Map(99));

    [Fact]
    public void Dns_SplitsAnswers()
    {
        var m = Assert.IsType<DnsEventMessage>(Map(22, ("ProcessId", "7"), ("QueryName", "a.test"), ("QueryResults", "1.2.3.4;5.6.7.8;")));
        Assert.Equal(["1.2.3.4", "5.6.7.8"], m.Answers!);
    }
}
