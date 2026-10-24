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

    private static void WriteDb(string path, string marker)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE TABLE IF NOT EXISTS m (v TEXT); DELETE FROM m; INSERT INTO m VALUES ('{marker}');";
        cmd.ExecuteNonQuery();
        conn.Close();
        SqliteConnection.ClearAllPools(); // Windows: a pooled handle would block the seed's open
    }

    private static string MarkerOf(string path)
    {
        using var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT v FROM m LIMIT 1";
        return (string)cmd.ExecuteScalar()!;
    }

    // Rollout step 3 (BLOB-DURABILITY-SPEC.md 3.2): restore from the replica first, share copy
    // as the fallback.

    [TestMethod]
    public async Task RestoreOrSeed_RestoreSucceeds_SkipsShareCopyAndLeavesLockToBackupService()
    {
        WriteDb(_paths.BackupDbPath, "share");
        var restored = await DatabaseSeeder.RestoreOrSeedAsync(_paths, _lock, ct =>
        {
            WriteDb(_paths.ActiveDbPath, "replica");
            return Task.FromResult(true);
        }, NullLogger.Instance, CancellationToken.None);

        Assert.IsTrue(restored);
        Assert.AreEqual("replica", MarkerOf(_paths.ActiveDbPath), "the share copy must not overwrite the restore");
        Assert.IsFalse(_lock.IsHeld, "a successful restore does not wait for the share lock");
        Assert.IsFalse(File.Exists(_paths.LockPath));
    }

    [TestMethod]
    public async Task RestoreOrSeed_RestoreReturnsFalse_FallsBackToShareCopyWithLock()
    {
        WriteDb(_paths.BackupDbPath, "share");
        var restored = await DatabaseSeeder.RestoreOrSeedAsync(
            _paths, _lock, ct => Task.FromResult(false), NullLogger.Instance, CancellationToken.None);

        Assert.IsFalse(restored);
        Assert.AreEqual("share", MarkerOf(_paths.ActiveDbPath));
        Assert.IsTrue(_lock.IsHeld, "the share path takes the lock as before");
    }

    [TestMethod]
    public async Task RestoreOrSeed_RestoreThrows_FallsBackToShareCopy()
    {
        WriteDb(_paths.BackupDbPath, "share");
        var restored = await DatabaseSeeder.RestoreOrSeedAsync(
            _paths, _lock, ct => throw new InvalidOperationException("blob unreachable"), NullLogger.Instance, CancellationToken.None);

        Assert.IsFalse(restored);
        Assert.AreEqual("share", MarkerOf(_paths.ActiveDbPath));
        Assert.IsTrue(_lock.IsHeld);
    }

    [TestMethod]
    public async Task RestoreOrSeed_FallbackWaitsOnForeignLiveLock()
    {
        WriteDb(_paths.BackupDbPath, "share");
        var other = new ShareWriterLock(_paths.LockPath, _paths.LockStaleThreshold, _paths.HeartbeatInterval, NullLogger.Instance);
        Assert.IsTrue(other.TryAcquire());
        try
        {
            var task = DatabaseSeeder.RestoreOrSeedAsync(
                _paths, _lock, ct => Task.FromResult(false), NullLogger.Instance, CancellationToken.None);
            await Task.Delay(150);
            Assert.IsFalse(task.IsCompleted, "must wait for the previous writer's final copy before seeding from the share");
            other.Release();
            Assert.IsFalse(await task.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.AreEqual("share", MarkerOf(_paths.ActiveDbPath));
        }
        finally { other.Release(); }
    }

    [TestMethod]
    public async Task RestoreOrSeed_CancelledDuringRestore_Throws()
    {
        using var cts = new CancellationTokenSource();
        var task = DatabaseSeeder.RestoreOrSeedAsync(_paths, _lock, async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return true;
        }, NullLogger.Instance, cts.Token);
        cts.Cancel();
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => task);
        Assert.IsFalse(_lock.IsHeld);
    }

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
}
