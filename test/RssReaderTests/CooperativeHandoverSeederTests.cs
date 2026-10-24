using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RssApp.Config;
using RssReader.Server.Services;

namespace RssReaderTests;

/// <summary>
/// The new writer's side of the cooperative handover (BLOB-DURABILITY-SPEC.md 3.2 v2) with
/// the <c>litestream restore -f</c> process faked: the fake writes the database and the
/// <c>-txid</c> sidecar the way 0.5.17 does, and the test moves the txid along.
/// </summary>
[TestClass]
public class CooperativeHandoverSeederTests
{
    private string _dir = null!;
    private string _active = null!;
    private DurabilityConfig _config = null!;

    private sealed class FakeFollower : ILitestreamFollower
    {
        public string OutputPath { get; }
        public bool StartThrows { get; set; }
        public bool HasExited { get; set; }
        public int? ExitCode { get; set; }
        public int StopExitCode { get; set; } = 0;
        public bool Started { get; private set; }
        public bool Stopped { get; private set; }
        public bool WriteCorrupt { get; set; }

        public FakeFollower(string outputPath) => OutputPath = outputPath;

        public void Start()
        {
            if (StartThrows) throw new InvalidOperationException("no litestream");
            Started = true;
        }

        /// <summary>Simulates the initial restore completing at the given txid.</summary>
        public void Restored(long txid, string marker)
        {
            if (WriteCorrupt) File.WriteAllText(OutputPath, "not a database");
            else CreateDb(OutputPath, marker);
            SetTxid(txid);
        }

        public void SetTxid(long txid) => File.WriteAllText(OutputPath + "-txid", txid.ToString("x16") + "\n");

        public long? ReadTxid() => LitestreamFollower.ReadTxidFile(OutputPath + "-txid");

        public Task<int?> StopAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            Stopped = true;
            HasExited = true;
            ExitCode = StopExitCode;
            return Task.FromResult<int?>(StopExitCode);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CooperativeHandoverSeederTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _active = Path.Combine(_dir, "live", "storage.db");
        _config = new DurabilityConfig
        {
            LitestreamEnabled = true,
            BlobServiceUri = "https://x.blob.core.windows.net",
            RestoreFromBlob = true,
            RestoreTimeout = TimeSpan.FromSeconds(5),
            CatchUpTimeout = TimeSpan.FromSeconds(3),
            CatchUpQuietPeriod = TimeSpan.FromMilliseconds(300),
        };
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static void CreateDb(string path, string marker)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE TABLE IF NOT EXISTS t (v TEXT); DELETE FROM t; INSERT INTO t VALUES ('{marker}');";
        cmd.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
    }

    private static string ReadMarker(string path)
    {
        using var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT v FROM t LIMIT 1";
        return (string)cmd.ExecuteScalar()!;
    }

    private sealed class Harness
    {
        public InMemoryWriterHandoffChannel Channel { get; } = new();
        public FakeFollower Follower { get; }
        public TaskCompletionSource<bool> Lease { get; } = new();
        public int ReadyCalls { get; private set; }
        public DateTimeOffset? ReadyAt { get; private set; }
        public CooperativeHandoverSeeder Seeder { get; }

        public Harness(DurabilityConfig config, string active)
        {
            Follower = new FakeFollower(active + ".follow");
            Seeder = new CooperativeHandoverSeeder(
                config, active, Channel, () => Follower,
                async ct => await Lease.Task.WaitAsync(ct),
                () => { ReadyCalls++; ReadyAt ??= DateTimeOffset.UtcNow; },
                "rev-0096/replica-b", NullLogger.Instance)
            { PollInterval = TimeSpan.FromMilliseconds(20) };
        }

        /// <summary>Plays the old writer: waits for the request, acknowledges it, releases the lease.</summary>
        public async Task OldWriterAcknowledgesAsync(long finalTxid, Action? beforeRelease = null)
        {
            HandoffRequest? req = null;
            for (var i = 0; i < 200 && req == null; i++)
            {
                (req, _) = await Channel.ReadAsync(CancellationToken.None);
                if (req == null) await Task.Delay(10);
            }
            Assert.IsNotNull(req, "the new writer must write the request");
            await Channel.AcknowledgeAsync(new HandoffAck(req!.Id, "rev-0095/replica-a", finalTxid, DateTimeOffset.UtcNow), CancellationToken.None);
            beforeRelease?.Invoke();
            Lease.TrySetResult(true);
        }
    }

