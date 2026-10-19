using System.Text;
using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Maldex.Core.Verdicts;
using Maldex.Integration.Tests.Fixtures;
using Maldex.Integration.Tests.Orchestrator;
using Maldex.Processing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Maldex.Integration.Tests.Processing;

[Collection("sql")]
public class ProcessingTests(SqlServerFixture sql)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The standard antivirus test file (split so this source file is not itself flagged by scanners).
    private static readonly byte[] EicarBytes =
        Encoding.ASCII.GetBytes(@"X5O!P%@AP[4\PZX54(P^)7CC)7}$" + "EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*");

    private static byte[] Unique() => Encoding.ASCII.GetBytes("benign sample " + Guid.NewGuid());

    private static Dictionary<string, string?> Replay(string file) => new() { ["Maldex:Fake:Replay:DefaultFile"] = file };

    /// <summary>Seeds a task with a stored sample, runs it through the fake orchestrator to <c>Processing</c>.</summary>
    private static async Task<Guid> RunToProcessingAsync(OrchestratorHarness h, byte[] sample)
    {
        var id = await h.SeedPreparingTaskAsync(sampleContent: sample);
        await h.RunAsync(id);
        Assert.Equal(RunStatus.Processing, await h.StatusOfAsync(id));
        return id;
    }

    [Fact]
    public async Task Eicar_IsMalicious()
    {
        var yara = new Maldex.Infrastructure.Scanning.YaraOptions().Binary;
        Assert.SkipUnless(YaraBinaryAvailable(yara), $"The '{yara}' binary is not installed (apt install yara); skipping the EICAR scan.");

        await using var h = await OrchestratorHarness.CreateAsync(sql);
        var id = await RunToProcessingAsync(h, EicarBytes);

        await h.Get<ProcessingPipeline>().ProcessAsync(id, Ct);

        await using var db = h.NewContext();
        var task = await db.Tasks.SingleAsync(t => t.Id == id, Ct);
        Assert.Equal(RunStatus.Completed, task.Status);
        Assert.Equal(Verdict.Malicious, task.Verdict);
        Assert.Empty(task.ProcessingWarnings);
        var d = Assert.Single(await db.Detections.Where(x => x.TaskId == id).ToListAsync(Ct));
        Assert.Equal(DetectionSource.Yara, d.Source);
        Assert.Equal("EICAR_Test_File", d.Rule);
        Assert.Equal("malicious,test", d.Tags);
        Assert.NotNull(task.CompletedAt);
    }

    [Fact]
    public async Task MacroCalcReplay_IsSuspicious_WithOfficeHeuristic()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql, Replay("macro-calc.jsonl"));
        var id = await RunToProcessingAsync(h, Unique());

        await h.Get<ProcessingPipeline>().ProcessAsync(id, Ct);

        await using var db = h.NewContext();
        var task = await db.Tasks.SingleAsync(t => t.Id == id, Ct);
        Assert.Equal(RunStatus.Completed, task.Status);
        Assert.Equal(Verdict.Suspicious, task.Verdict);
        var d = Assert.Single(await db.Detections.Where(x => x.TaskId == id).ToListAsync(Ct));
        Assert.Equal(DetectionSource.Heuristic, d.Source);
        Assert.Equal(BehaviorHeuristics.OfficeSpawnsProcess, d.Rule);
        Assert.EndsWith("calc.exe", d.Target);
    }

    [Fact]
    public async Task DefaultReplay_IsNoThreats()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql);
        var id = await RunToProcessingAsync(h, Unique());

        await h.Get<ProcessingPipeline>().ProcessAsync(id, Ct);

        await using var db = h.NewContext();
        var task = await db.Tasks.SingleAsync(t => t.Id == id, Ct);
        Assert.Equal(RunStatus.Completed, task.Status);
        Assert.Equal(Verdict.NoThreats, task.Verdict);
        Assert.Empty(task.ProcessingWarnings);
        Assert.Empty(await db.Detections.Where(x => x.TaskId == id).ToListAsync(Ct));

        // The final status (with the verdict) went out live, and the history records the transition.
        var last = h.Publisher.Statuses[^1];
        Assert.Equal((id, RunStatus.Completed, Verdict.NoThreats), (last.TaskId, last.Status, last.Verdict));
        var history = await db.TaskStatusHistory.Where(x => x.TaskId == id).OrderBy(x => x.Id).Select(x => x.To).ToListAsync(Ct);
        Assert.Equal(RunStatus.Completed, history[^1]);
    }

    [Fact]
    public async Task YaraBinaryMissing_CompletesWithWarning()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql, new Dictionary<string, string?> { ["Maldex:Yara:Binary"] = "/nonexistent" });
        var id = await RunToProcessingAsync(h, Unique());

        await h.Get<ProcessingPipeline>().ProcessAsync(id, Ct);

        await using var db = h.NewContext();
        var task = await db.Tasks.SingleAsync(t => t.Id == id, Ct);
        Assert.Equal(RunStatus.Completed, task.Status);
        Assert.StartsWith("yara failed:", Assert.Single(task.ProcessingWarnings));
    }

    [Fact]
    public async Task Processing_IsIdempotentOnRerun()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql, Replay("macro-calc.jsonl"));
        var id = await RunToProcessingAsync(h, Unique());
        var pipeline = h.Get<ProcessingPipeline>();

        await pipeline.ProcessAsync(id, Ct);
        int first;
        await using (var db = h.NewContext()) first = await db.Detections.CountAsync(x => x.TaskId == id, Ct);
        Assert.Equal(1, first);

        // A restart mid-processing leaves the task in Processing; simulate that and run again.
        await using (var db = h.NewContext())
        {
            var t = await db.Tasks.SingleAsync(x => x.Id == id, Ct);
            t.Status = RunStatus.Processing;
            t.ProcessingWarnings.Add("stale warning");
            await db.SaveChangesAsync(Ct);
        }
        await pipeline.ProcessAsync(id, Ct);

        await using var check = h.NewContext();
        Assert.Equal(first, await check.Detections.CountAsync(x => x.TaskId == id, Ct));
        var task = await check.Tasks.SingleAsync(t => t.Id == id, Ct);
        Assert.Equal(RunStatus.Completed, task.Status);
        Assert.Equal(Verdict.Suspicious, task.Verdict);
        Assert.Empty(task.ProcessingWarnings);
    }

    [Fact]
    public async Task UrlTask_SkipsYara_AndCompletes()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql, new Dictionary<string, string?> { ["Maldex:Yara:Binary"] = "/nonexistent" });
        var id = await h.SeedTaskAsync(RunStatus.Processing);

        await h.Get<ProcessingPipeline>().ProcessAsync(id, Ct);

        await using var db = h.NewContext();
        var task = await db.Tasks.SingleAsync(t => t.Id == id, Ct);
        Assert.Equal(RunStatus.Completed, task.Status);
        Assert.Equal(Verdict.NoThreats, task.Verdict);
        Assert.Empty(task.ProcessingWarnings); // no sample, so the scanner (missing here) was never invoked
    }

    [Fact]
    public void LongGuestStrings_FitTheColumns()
    {
        // A heuristic target taken from the guest can be huge; the tags column is only 1024 chars.
        var longTags = new DetectionResult(DetectionSource.Yara, new string('r', 400), 2, Enumerable.Range(0, 400).Select(i => "tag" + i).ToArray(), "t", null);
        var detection = ProcessingPipeline.ToEntity(Guid.NewGuid(), longTags);
        Assert.True(detection.Tags.Length <= 1024);
        Assert.True(detection.Rule.Length <= 256);
        Assert.StartsWith("tag0,tag1,", detection.Tags);
        Assert.DoesNotContain(",,", detection.Tags);
    }

    [Fact]
    public async Task Worker_ProcessesPendingTasks_AndLeavesNoScanCopyBehind()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql);
        var first = await RunToProcessingAsync(h, Unique());
        var second = await RunToProcessingAsync(h, Unique());
        var before = Directory.GetFiles(Path.GetTempPath(), "maldex-scan-*").Length;

        var done = await h.Get<ProcessingWorker>().ProcessPendingAsync(Ct);

        Assert.Equal([first, second], done); // oldest first
        Assert.Equal(RunStatus.Completed, await h.StatusOfAsync(first));
        Assert.Equal(RunStatus.Completed, await h.StatusOfAsync(second));
        Assert.Equal(before, Directory.GetFiles(Path.GetTempPath(), "maldex-scan-*").Length);
        Assert.Empty(await h.Get<ProcessingWorker>().ProcessPendingAsync(Ct));
    }

    private static bool YaraBinaryAvailable(string binary)
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(binary, "--version")
            { RedirectStandardOutput = true, RedirectStandardError = true });
            p!.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }
}
