using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RssReader.Server.Services;

namespace RssReaderTests;

[TestClass]
public class DatabaseSeederTests
{
    private string _dir = null!;
    private DatabaseBackupToFilePaths _paths = null!;
    private ShareWriterLock _lock = null!;

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "DatabaseSeederTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_dir, "share"));
        Directory.CreateDirectory(Path.Combine(_dir, "live"));
        _paths = new DatabaseBackupToFilePaths(
            ActiveDbPath: Path.Combine(_dir, "live", "storage.db"),
            BackupDbPath: Path.Combine(_dir, "share", "storage.db"),
            Interval: TimeSpan.FromMinutes(5),
            LockStaleThreshold: TimeSpan.FromSeconds(30),
            LockHeartbeatInterval: TimeSpan.FromSeconds(30));
        _lock = new ShareWriterLock(_paths.LockPath, _paths.LockStaleThreshold, _paths.HeartbeatInterval, NullLogger.Instance)
        { PollInterval = TimeSpan.FromMilliseconds(20), GiveUpAfter = TimeSpan.FromMilliseconds(200) };
    }

    [TestCleanup]
    public void Cleanup()
    {
        _lock.Release();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static Task<bool> Seed(DatabaseBackupToFilePaths p) =>
        DatabaseSeeder.SeedFromShareAsync(p, NullLogger.Instance, CancellationToken.None);

    [TestMethod]
    public async Task Seed_ReplacesLiveDbWithShareCopy()
    {
        WriterStartupGateTests.SeedSqliteDb(_paths.BackupDbPath, "from-share");
        WriterStartupGateTests.SeedSqliteDb(_paths.ActiveDbPath, "stale-live");

        Assert.IsTrue(await Seed(_paths));

        Assert.AreEqual("from-share", WriterStartupGateTests.ReadMarker(_paths.ActiveDbPath));
        Assert.AreEqual(new FileInfo(_paths.BackupDbPath).Length, new FileInfo(_paths.ActiveDbPath).Length);
        Assert.IsTrue(File.Exists(_paths.BackupDbPath), "Share copy is copied, not moved");
    }

    [TestMethod]
    public async Task Seed_CreatesLiveDbWhenNoneExists()
    {
        WriterStartupGateTests.SeedSqliteDb(_paths.BackupDbPath, "from-share");
        Assert.IsTrue(await Seed(_paths));
        Assert.AreEqual("from-share", WriterStartupGateTests.ReadMarker(_paths.ActiveDbPath));
    }

    [TestMethod]
    public async Task Seed_CreatesMissingLiveDirectory()
    {
        WriterStartupGateTests.SeedSqliteDb(_paths.BackupDbPath, "from-share");
        var p = _paths with { ActiveDbPath = Path.Combine(_dir, "live", "nested", "storage.db") };
        Assert.IsTrue(await Seed(p));
        Assert.AreEqual("from-share", WriterStartupGateTests.ReadMarker(p.ActiveDbPath));
    }

    [TestMethod]
    public async Task Seed_DropsStaleWalSidecarsOfThePreviousLiveFile()
    {
        WriterStartupGateTests.SeedSqliteDb(_paths.BackupDbPath, "from-share");
        File.WriteAllText(_paths.ActiveDbPath + "-wal", "old wal");
        File.WriteAllText(_paths.ActiveDbPath + "-shm", "old shm");

        await Seed(_paths);

        Assert.IsFalse(File.Exists(_paths.ActiveDbPath + "-wal"));
        Assert.IsFalse(File.Exists(_paths.ActiveDbPath + "-shm"));
    }

    [TestMethod]
    public async Task Seed_StagesAndChecksBeforeReplacing_RefusesCorruptCopy()
    {
        WriterStartupGateTests.SeedSqliteDb(_paths.ActiveDbPath, "stale-live");
        File.WriteAllText(_paths.BackupDbPath, "this is not a database");

        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => Seed(_paths));

        StringAssert.Contains(ex.Message, "quick_check");
        Assert.AreEqual("stale-live", WriterStartupGateTests.ReadMarker(_paths.ActiveDbPath), "Live DB kept");
        Assert.AreEqual(0, Directory.GetFiles(Path.Combine(_dir, "live"), "*.seed.*").Length, "Stage file cleaned up");
    }

    [TestMethod]
    public async Task Seed_NoShareCopy_LeavesLiveDbAlone()
    {
        WriterStartupGateTests.SeedSqliteDb(_paths.ActiveDbPath, "local");
        Assert.IsFalse(await Seed(_paths));
        Assert.AreEqual("local", WriterStartupGateTests.ReadMarker(_paths.ActiveDbPath));
    }

    [TestMethod]
    public async Task Seed_NoShareCopyAndNoLiveDb_LeavesNothing()
    {
        // Today's first-boot behaviour: SQLite creates the database on first open.
        Assert.IsFalse(await Seed(_paths));
        Assert.IsFalse(File.Exists(_paths.ActiveDbPath));
    }

    [TestMethod]
    public async Task WaitAndSeed_WaitsOnLiveLock_ProceedsWhenReleased()
    {
        WriterStartupGateTests.SeedSqliteDb(_paths.BackupDbPath, "from-share");
        WriterStartupGateTests.SeedSqliteDb(_paths.ActiveDbPath, "stale-live");
        File.WriteAllText(_paths.LockPath, "old-revision");

        var seed = DatabaseSeeder.WaitForLockAndSeedAsync(_paths, _lock, NullLogger.Instance, CancellationToken.None);
        await Task.Delay(300);
        Assert.IsFalse(seed.IsCompleted);
        Assert.AreEqual("stale-live", WriterStartupGateTests.ReadMarker(_paths.ActiveDbPath));

        File.Delete(_paths.LockPath);
        Assert.IsTrue(await seed.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.IsTrue(_lock.IsHeld);
        Assert.AreEqual("from-share", WriterStartupGateTests.ReadMarker(_paths.ActiveDbPath));
    }

    [TestMethod]
    public async Task WaitAndSeed_ProceedsWhenLockIsStale()
    {
        WriterStartupGateTests.SeedSqliteDb(_paths.BackupDbPath, "from-share");
        File.WriteAllText(_paths.LockPath, "crashed-revision");
        File.SetLastWriteTimeUtc(_paths.LockPath, DateTime.UtcNow - TimeSpan.FromMinutes(5));

        Assert.IsTrue(await DatabaseSeeder
            .WaitForLockAndSeedAsync(_paths, _lock, NullLogger.Instance, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.IsTrue(_lock.IsHeld);
        Assert.AreEqual("from-share", WriterStartupGateTests.ReadMarker(_paths.ActiveDbPath));
    }

    [TestMethod]
    public async Task WaitAndSeed_CancelledWhileWaiting_LeavesForeignLock()
    {
        File.WriteAllText(_paths.LockPath, "old-revision");
        using var cts = new CancellationTokenSource(200);
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() =>
            DatabaseSeeder.WaitForLockAndSeedAsync(_paths, _lock, NullLogger.Instance, cts.Token));
        Assert.IsFalse(_lock.IsHeld);
        Assert.AreEqual("old-revision", File.ReadAllText(_paths.LockPath));
    }

    [TestMethod]
    public async Task WaitAndSeed_UnwritableLockDirectory_WarnsAndSeedsAnyway()
    {
        // Self-host: a share copy in a directory where the lock cannot be created.
        // Boot must not hang or fail; the seed still happens, just without a lock.
        WriterStartupGateTests.SeedSqliteDb(_paths.BackupDbPath, "from-share");
        var unwritable = new ShareWriterLock(
            Path.Combine(_dir, "missing", "storage.db.lock"), _paths.LockStaleThreshold, _paths.HeartbeatInterval, NullLogger.Instance)
        { PollInterval = TimeSpan.FromMilliseconds(20), GiveUpAfter = TimeSpan.FromMilliseconds(200) };

        var seeded = await DatabaseSeeder
            .WaitForLockAndSeedAsync(_paths, unwritable, NullLogger.Instance, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.IsTrue(seeded);
        Assert.IsFalse(unwritable.IsHeld);
        Assert.AreEqual("from-share", WriterStartupGateTests.ReadMarker(_paths.ActiveDbPath));
    }

    // ── Shutdown delta ─────────────────────────────────────────────────

    /// <summary>Publish a base with the backup service, write late rows, then its shutdown delta.</summary>
    private async Task<DatabaseBackupToFileService> PublishBaseThenDelta(string liveDbPath, params string[] lateMarkers)
    {
        var writer = _paths with { ActiveDbPath = liveDbPath };
        var service = new DatabaseBackupToFileService(NullLogger<DatabaseBackupToFileService>.Instance, writer, _lock);
        Assert.IsTrue(await service.RunBackupCycleAsync(CancellationToken.None));
        foreach (var m in lateMarkers) WriterStartupGateTests.SeedSqliteDb(liveDbPath, m);
        Assert.IsTrue(await service.RunFinalBackupAsync(CancellationToken.None));
        Assert.AreEqual("delta", service.FinalBackupKind);
        return service;
    }

    [TestMethod]
    public async Task Seed_AppliesTheShutdownDeltaOnTopOfTheBase()
    {
        var previousLive = Path.Combine(_dir, "previous", "storage.db");
        Directory.CreateDirectory(Path.GetDirectoryName(previousLive)!);
        WriterStartupGateTests.SeedSqliteDb(previousLive, "periodic");
        await PublishBaseThenDelta(previousLive, "late-write");
        Assert.AreEqual("periodic", WriterStartupGateTests.ReadMarker(_paths.BackupDbPath), "Base alone is the periodic state");

        Assert.IsTrue(await Seed(_paths));

        Assert.AreEqual("late-write", WriterStartupGateTests.ReadMarker(_paths.ActiveDbPath));
        Assert.IsFalse(File.Exists(_paths.ActiveDbPath + "-wal"), "Shipped WAL folded into the main file");
        Assert.IsTrue(File.Exists(_paths.DeltaPath), "Delta stays on the share until the next base is published");
        Assert.AreEqual(0, Directory.GetFiles(Path.Combine(_dir, "live"), "*.seed.*").Length, "Stage files cleaned up");
    }

    [TestMethod]
    public async Task Seed_IgnoresADeltaThatBelongsToAnEarlierBase()
    {
        var previousLive = Path.Combine(_dir, "previous", "storage.db");
        Directory.CreateDirectory(Path.GetDirectoryName(previousLive)!);
        WriterStartupGateTests.SeedSqliteDb(previousLive, "periodic");
        await PublishBaseThenDelta(previousLive, "late-write");
        // A newer base replaced the file the delta was computed against (crash between
        // publishing it and deleting the old delta).
        File.Delete(_paths.BackupDbPath);
        WriterStartupGateTests.SeedSqliteDb(_paths.BackupDbPath, "newer-base");

        Assert.IsTrue(await Seed(_paths));

        Assert.AreEqual("newer-base", WriterStartupGateTests.ReadMarker(_paths.ActiveDbPath));
    }

    [TestMethod]
    public async Task Seed_IgnoresACorruptDelta_SeedsTheBaseAlone()
    {
        var previousLive = Path.Combine(_dir, "previous", "storage.db");
        Directory.CreateDirectory(Path.GetDirectoryName(previousLive)!);
        WriterStartupGateTests.SeedSqliteDb(previousLive, "periodic");
        await PublishBaseThenDelta(previousLive, "late-write");
        // Truncate the delta as a torn share write would.
        using (var f = new FileStream(_paths.DeltaPath, FileMode.Open)) f.SetLength(f.Length - 7);

        Assert.IsTrue(await Seed(_paths));

        Assert.AreEqual("periodic", WriterStartupGateTests.ReadMarker(_paths.ActiveDbPath));
    }
}