    [TestMethod]
    public async Task Run_RequestsHandoff_WaitsForAckedTxid_RenamesFollowedCopy()
    {
        CreateDb(_active, "old-live");
        File.WriteAllText(_active + "-wal", "stale");
        var h = new Harness(_config, _active);

        var run = h.Seeder.RunAsync(CancellationToken.None);
        await Task.Delay(100);
        Assert.AreEqual(0, h.ReadyCalls, "probe stays 503 until the initial restore is on disk");
        Assert.IsFalse(run.IsCompleted);

        h.Follower.Restored(10, "replica@10");
        await Task.Delay(100);
        Assert.AreEqual(1, h.ReadyCalls, "probe turns 200 once the initial restore is done");
        Assert.IsFalse(run.IsCompleted, "still waiting for the lease");

        // Old writer drains, syncs two more transactions, acknowledges at txid 12, releases.
        await h.OldWriterAcknowledgesAsync(12);
        await Task.Delay(150);
        Assert.IsFalse(run.IsCompleted, "follower at 10 must reach the acknowledged 12 first");
        h.Follower.SetTxid(11);
        await Task.Delay(100);
        Assert.IsFalse(run.IsCompleted);
        CreateDb(h.Follower.OutputPath, "replica@12");
        h.Follower.SetTxid(12);

        Assert.IsTrue(await run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsTrue(h.Follower.Stopped);
        Assert.AreEqual("replica@12", ReadMarker(_active));
        Assert.IsFalse(File.Exists(_active + "-wal"));
        Assert.IsFalse(File.Exists(h.Follower.OutputPath));
        Assert.IsFalse(File.Exists(h.Follower.OutputPath + "-txid"));
        Assert.AreEqual(1, h.Channel.Requests);
    }

    [TestMethod]
    public async Task Run_NoAck_OldImageReleasedOnSigterm_ProceedsOnceFollowerIsQuiet()
    {
        var h = new Harness(_config, _active);
        var run = h.Seeder.RunAsync(CancellationToken.None);
        h.Follower.Restored(10, "replica@10");
        await Task.Delay(100);
        h.Lease.TrySetResult(true); // released by SIGTERM path; nobody acknowledged

        Assert.IsTrue(await run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual("replica@10", ReadMarker(_active));
    }

    [TestMethod]
    public async Task Run_FollowerNeverReachesAckedTxid_ReturnsFalseForFullRestore()
    {
        var h = new Harness(_config, _active);
        var run = h.Seeder.RunAsync(CancellationToken.None);
        h.Follower.Restored(10, "replica@10");
        await h.OldWriterAcknowledgesAsync(99);

        Assert.IsFalse(await run.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.IsFalse(File.Exists(_active), "nothing renamed into place");
        Assert.AreEqual(1, h.ReadyCalls);
    }

    [TestMethod]
    public async Task Run_FollowerExitsBeforeRestore_ReturnsFalseAndOpensProbe()
    {
        var h = new Harness(_config, _active);
        var run = h.Seeder.RunAsync(CancellationToken.None);
        await Task.Delay(50);
        h.Follower.HasExited = true;
        h.Follower.ExitCode = 1;

        Assert.IsFalse(await run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(1, h.ReadyCalls, "the probe must not stay 503 when the follower is gone");
        Assert.AreEqual(1, h.Channel.Requests, "the request was still written so the old writer drains early");
    }

    [TestMethod]
    public async Task Run_FollowerCannotStart_ReturnsFalse()
    {
        var h = new Harness(_config, _active);
        h.Follower.StartThrows = true;
        Assert.IsFalse(await h.Seeder.RunAsync(CancellationToken.None));
        Assert.AreEqual(1, h.ReadyCalls);
        Assert.AreEqual(0, h.Channel.Requests);
    }

    [TestMethod]
    public async Task Run_InitialRestoreTimesOut_ReturnsFalse()
    {
        _config.RestoreTimeout = TimeSpan.FromMilliseconds(300);
        var h = new Harness(_config, _active);
        Assert.IsFalse(await h.Seeder.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(1, h.ReadyCalls);
    }

    [TestMethod]
    public async Task Run_LeaseNotHeld_ReturnsFalse()
    {
        var h = new Harness(_config, _active);
        var run = h.Seeder.RunAsync(CancellationToken.None);
        h.Follower.Restored(10, "x");
        h.Lease.TrySetResult(false);
        Assert.IsFalse(await run.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task Run_FollowedCopyFailsQuickCheck_ReturnsFalse()
    {
        CreateDb(_active, "old-live");
        var h = new Harness(_config, _active);
        h.Follower.WriteCorrupt = true;
        var run = h.Seeder.RunAsync(CancellationToken.None);
        h.Follower.Restored(10, "x");
        await h.OldWriterAcknowledgesAsync(10);
        Assert.IsFalse(await run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual("old-live", ReadMarker(_active));
    }

    [TestMethod]
    public async Task Run_FollowerStopExitsNonZero_ReturnsFalse()
    {
        var h = new Harness(_config, _active);
        h.Follower.StopExitCode = 2;
        var run = h.Seeder.RunAsync(CancellationToken.None);
        h.Follower.Restored(10, "x");
        await h.OldWriterAcknowledgesAsync(10);
        Assert.IsFalse(await run.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task Run_Cancelled_Throws()
    {
        var h = new Harness(_config, _active);
        using var cts = new CancellationTokenSource();
        var run = h.Seeder.RunAsync(cts.Token);
        await Task.Delay(50);
        cts.Cancel();
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => run);
    }

    // The tail wait on its own.

    [TestMethod]
    public async Task WaitForCatchUp_TargetReached_True()
    {
        long? txid = 5;
        var task = CooperativeHandoverSeeder.WaitForCatchUpAsync(() => txid, () => false, 7, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(10), CancellationToken.None);
        await Task.Delay(60);
        Assert.IsFalse(task.IsCompleted);
        txid = 7;
        Assert.IsTrue(await task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [TestMethod]
    public async Task WaitForCatchUp_TargetAheadOfAck_TrueImmediately()
    {
        Assert.IsTrue(await CooperativeHandoverSeeder.WaitForCatchUpAsync(() => 9, () => false, 7, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(10), CancellationToken.None));
    }

    [TestMethod]
    public async Task WaitForCatchUp_Timeout_False()
    {
        Assert.IsFalse(await CooperativeHandoverSeeder.WaitForCatchUpAsync(() => 5, () => false, 7, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(10), CancellationToken.None));
    }

    [TestMethod]
    public async Task WaitForCatchUp_FollowerDied_False()
    {
        var exited = false;
        var task = CooperativeHandoverSeeder.WaitForCatchUpAsync(() => 5, () => exited, 7, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(10), CancellationToken.None);
        exited = true;
        Assert.IsFalse(await task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [TestMethod]
    public async Task WaitForCatchUp_NoTarget_TrueOnceQuiet_ResetsWhileMoving()
    {
        long? txid = 5;
        var task = CooperativeHandoverSeeder.WaitForCatchUpAsync(() => txid, () => false, null, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(10), CancellationToken.None);
        await Task.Delay(150); txid = 6;
        await Task.Delay(150); txid = 7;
        await Task.Delay(150);
        Assert.IsFalse(task.IsCompleted, "still moving");
        Assert.IsTrue(await task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [TestMethod]
    public async Task WaitForCatchUp_NoTxidYet_WaitsThenTimesOut()
    {
        Assert.IsFalse(await CooperativeHandoverSeeder.WaitForCatchUpAsync(() => null, () => false, null, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(10), CancellationToken.None));
    }

    [TestMethod]
    public void TxidSidecar_ParsesLitestreamHex()
    {
        var p = Path.Combine(_dir, "f.db-txid");
        File.WriteAllText(p, "000000000000002a\n");
        Assert.AreEqual(42L, LitestreamFollower.ReadTxidFile(p));
        Assert.IsNull(LitestreamFollower.ReadTxidFile(p + ".missing"));
        Assert.AreEqual("500ms", LitestreamFollower.FormatGoDuration(TimeSpan.FromMilliseconds(500)));
        Assert.AreEqual("2s", LitestreamFollower.FormatGoDuration(TimeSpan.FromSeconds(2)));
    }
}
