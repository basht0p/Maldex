using System.Net;
using System.Security.Cryptography;
using Maldex.Core.Domain;
using Maldex.Core.Media;
using Maldex.Data;
using Maldex.Infrastructure.Fakes;
using Maldex.Infrastructure.Media;
using Maldex.Integration.Tests.Fixtures;
using Maldex.Integration.Tests.Orchestrator;
using Maldex.Orchestrator.Media;
using Maldex.Orchestrator.Templates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Maldex.Integration.Tests.Media;

[Collection("sql")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class MediaIntakeTests : IAsyncLifetime
{
    private const long MiB = 1024 * 1024;

    private readonly SqlServerFixture _sql;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "maldex-media-" + Guid.NewGuid().ToString("N"));
    private readonly string _connection;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 25, 9, 0, 0, TimeSpan.Zero));
    private readonly SettableDiskSpace _disk = new() { Free = 500_000 };
    private readonly FakeMediaInspector _inspector = new();
    private readonly MediaOptions _options;
    private readonly List<MaldexDbContext> _contexts = [];
    private readonly List<WebApplication> _servers = [];
    private MediaUploadState _state = new();
    private Guid _userId;

    public MediaIntakeTests(SqlServerFixture sql)
    {
        _sql = sql;
        _connection = sql.CreateDatabaseConnectionString();
        _options = new MediaOptions { Root = _root, MaxBytes = 10 * MiB, AllowPrivateFetch = true };
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using var db = _sql.CreateContext(_connection);
        await db.Database.MigrateAsync(Ct);
        var user = new AppUser { Id = Guid.NewGuid(), EntraObjectId = Guid.NewGuid().ToString("N"), DisplayName = "Admin", Role = UserRole.Admin };
        db.Add(user);
        await db.SaveChangesAsync(Ct);
        _userId = user.Id;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _servers) await s.DisposeAsync();
        foreach (var c in _contexts) await c.DisposeAsync();
        _state.Dispose();
        if (Directory.Exists(_root))
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetUnixFileMode(f, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Directory.Delete(_root, true);
        }
    }

    private MaldexDbContext NewDb()
    {
        var db = _sql.CreateContext(_connection);
        _contexts.Add(db);
        return db;
    }

    private MediaStore NewStore() => new(Options.Create(_options), _disk);

    private SafeHttpFetcher NewFetcher() => new(Options.Create(_options), _time, NullLogger<SafeHttpFetcher>.Instance);

    private MediaIntake NewIntake(MaldexDbContext? db = null, MediaUploadState? state = null, Maldex.Core.Abstractions.IMediaInspector? inspector = null) =>
        new(db ?? NewDb(), NewStore(), inspector ?? _inspector, NewFetcher(), Options.Create(_options), Options.Create(new TemplateJobOptions { MinFreeGiB = 30 }), _time, state ?? _state);

    private static byte[] Bytes(int length) => RandomNumberGenerator.GetBytes(length);

    private static string Hex(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private async Task<MediaFile> StartAsync(MediaIntake intake, byte[] data, string name = "win11.iso", string? sha = null, MediaKind kind = MediaKind.OsInstaller) =>
        await intake.StartUploadAsync(name, data.Length, kind, sha, _userId, Ct);

    private static Task<(long ReceivedBytes, MediaStatus Status)> Send(MediaIntake intake, Guid id, byte[] data, int offset, int count) =>
        intake.AppendChunkAsync(id, offset, new MemoryStream(data, offset, count), Ct);

    private async Task<MediaFile> RowAsync(Guid id)
    {
        await using var db = _sql.CreateContext(_connection);
        return await db.MediaFiles.AsNoTracking().SingleAsync(m => m.Id == id, Ct);
    }

    private async Task<List<string>> AuditAsync(Guid id)
    {
        await using var db = _sql.CreateContext(_connection);
        return await db.AuditLogs.AsNoTracking().Where(a => a.Target == id.ToString()).OrderBy(a => a.Id).Select(a => a.Action).ToListAsync(Ct);
    }

    private string[] FinalFiles() => Directory.Exists(_root) ? Directory.GetFiles(_root, "*.iso") : [];

    private string[] StagingFiles() => Directory.Exists(Path.Combine(_root, "staging")) ? Directory.GetFiles(Path.Combine(_root, "staging")) : [];

    /// <summary>Uploads a whole file in three chunks and runs the worker's completion step.</summary>
    private async Task<MediaFile> UploadReadyAsync(byte[] data, string name = "win11.iso")
    {
        var intake = NewIntake();
        var media = await StartAsync(intake, data, name);
        var third = data.Length / 3;
        await Send(intake, media.Id, data, 0, third);
        await Send(intake, media.Id, data, third, third);
        await Send(intake, media.Id, data, 2 * third, data.Length - 2 * third);
        await NewIntake().CompleteAsync(media.Id, Ct);
        return await RowAsync(media.Id);
    }

    [Fact]
    public async Task Upload_InThreeChunks_BecomesReady_WithShaAndContents()
    {
        var data = Bytes(3_000_001);
        var intake = NewIntake();
        var media = await StartAsync(intake, data, sha: Hex(data));
        Assert.Equal(MediaStatus.Receiving, media.Status);
        Assert.Equal(MediaSource.Upload, media.Source);

        var third = data.Length / 3;
        Assert.Equal((third, MediaStatus.Receiving), await Send(intake, media.Id, data, 0, third));
        Assert.Equal((2L * third, MediaStatus.Receiving), await Send(intake, media.Id, data, third, third));
        Assert.Empty(state_QueueSnapshot());
        Assert.Equal((data.Length, MediaStatus.Receiving), await Send(intake, media.Id, data, 2 * third, data.Length - 2 * third));
        var queued = Assert.Single(state_QueueSnapshot());
        Assert.Equal(media.Id, queued.Id);

        await NewIntake().CompleteAsync(media.Id, Ct);

        var row = await RowAsync(media.Id);
        Assert.Equal(MediaStatus.Ready, row.Status);
        Assert.Equal(Hex(data), row.Sha256);
        Assert.Equal(data.Length, row.ReceivedBytes);
        var contents = MediaContentsJson.Parse(row.Contents);
        Assert.NotNull(contents);
        Assert.Equal(8, contents.Images.Count);
        var final = Assert.Single(FinalFiles());
        Assert.Equal(Hex(data) + ".iso", Path.GetFileName(final));
        Assert.Equal(data, await File.ReadAllBytesAsync(final, Ct));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead, File.GetUnixFileMode(final));
        Assert.Empty(StagingFiles());
        Assert.Equal(["media.upload.start", "media.ready"], await AuditAsync(media.Id));
    }

    // The queue is drained into a list so a test can look at what was queued.
    private List<MediaWork> state_QueueSnapshot()
    {
        var items = new List<MediaWork>();
        while (_state.Queue.Reader.TryRead(out var work)) items.Add(work);
        foreach (var w in items) _state.Queue.Writer.TryWrite(w);
        return items;
    }

    [Fact]
    public async Task Chunk_WrongOffset_409_WithReceivedBytes()
    {
        var data = Bytes(1000);
        var intake = NewIntake();
        var media = await StartAsync(intake, data);
        await Send(intake, media.Id, data, 0, 400);

        var ex = await Assert.ThrowsAsync<MediaOffsetException>(() => Send(intake, media.Id, data, 500, 100));
        Assert.Equal(400, ex.ReceivedBytes);
        Assert.Equal(400, (await RowAsync(media.Id)).ReceivedBytes);
        Assert.Equal(400, new FileInfo(Assert.Single(StagingFiles())).Length);
    }

    [Fact]
    public async Task Upload_ResumeAfterRestart_HashMatchesWholeFile()
    {
        var data = Bytes(2_500_000);
        var intake = NewIntake();
        var media = await StartAsync(intake, data);
        await Send(intake, media.Id, data, 0, 1_000_000);

        // A restart: a new MediaUploadState has no running hash and must rebuild it from the staging file.
        _state.Dispose();
        _state = new MediaUploadState();
        var resumed = NewIntake();
        Assert.Equal((2_500_000L, MediaStatus.Receiving), await Send(resumed, media.Id, data, 1_000_000, 1_500_000));
        await NewIntake().CompleteAsync(media.Id, Ct);

        var row = await RowAsync(media.Id);
        Assert.Equal(MediaStatus.Ready, row.Status);
        Assert.Equal(Hex(data), row.Sha256);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Chunk_PartialWriteIsDiscardedOnResume(bool restart)
    {
        var data = Bytes(1_200_000);
        var intake = NewIntake();
        var media = await StartAsync(intake, data);
        await Send(intake, media.Id, data, 0, 500_000);

        // A chunk that died half way: bytes on disk beyond what the row says was received.
        await File.AppendAllBytesAsync(Assert.Single(StagingFiles()), Bytes(123_456), Ct);
        if (restart)
        {
            _state.Dispose();
            _state = new MediaUploadState();
        }
        var resumed = NewIntake();
        await Send(resumed, media.Id, data, 500_000, 700_000);
        await NewIntake().CompleteAsync(media.Id, Ct);

        var row = await RowAsync(media.Id);
        Assert.Equal(MediaStatus.Ready, row.Status);
        Assert.Equal(Hex(data), row.Sha256);
        Assert.Equal(data, await File.ReadAllBytesAsync(Assert.Single(FinalFiles()), Ct));
    }

    [Fact]
    public async Task Chunk_PastDeclaredSize_400()
    {
        var data = Bytes(1000);
        var intake = NewIntake();
        var media = await StartAsync(intake, data);
        await Send(intake, media.Id, data, 0, 600);

        var ex = await Assert.ThrowsAsync<MediaRuleException>(() => intake.AppendChunkAsync(media.Id, 600, new MemoryStream(Bytes(500)), Ct));
        Assert.Equal("the chunk goes past the declared size of the file", ex.Message);
        Assert.Equal(600, (await RowAsync(media.Id)).ReceivedBytes);

        // The bytes written before the check are discarded: the upload still finishes with the right hash.
        await Send(intake, media.Id, data, 600, 400);
        await NewIntake().CompleteAsync(media.Id, Ct);
        Assert.Equal(Hex(data), (await RowAsync(media.Id)).Sha256);
    }

    [Fact]
    public async Task Chunk_LargerThanMaxChunk_400()
    {
        _options.MaxChunkBytes = 100;
        var data = Bytes(1000);
        var intake = NewIntake();
        var media = await StartAsync(intake, data);
        var ex = await Assert.ThrowsAsync<MediaRuleException>(() => Send(intake, media.Id, data, 0, 101));
        Assert.Equal("the chunk is larger than the 100 byte limit", ex.Message);
        Assert.Equal((100L, MediaStatus.Receiving), await Send(intake, media.Id, data, 0, 100));
    }

    [Fact]
    public async Task Upload_TooBig_400()
    {
        var ex = await Assert.ThrowsAsync<MediaRuleException>(() => NewIntake().StartUploadAsync("a.iso", _options.MaxBytes + 1, MediaKind.OsInstaller, null, _userId, Ct));
        Assert.Equal("the file is larger than the 0.01 GiB limit", ex.Message);
        await Assert.ThrowsAsync<MediaRuleException>(() => NewIntake().StartUploadAsync("a.iso", 0, MediaKind.OsInstaller, null, _userId, Ct));
        Assert.Empty(await NewDb().MediaFiles.ToListAsync(Ct));
    }

    [Theory]
    [InlineData("..\\..\\evil\\setup.iso", "setup.iso")]
    [InlineData("/etc/passwd.iso", "passwd.iso")]
    [InlineData("  a\tb\u0001.iso ", "ab.iso")]
    public async Task Upload_FileName_IsCleaned(string given, string expected)
    {
        var media = await NewIntake().StartUploadAsync(given, 10, MediaKind.OsInstaller, null, _userId, Ct);
        Assert.Equal(expected, media.FileName);
        await Assert.ThrowsAsync<MediaRuleException>(() => NewIntake().StartUploadAsync(" / ", 10, MediaKind.OsInstaller, null, _userId, Ct));
        await Assert.ThrowsAsync<MediaRuleException>(() => NewIntake().StartUploadAsync("a.iso", 10, MediaKind.OsInstaller, "xyz", _userId, Ct));
    }

    [Fact]
    public async Task Upload_NoDiskSpace_409()
    {
        // 30 GiB headroom plus 5 MiB: free is 1 MiB short.
        _disk.Free = 30 * 1024 + 5 - 1;
        var ex = await Assert.ThrowsAsync<MediaRefusedException>(() => NewIntake().StartUploadAsync("a.iso", 5 * MiB, MediaKind.OsInstaller, null, _userId, Ct));
        Assert.Equal("not enough disk space for this file: 30 GiB free, 30 GiB required", ex.Message);
        Assert.Empty(await NewDb().MediaFiles.ToListAsync(Ct));

        _disk.Free = 30 * 1024 + 5;
        await NewIntake().StartUploadAsync("a.iso", 5 * MiB, MediaKind.OsInstaller, null, _userId, Ct);
    }

    [Fact]
    public async Task Upload_CountsTheBytesOtherUploadsStillExpect()
    {
        _disk.Free = 30 * 1024 + 8;
        var first = await NewIntake().StartUploadAsync("a.iso", 5 * MiB, MediaKind.OsInstaller, null, _userId, Ct);
        await Assert.ThrowsAsync<MediaRefusedException>(() => NewIntake().StartUploadAsync("b.iso", 5 * MiB, MediaKind.OsInstaller, null, _userId, Ct));

        // Bytes already received are on disk (and so no longer free space to count twice).
        var data = Bytes(3 * (int)MiB);
        await NewIntake().AppendChunkAsync(first.Id, 0, new MemoryStream(data), Ct);
        _disk.Free = 30 * 1024 + 5 + 2; // the new file's 5 MiB, plus the 2 MiB the first upload still expects
        await NewIntake().StartUploadAsync("b.iso", 5 * MiB, MediaKind.OsInstaller, null, _userId, Ct);
    }

    [Fact]
    public async Task ShaMismatch_FailsTheUpload()
    {
        var data = Bytes(5000);
        var intake = NewIntake();
        var media = await StartAsync(intake, data, sha: Hex(Bytes(5000)));
        await Send(intake, media.Id, data, 0, 5000);
        await NewIntake().CompleteAsync(media.Id, Ct);

        var row = await RowAsync(media.Id);
        Assert.Equal(MediaStatus.Failed, row.Status);
        Assert.Equal("the received file does not match the SHA-256 given at upload start", row.FailureReason);
        Assert.Empty(FinalFiles());
        Assert.Empty(StagingFiles());
        Assert.Equal(["media.upload.start", "media.failed"], await AuditAsync(media.Id));
    }

    [Fact]
    public async Task Duplicate_FailsAndLeavesOneFileOnDisk()
    {
        var data = Bytes(6000);
        var first = await UploadReadyAsync(data, "first.iso");
        Assert.Equal(MediaStatus.Ready, first.Status);

        var intake = NewIntake();
        var again = await StartAsync(intake, data, "second.iso");
        await Send(intake, again.Id, data, 0, 6000);
        await NewIntake().CompleteAsync(again.Id, Ct);

        var row = await RowAsync(again.Id);
        Assert.Equal(MediaStatus.Failed, row.Status);
        Assert.Equal("already in the library as first.iso", row.FailureReason);
        Assert.Single(FinalFiles());
        Assert.Empty(StagingFiles());
        Assert.Equal(MediaStatus.Ready, (await RowAsync(first.Id)).Status);
    }

    [Fact]
    public async Task InspectorError_FailsAndLeavesNoFinalFile()
    {
        _inspector.Error = "this is not a Windows installation image";
        var data = Bytes(4000);
        var intake = NewIntake();
        var media = await StartAsync(intake, data);
        await Send(intake, media.Id, data, 0, 4000);
        await NewIntake().CompleteAsync(media.Id, Ct);

        var row = await RowAsync(media.Id);
        Assert.Equal(MediaStatus.Failed, row.Status);
        Assert.Equal("this is not a Windows installation image", row.FailureReason);
        Assert.Empty(FinalFiles());
        Assert.Empty(StagingFiles());
        Assert.Equal(["media.upload.start", "media.failed"], await AuditAsync(media.Id));
    }

    private async Task<(string TemplateId, Guid JobId)> SeedBuildJobAsync(Guid mediaId, TemplateJobState state, string? mediaIds = null)
    {
        await using var db = _sql.CreateContext(_connection);
        var template = new Template { Id = "t" + Guid.NewGuid().ToString("N")[..8], DisplayName = "Win 11 Lab", CreatedAt = _time.GetUtcNow() };
        var job = new TemplateJob
        {
            Id = Guid.NewGuid(), Kind = TemplateJobKind.Build, TemplateId = template.Id, MediaIds = mediaIds ?? $"[\"{mediaId}\"]", State = state,
            RequestedByUserId = _userId, CreatedAt = _time.GetUtcNow(),
        };
        db.Add(template);
        db.Add(job);
        await db.SaveChangesAsync(Ct);
        return (template.Id, job.Id);
    }

    [Theory]
    [MemberData(nameof(ActiveStates))]
    public async Task Delete_UsedByActiveBuild_409(TemplateJobState state)
    {
        var media = await UploadReadyAsync(Bytes(5000));
        var (_, jobId) = await SeedBuildJobAsync(media.Id, state);

        var ex = await Assert.ThrowsAsync<MediaInUseException>(() => NewIntake().DeleteAsync(media.Id, _userId, Ct));
        Assert.Equal($"used by the build of Win 11 Lab (job {jobId})", ex.Message);
        Assert.Equal(MediaStatus.Ready, (await RowAsync(media.Id)).Status);
        Assert.Single(FinalFiles());
    }

    public static TheoryData<TemplateJobState> ActiveStates() => [.. Maldex.Core.Lifecycle.TemplateJobStateMachine.ActiveStates];

    [Fact]
    public async Task Delete_UsedOnlyByFinishedBuild_Succeeds()
    {
        var media = await UploadReadyAsync(Bytes(5000));
        await SeedBuildJobAsync(media.Id, TemplateJobState.Published);
        await NewIntake().DeleteAsync(media.Id, _userId, Ct);
        Assert.Equal(MediaStatus.Deleted, (await RowAsync(media.Id)).Status);
    }

    [Fact]
    public async Task Delete_WaitsForTheLockAThatBuildQueueHolds()
    {
        var media = await UploadReadyAsync(Bytes(5000));
        await using var other = _sql.CreateContext(_connection);
        await using var tx = await other.Database.BeginTransactionAsync(Ct);
        var locked = await MediaLocks.LockAsync(other, [media.Id], Ct);
        Assert.Single(locked);

        var delete = NewIntake().DeleteAsync(media.Id, _userId, Ct);
        await Task.Delay(500, Ct);
        Assert.False(delete.IsCompleted, "the delete must wait for the lock");

        await tx.CommitAsync(Ct);
        await delete.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Equal(MediaStatus.Deleted, (await RowAsync(media.Id)).Status);
    }

    [Fact]
    public async Task Delete_ReadyFile_MarksItDeleted_AndRemovesTheFile()
    {
        var media = await UploadReadyAsync(Bytes(5000));
        Assert.Single(FinalFiles());

        await NewIntake().DeleteAsync(media.Id, _userId, Ct);

        var row = await RowAsync(media.Id);
        Assert.Equal(MediaStatus.Deleted, row.Status);
        Assert.Equal(media.Sha256, row.Sha256);
        Assert.Equal("win11.iso", row.FileName);
        Assert.Empty(FinalFiles());
        Assert.Equal(["media.upload.start", "media.ready", "media.delete"], await AuditAsync(media.Id));
        await Assert.ThrowsAsync<MediaRefusedException>(() => NewIntake().DeleteAsync(media.Id, _userId, Ct));
    }

    [Fact]
    public async Task Delete_ReceivingUpload_RemovesStaging_AndStopsFurtherChunks()
    {
        var data = Bytes(1000);
        var intake = NewIntake();
        var media = await StartAsync(intake, data);
        await Send(intake, media.Id, data, 0, 300);

        await NewIntake().DeleteAsync(media.Id, _userId, Ct);

        Assert.Equal(MediaStatus.Deleted, (await RowAsync(media.Id)).Status);
        Assert.Empty(StagingFiles());
        await Assert.ThrowsAsync<MediaRefusedException>(() => Send(NewIntake(), media.Id, data, 300, 100));
    }

    [Fact]
    public async Task Abandoned_AfterTwentyFourHours_Fails()
    {
        var data = Bytes(1000);
        var intake = NewIntake();
        var media = await StartAsync(intake, data);
        await Send(intake, media.Id, data, 0, 300);
        var fresh = await StartAsync(intake, Bytes(1000), "fresh.iso");

        _time.Advance(TimeSpan.FromHours(24));
        await NewIntake().SweepAsync(Ct);
        Assert.Equal(MediaStatus.Receiving, (await RowAsync(media.Id)).Status);

        _time.Advance(TimeSpan.FromSeconds(1));
        await Send(NewIntake(), fresh.Id, data, 0, 100); // activity keeps an upload alive
        await NewIntake().SweepAsync(Ct);

        var row = await RowAsync(media.Id);
        Assert.Equal(MediaStatus.Failed, row.Status);
        Assert.Equal("upload abandoned (no data for 24 h)", row.FailureReason);
        Assert.Equal(MediaStatus.Receiving, (await RowAsync(fresh.Id)).Status);
        Assert.Single(StagingFiles());
        Assert.Equal(["media.upload.start", "media.abandon"], await AuditAsync(media.Id));
        await using var db = _sql.CreateContext(_connection);
        Assert.Null((await db.AuditLogs.SingleAsync(a => a.Action == "media.abandon", Ct)).UserId);
    }

    [Fact]
    public async Task Sweep_DeletesStagingFilesWithoutAReceivingRow()
    {
        var store = NewStore();
        var orphan = Guid.NewGuid();
        store.OpenStagingForAppend(orphan, 0).Dispose();
        var failed = await StartAsync(NewIntake(), Bytes(10));
        store.OpenStagingForAppend(failed.Id, 0).Dispose();
        await NewIntake().DeleteAsync(failed.Id, _userId, Ct);
        store.OpenStagingForAppend(failed.Id, 0).Dispose();
        var live = await StartAsync(NewIntake(), Bytes(10));
        store.OpenStagingForAppend(live.Id, 0).Dispose();
        Assert.Equal(3, StagingFiles().Length);

        await NewIntake().SweepAsync(Ct);

        Assert.Equal(live.Id.ToString("N") + ".part", Path.GetFileName(Assert.Single(StagingFiles())));
    }

    [Fact]
    public async Task Recover_FailsInterruptedFetches_AndRequeuesCompleteUploads()
    {
        var data = Bytes(2000);
        var intake = NewIntake();
        var done = await StartAsync(intake, data);
        await Send(intake, done.Id, data, 0, 2000);
        var partial = await StartAsync(intake, data, "partial.iso");
        await Send(intake, partial.Id, data, 0, 500);
        var fetch = await NewIntake().StartFetchAsync(new Uri("http://example.test/win.iso?token=secret"), MediaKind.OsInstaller, null, _userId, Ct);
        _state.Dispose();
        _state = new MediaUploadState(); // a restart

        await NewIntake().RecoverAsync(Ct);

        var fetched = await RowAsync(fetch.Id);
        Assert.Equal(MediaStatus.Failed, fetched.Status);
        Assert.Equal("interrupted", fetched.FailureReason);
        Assert.Equal(MediaStatus.Receiving, (await RowAsync(partial.Id)).Status);
        var queued = Assert.Single(state_QueueSnapshot());
        Assert.Equal(done.Id, queued.Id);
        Assert.Contains("media.failed", await AuditAsync(fetch.Id));
    }

    private async Task<int> ServeAsync(Action<WebApplication> map)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        map(app);
        await app.StartAsync(Ct);
        _servers.Add(app);
        return new Uri(app.Urls.First()).Port;
    }

    [Fact]
    public async Task Fetch_EndToEnd_AgainstALoopbackServer()
    {
        var data = Bytes(700_000);
        var port = await ServeAsync(app => app.MapGet("/dl/Setup.iso", () => Results.Bytes(data, "application/octet-stream")));
        var url = new Uri($"http://127.0.0.1:{port}/dl/Setup.iso?token=hunter2");

        var started = await NewIntake().StartFetchAsync(url, MediaKind.OsInstaller, null, _userId, Ct);
        Assert.Equal(MediaStatus.Receiving, started.Status);
        Assert.Equal(MediaSource.Url, started.Source);
        Assert.DoesNotContain("hunter2", started.SourceUrl);
        var work = Assert.Single(state_QueueSnapshot());
        Assert.Equal(url, work.FetchUrl);

        await NewIntake().RunFetchAsync(started.Id, url, false, Ct);

        var row = await RowAsync(started.Id);
        Assert.Equal(MediaStatus.Ready, row.Status);
        Assert.Equal(Hex(data), row.Sha256);
        Assert.Equal(data.Length, row.Size);
        Assert.Equal(data.Length, row.ReceivedBytes);
        Assert.Equal("Setup.iso", row.FileName);
        Assert.Equal(data, await File.ReadAllBytesAsync(Assert.Single(FinalFiles()), Ct));
        Assert.Empty(StagingFiles());
        var actions = await AuditAsync(started.Id);
        Assert.Equal(["media.fetch.start", "media.ready"], actions);
        await using var db = _sql.CreateContext(_connection);
        var audit = await db.AuditLogs.SingleAsync(a => a.Action == "media.fetch.start", Ct);
        Assert.DoesNotContain("hunter2", audit.Details);
        Assert.Contains($"127.0.0.1:{port}/dl/Setup.iso", audit.Details);
    }

    [Fact]
    public async Task Fetch_GivenFileName_IsKept()
    {
        var port = await ServeAsync(app => app.MapGet("/x", () => Results.Bytes(Bytes(900))));
        var url = new Uri($"http://127.0.0.1:{port}/x");
        var started = await NewIntake().StartFetchAsync(url, MediaKind.Application, "office.iso", _userId, Ct);
        await NewIntake().RunFetchAsync(started.Id, url, true, Ct);
        var row = await RowAsync(started.Id);
        Assert.Equal((MediaStatus.Ready, "office.iso", MediaKind.Application), (row.Status, row.FileName, row.Kind));
    }

    [Fact]
    public async Task Fetch_Failure_FailsTheRow_AndLeavesNoStaging()
    {
        var port = await ServeAsync(app => app.MapGet("/gone", () => Results.NotFound()));
        var url = new Uri($"http://127.0.0.1:{port}/gone");
        var started = await NewIntake().StartFetchAsync(url, MediaKind.OsInstaller, null, _userId, Ct);
        await NewIntake().RunFetchAsync(started.Id, url, false, Ct);

        var row = await RowAsync(started.Id);
        Assert.Equal(MediaStatus.Failed, row.Status);
        Assert.False(string.IsNullOrWhiteSpace(row.FailureReason));
        Assert.Empty(StagingFiles());
        Assert.Empty(FinalFiles());
        Assert.Equal(["media.fetch.start", "media.failed"], await AuditAsync(started.Id));
    }

    [Fact]
    public async Task Fetch_Delete_CancelsARunningFetch()
    {
        var started = new TaskCompletionSource();
        var port = await ServeAsync(app => app.MapGet("/slow", async (HttpContext c) =>
        {
            c.Response.ContentLength = 10_000_000;
            await c.Response.Body.WriteAsync(new byte[1000], c.RequestAborted);
            await c.Response.Body.FlushAsync(c.RequestAborted);
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, c.RequestAborted);
        }));
        var url = new Uri($"http://127.0.0.1:{port}/slow");
        var media = await NewIntake().StartFetchAsync(url, MediaKind.OsInstaller, null, _userId, Ct);
        var run = NewIntake().RunFetchAsync(media.Id, url, false, Ct);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        await NewIntake().DeleteAsync(media.Id, _userId, Ct);
        await run.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Equal(MediaStatus.Deleted, (await RowAsync(media.Id)).Status);
        Assert.Empty(StagingFiles());
    }

    [Theory]
    [InlineData("ftp://example.test/a.iso")]
    [InlineData("http://user:pw@example.test/a.iso")]
    public async Task Fetch_RefusedUrl_400(string url)
    {
        await Assert.ThrowsAsync<MediaRuleException>(() => NewIntake().StartFetchAsync(new Uri(url), MediaKind.OsInstaller, null, _userId, Ct));
        Assert.Empty(await NewDb().MediaFiles.ToListAsync(Ct));
    }

    [Fact]
    public async Task Worker_CompletesQueuedUploads_AndRunsFetches()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(_time);
        services.AddSingleton(_state);
        services.AddDbContext<MaldexDbContext>(o => o.UseSqlServer(_connection));
        services.AddSingleton<IOptions<MediaOptions>>(Options.Create(_options));
        services.AddSingleton<IOptions<TemplateJobOptions>>(Options.Create(new TemplateJobOptions()));
        services.AddSingleton<Maldex.Core.Abstractions.IDiskSpaceProbe>(_disk);
        services.AddSingleton<Maldex.Core.Abstractions.IMediaInspector>(_inspector);
        services.AddSingleton<MediaStore>();
        services.AddSingleton(NewFetcher());
        services.AddScoped<MediaIntake>();
        services.AddLogging();
        services.AddSingleton<MediaWorker>();
        await using var provider = services.BuildServiceProvider();
        var worker = provider.GetRequiredService<MediaWorker>();
        await worker.StartAsync(Ct);
        try
        {
            var data = Bytes(3000);
            var intake = NewIntake();
            var media = await StartAsync(intake, data);
            await Send(intake, media.Id, data, 0, 3000);

            var row = await WaitForAsync(media.Id, MediaStatus.Ready);
            Assert.Equal(Hex(data), row.Sha256);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Fetch_NotEnoughDisk_409_CountingUploadsStillExpected()
    {
        var url = new Uri("http://example.test/win.iso");
        _disk.Free = 30 * 1024 - 1;
        var ex = await Assert.ThrowsAsync<MediaRefusedException>(() => NewIntake().StartFetchAsync(url, MediaKind.OsInstaller, null, _userId, Ct));
        Assert.Equal("not enough disk space for this file: 30 GiB free, 30 GiB required", ex.Message);
        Assert.Empty(await NewDb().MediaFiles.ToListAsync(Ct));

        _disk.Free = 30 * 1024 + 5;
        await NewIntake().StartUploadAsync("a.iso", 5 * MiB, MediaKind.OsInstaller, null, _userId, Ct);
        _disk.Free = 30 * 1024 + 4; // the upload still expects 5 MiB
        await Assert.ThrowsAsync<MediaRefusedException>(() => NewIntake().StartFetchAsync(url, MediaKind.OsInstaller, null, _userId, Ct));
        _disk.Free = 30 * 1024 + 5;
        await NewIntake().StartFetchAsync(url, MediaKind.OsInstaller, null, _userId, Ct);
    }

    [Fact]
    public async Task Fetch_StopsWhenFreeSpaceDropsBelowTheReserve()
    {
        var firstSent = new TaskCompletionSource();
        var go = new TaskCompletionSource();
        var port = await ServeAsync(app => app.MapGet("/big", async (HttpContext c) =>
        {
            await c.Response.Body.WriteAsync(new byte[100_000], c.RequestAborted);
            await c.Response.Body.FlushAsync(c.RequestAborted);
            firstSent.TrySetResult();
            await go.Task;
            for (var i = 0; i < 50; i++)
            {
                await c.Response.Body.WriteAsync(new byte[100_000], c.RequestAborted);
                await c.Response.Body.FlushAsync(c.RequestAborted);
            }
        }));
        var url = new Uri($"http://127.0.0.1:{port}/big");
        var media = await NewIntake().StartFetchAsync(url, MediaKind.OsInstaller, null, _userId, Ct);
        var run = NewIntake().RunFetchAsync(media.Id, url, false, Ct);
        await firstSent.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        _disk.Free = 30 * 1024 - 1;
        go.SetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        var row = await RowAsync(media.Id);
        Assert.Equal(MediaStatus.Failed, row.Status);
        Assert.StartsWith("not enough disk space: the fetch was stopped at ", row.FailureReason);
        Assert.Empty(StagingFiles());
        Assert.Empty(FinalFiles());
        Assert.Contains("media.failed", await AuditAsync(media.Id));
    }

    private sealed class ShutdownInspector(CancellationTokenSource cts) : Maldex.Core.Abstractions.IMediaInspector
    {
        public Task<Maldex.Core.Abstractions.MediaInspection> InspectAsync(string isoPath, MediaKind kind, CancellationToken ct)
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("unreachable");
        }
    }

    [Fact]
    public async Task ShutdownDuringInspection_IsResumedFromTheFinalFile()
    {
        var data = Bytes(8000);
        var intake = NewIntake();
        var media = await StartAsync(intake, data);
        await Send(intake, media.Id, data, 0, 8000);

        using var shutdown = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NewIntake(inspector: new ShutdownInspector(shutdown)).CompleteAsync(media.Id, shutdown.Token));
        var interrupted = await RowAsync(media.Id);
        Assert.Equal(MediaStatus.Receiving, interrupted.Status);
        Assert.Equal(Hex(data), interrupted.Sha256);
        Assert.Empty(StagingFiles());
        Assert.Single(FinalFiles());

        _state.Dispose();
        _state = new MediaUploadState();
        await NewIntake().RecoverAsync(Ct);
        Assert.Equal(media.Id, Assert.Single(state_QueueSnapshot()).Id);
        await NewIntake().CompleteAsync(media.Id, Ct);

        var row = await RowAsync(media.Id);
        Assert.Equal(MediaStatus.Ready, row.Status);
        Assert.Equal(Hex(data), row.Sha256);
        Assert.Single(FinalFiles());
    }

    [Fact]
    public async Task StagingGone_AndNoFinalFile_FailsTheUpload()
    {
        var data = Bytes(3000);
        var intake = NewIntake();
        var media = await StartAsync(intake, data);
        await Send(intake, media.Id, data, 0, 3000);
        File.Delete(Assert.Single(StagingFiles()));
        _state.Dispose();
        _state = new MediaUploadState();

        await NewIntake().CompleteAsync(media.Id, Ct);
        Assert.Equal(MediaStatus.Failed, (await RowAsync(media.Id)).Status);
        Assert.Empty(FinalFiles());
    }

    [Fact]
    public async Task Sweep_DeletesFinalFilesNoLiveRowHolds()
    {
        var kept = await UploadReadyAsync(Bytes(4000), "kept.iso");
        var gone = await UploadReadyAsync(Bytes(4001), "gone.iso");
        await NewIntake().DeleteAsync(gone.Id, _userId, Ct);
        var orphan = Path.Combine(_root, new string('b', 64) + ".iso");
        await File.WriteAllBytesAsync(orphan, Bytes(10), Ct);
        var strayName = Path.Combine(_root, "notes.iso");
        await File.WriteAllBytesAsync(strayName, Bytes(10), Ct);

        await NewIntake().SweepAsync(Ct);

        Assert.False(File.Exists(orphan));
        Assert.True(File.Exists(strayName), "files that are not <sha256>.iso are not ours to delete");
        Assert.Contains(FinalFiles(), f => Path.GetFileName(f) == kept.Sha256 + ".iso");
    }

    [Fact]
    public async Task Sweep_NeverAbandonsAFullyReceivedUpload()
    {
        var data = Bytes(2000);
        var intake = NewIntake();
        var media = await StartAsync(intake, data);
        await Send(intake, media.Id, data, 0, 2000);

        _time.Advance(TimeSpan.FromHours(48));
        await NewIntake().SweepAsync(Ct);

        Assert.Equal(MediaStatus.Receiving, (await RowAsync(media.Id)).Status);
        Assert.Single(StagingFiles());
    }

    [Fact]
    public async Task Delete_UnparsableMediaIds_CountsAsInUse()
    {
        var media = await UploadReadyAsync(Bytes(5000));
        await SeedBuildJobAsync(media.Id, TemplateJobState.Queued, "not json");
        await Assert.ThrowsAsync<MediaInUseException>(() => NewIntake().DeleteAsync(media.Id, _userId, Ct));
    }

    [Fact]
    public async Task Delete_OtherMediaInJob_DoesNotBlock_AndIdsMatchExactly()
    {
        var media = await UploadReadyAsync(Bytes(5000));
        await SeedBuildJobAsync(media.Id, TemplateJobState.Queued, $"[\"{Guid.NewGuid()}\"]");
        await NewIntake().DeleteAsync(media.Id, _userId, Ct);
        Assert.Equal(MediaStatus.Deleted, (await RowAsync(media.Id)).Status);
    }

    [Fact]
    public async Task UnknownId_IsNotFound_AndLeavesNoState()
    {
        var id = Guid.NewGuid();
        var ex = await Assert.ThrowsAsync<MediaNotFoundException>(() => Send(NewIntake(), id, Bytes(10), 0, 10));
        Assert.Equal(id, ex.Id);
        await Assert.ThrowsAsync<MediaNotFoundException>(() => NewIntake().DeleteAsync(id, _userId, Ct));
        await NewIntake().CompleteAsync(id, Ct);
        Assert.Equal(0, _state.TrackedUploads);
    }

    [Fact]
    public async Task EndedUploads_DropTheirStateEntry()
    {
        await UploadReadyAsync(Bytes(5000));
        Assert.Equal(0, _state.TrackedUploads);

        var data = Bytes(1000);
        var intake = NewIntake();
        var media = await StartAsync(intake, data);
        await Send(intake, media.Id, data, 0, 300);
        Assert.Equal(1, _state.TrackedUploads);
        await NewIntake().DeleteAsync(media.Id, _userId, Ct);
        Assert.Equal(0, _state.TrackedUploads);
    }

    private async Task<MediaFile> WaitForAsync(Guid id, MediaStatus status)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var row = await RowAsync(id);
            if (row.Status == status || DateTime.UtcNow > deadline) return row;
            await Task.Delay(50, Ct);
        }
    }
}
