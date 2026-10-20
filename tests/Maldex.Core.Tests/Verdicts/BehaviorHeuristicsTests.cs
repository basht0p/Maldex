using System.Text.Json;
using Xunit;
using Maldex.Core.Agent;
using Maldex.Core.Domain;
using Maldex.Core.Verdicts;

namespace Maldex.Core.Tests.Verdicts;

public class BehaviorHeuristicsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static ProcessNode P(long id, int pid, int ppid, string image, int sec, long? parentNode = null) =>
        new() { Id = id, Pid = pid, ParentPid = ppid, ParentNodeId = parentNode, ImagePath = image, StartedAt = T0.AddSeconds(sec) };

    private static BehaviorEvent E(EventType t, AgentMessage m, int sec) =>
        new() { Type = t, At = T0.AddSeconds(sec), Data = JsonSerializer.Serialize(m, AgentJson.Options) };

    private static BehaviorEvent Reg(string op, string key, string? val = null, int sec = 1) =>
        E(EventType.Registry, new RegistryEventMessage(T0.AddSeconds(sec), 5, op, key, val), sec);

    private const string Word = @"C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE";

    [Fact] public void WinwordSpawnsCalc_Fires()
    {
        var r = BehaviorHeuristics.Evaluate([P(1, 100, 1, Word, 0), P(2, 200, 100, @"C:\Windows\System32\calc.exe", 5)], []);
        var d = Assert.Single(r);
        Assert.Equal("office_spawns_process", d.Rule);
        Assert.Equal(DetectionSource.Heuristic, d.Source);
        Assert.Equal(2, d.Severity);
        Assert.Equal(@"C:\Windows\System32\calc.exe", d.Target);
        Assert.Equal("WINWORD.EXE → calc.exe", d.Details);
        Assert.Equal(["heuristic"], d.Tags);
    }

    [Fact] public void WinwordSpawnsSplwow64_DoesNotFire() =>
        Assert.Empty(BehaviorHeuristics.Evaluate([P(1, 100, 1, Word, 0), P(2, 200, 100, @"C:\Windows\splwow64.exe", 5)], []));

    [Fact] public void ParentNodeId_IsPreferred()
    {
        var r = BehaviorHeuristics.Evaluate([P(1, 100, 1, Word, 0), P(2, 100, 1, @"C:\x\other.exe", 1), P(3, 300, 999, @"C:\Windows\calc.exe", 5, parentNode: 1)], []);
        Assert.Single(r);
    }

    [Fact] public void RunKeySet_HKCU_Fires()
    {
        var r = BehaviorHeuristics.Evaluate([], [Reg("set", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "Upd")]);
        var d = Assert.Single(r);
        Assert.Equal("persistence_run_key", d.Rule);
        Assert.Equal(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Upd", d.Target);
    }

    [Fact] public void RunOnceKeySet_HKLM_Fires() =>
        Assert.Single(BehaviorHeuristics.Evaluate([], [Reg("set", @"HKLM\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce\Sub")]));

    [Theory]
    [InlineData(@"HKU\S-1-5-21-3623811015-3361044348-30300820-1013\Software\Microsoft\Windows\CurrentVersion\Run")]
    [InlineData(@"HKU\S-1-5-21-1-2-3-1001\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce")]
    [InlineData(@"HKEY_USERS\S-1-5-21-1-2-3-1001\Software\Microsoft\Windows\CurrentVersion\Run")]
    [InlineData(@"\REGISTRY\USER\S-1-5-21-1-2-3-1001\Software\Microsoft\Windows\CurrentVersion\Run")]
    [InlineData(@"\REGISTRY\MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Run")]
    public void RunKeySet_SysmonHiveForms_Fire(string key)
    {
        var d = Assert.Single(BehaviorHeuristics.Evaluate([], [Reg("set", key, "MaldexTest")]));
        Assert.Equal("persistence_run_key", d.Rule);
        Assert.Equal(key + @"\MaldexTest", d.Target);
    }

    [Theory]
    [InlineData(@"HKU\S-1-5-21-1-2-3-1001_Classes\Software\Microsoft\Windows\CurrentVersion\Run")] // classes hive, not a run key
    [InlineData(@"HKU\Software\Microsoft\Windows\CurrentVersion\Run")] // no SID component
    [InlineData(@"HKU\S-1-5-21-1-2-3-1001\x\Software\Microsoft\Windows\CurrentVersion\Run")]
    [InlineData(@"\REGISTRY\USER\Software\Microsoft\Windows\CurrentVersion\Run")]
    public void RunKeyLookalikes_InUserHives_DoNotFire(string key) =>
        Assert.Empty(BehaviorHeuristics.Evaluate([], [Reg("set", key, "v")]));

    [Fact] public void RunKeyRead_DoesNotFire()
    {
        Assert.Empty(BehaviorHeuristics.Evaluate([], [Reg("create", @"HKCU\Software\Microsoft\Windows\CurrentVersion\RunServices")]));
        Assert.Empty(BehaviorHeuristics.Evaluate([], [Reg("read", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run")]));
    }

    [Fact] public void DroppedExeExecuted_Fires_CaseInsensitive()
    {
        var f = E(EventType.File, new FileEventMessage(T0.AddSeconds(1), 5, "create", @"C:\Users\a\AppData\Local\Temp\x.EXE"), 1);
        var r = BehaviorHeuristics.Evaluate([P(1, 9, 1, @"c:\users\a\appdata\local\temp\x.exe", 2)], [f]);
        var d = Assert.Single(r);
        Assert.Equal("dropped_executable_executed", d.Rule);
        Assert.Equal(@"c:\users\a\appdata\local\temp\x.exe", d.Target);
    }

    [Fact] public void DroppedViaRename_Fires()
    {
        var f = E(EventType.File, new FileEventMessage(T0.AddSeconds(1), 5, "rename", @"C:\t\a.tmp", @"C:\t\a.dll"), 1);
        Assert.Single(BehaviorHeuristics.Evaluate([P(1, 9, 1, @"C:\t\a.dll", 2)], [f]));
    }

    [Fact] public void ExeExecutedBeforeDrop_DoesNotFire()
    {
        var f = E(EventType.File, new FileEventMessage(T0.AddSeconds(5), 5, "create", @"C:\t\x.exe"), 5);
        Assert.Empty(BehaviorHeuristics.Evaluate([P(1, 9, 1, @"C:\t\x.exe", 2)], [f]));
    }

    [Fact] public void MalformedData_IsSkipped()
    {
        var bad = new[] { new BehaviorEvent { Type = EventType.Registry, Data = "not json" }, new BehaviorEvent { Type = EventType.File, Data = "{}" } };
        Assert.Empty(BehaviorHeuristics.Evaluate([], bad));
    }

    [Fact] public void Duplicates_AreCollapsed_AndOrdered()
    {
        var k = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run";
        var r = BehaviorHeuristics.Evaluate([], [Reg("set", k, "B"), Reg("create", k.ToLowerInvariant(), "b"), Reg("set", k, "A")]);
        Assert.Equal(2, r.Count);
        Assert.EndsWith("\\A", r[0].Target);
    }
}
