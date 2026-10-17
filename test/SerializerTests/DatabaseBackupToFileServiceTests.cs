using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RssReader.Server.Services;

namespace SerializerTests;

[TestClass]
public class DatabaseBackupToFileServiceTests
{
    private string _testDir = null!;
    private string _activeDbPath = null!;
    private string _backupDbPath = null!;
    private DatabaseBackupToFilePaths _paths = null!;
    private ShareWriterLock _lock = null!;
    private DatabaseBackupToFileService _service = null!;

    [TestInitialize]
    public void Setup()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "DatabaseBackupToFileServiceTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDir);

        var activeDir = Path.Combine(_testDir, "active");
        var backupDir = Path.Combine(_testDir, "backup");
        Directory.CreateDirectory(activeDir);
        Directory.CreateDirectory(backupDir);

        _activeDbPath = Path.Combine(activeDir, "storage.db");
        _backupDbPath = Path.Combine(backupDir, "storage.db");

        _paths = new DatabaseBackupToFilePaths(
            ActiveDbPath: _activeDbPath,
            BackupDbPath: _backupDbPath,
            Interval: TimeSpan.FromMinutes(5),
            LockStaleThreshold: TimeSpan.FromSeconds(10),
            LockHeartbeatInterval: TimeSpan.FromMilliseconds(200),
            ShutdownBackupTimeout: TimeSpan.FromSeconds(30));

        _lock = NewLock();
        _service = new DatabaseBackupToFileService(
            NullLogger<DatabaseBackupToFileService>.Instance,
            _paths,
            _lock);

        SeedSqliteDb(_activeDbPath, ("alpha", 1), ("beta", 2));
    }

    private ShareWriterLock NewLock() => new(
        _paths.LockPath, _paths.LockStaleThreshold, _paths.HeartbeatInterval,
        NullLogger.Instance)
    { PollInterval = TimeSpan.FromMilliseconds(100) };

    [TestCleanup]
    public void Cleanup()
    {
        _lock.Release();
        SqliteConnection.ClearAllPools();
        Thread.Sleep(50);
        try
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, recursive: true);
        }
        catch (IOException) { }
    }

    [TestMethod]
    public async Task RunBackup_ProducesValidSqliteFileWithSameRows()
    {
        var ok = await _service.RunBackupCycleAsync(CancellationToken.None);

        Assert.IsTrue(ok, "Backup cycle should succeed");
        Assert.IsTrue(File.Exists(_backupDbPath), "Backup file should exist at target path");

        var rows = ReadAllRows(_backupDbPath);
        CollectionAssert.AreEquivalent(
            new[] { ("alpha", 1), ("beta", 2) },
            rows.ToArray());
    }

    [TestMethod]
    public async Task RunBackup_RecordsLastSuccessTimestamp()
    {
        Assert.IsNull(_service.LastSuccessAtUtc);
        await _service.RunBackupCycleAsync(CancellationToken.None);
        Assert.IsNotNull(_service.LastSuccessAtUtc);
    }

    [TestMethod]
    public async Task RunBackup_LeavesNoTempFilesOnSuccess()
    {
        await _service.RunBackupCycleAsync(CancellationToken.None);

        var backupDir = Path.GetDirectoryName(_backupDbPath)!;
        var tmpFiles = Directory.GetFiles(backupDir, "*.tmp.*");
        Assert.AreEqual(0, tmpFiles.Length, "No leftover temp files on successful backup");
    }

    [TestMethod]
    public async Task RunBackup_KeepsLockUntilStop()
    {
        // The lock is the writer's lease for its whole lifetime (issue #136): a
        // booting revision waits on it, so it must not drop between cycles.
        await _service.RunBackupCycleAsync(CancellationToken.None);
        Assert.IsTrue(_lock.IsHeld);
        Assert.IsTrue(File.Exists(_paths.LockPath), "Lock stays held after a cycle");

        await _service.StopAsync(CancellationToken.None);
        Assert.IsFalse(_lock.IsHeld);
        Assert.IsFalse(File.Exists(_paths.LockPath), "Lock released after the final backup on stop");
    }

    [TestMethod]
    public async Task RunBackup_SkipsWhenAnotherWriterHoldsLock()
    {
        // Simulate another writer with a fresh lock file.
        File.WriteAllText(_paths.LockPath, "other-host:9999:active");

        var ok = await _service.RunBackupCycleAsync(CancellationToken.None);

        Assert.IsFalse(ok, "Should skip when another writer holds the lock");
        Assert.IsFalse(File.Exists(_backupDbPath),
            "No backup should be written when locked out");
        Assert.IsTrue(File.Exists(_paths.LockPath),
            "We must NOT delete a fresh lock owned by another writer");
    }

    [TestMethod]
    public async Task RunBackup_TakesOverStaleLock()
    {
        // Place a stale lock (mtime older than threshold).
        File.WriteAllText(_paths.LockPath, "crashed-host:1234");
        File.SetLastWriteTimeUtc(_paths.LockPath, DateTime.UtcNow - TimeSpan.FromMinutes(30));

        var ok = await _service.RunBackupCycleAsync(CancellationToken.None);

        Assert.IsTrue(ok, "Should take over a stale lock and run successfully");
        Assert.IsTrue(File.Exists(_backupDbPath));
    }

    [TestMethod]
    public async Task RunBackup_TakenOverLockIsOursAndReleasedOnStop()
    {
        File.WriteAllText(_paths.LockPath, "crashed-host:1234");
        File.SetLastWriteTimeUtc(_paths.LockPath, DateTime.UtcNow - TimeSpan.FromMinutes(30));

        var ok = await _service.RunBackupCycleAsync(CancellationToken.None);
        Assert.IsTrue(ok);
        Assert.IsTrue(_lock.StillOwned(), "After takeover the lock file carries our token");
        Assert.AreNotEqual("crashed-host:1234", ShareWriterLock.ReadFirstLine(_paths.LockPath));

        await _service.StopAsync(CancellationToken.None);
        Assert.IsFalse(File.Exists(_paths.LockPath),
            "Our own lock is released once the final backup has run");
    }

    [TestMethod]
    public async Task RunBackup_DoesNotPublishWhenLockWasTakenOver()
    {
        await _service.RunBackupCycleAsync(CancellationToken.None);
        var before = File.GetLastWriteTimeUtc(_backupDbPath);

        // Another writer decided our lock was stale and replaced it.
        _lock.Dispose();
        File.WriteAllText(_paths.LockPath, "newer-writer-token");
        var stolen = NewLock();   // fresh instance: it will see a live foreign lock
        var svc = new DatabaseBackupToFileService(NullLogger<DatabaseBackupToFileService>.Instance, _paths, stolen);

        SqliteConnection.ClearAllPools();
        AddRow(_activeDbPath, "gamma", 3);
        Assert.IsFalse(await svc.RunBackupCycleAsync(CancellationToken.None));
        Assert.AreEqual(before, File.GetLastWriteTimeUtc(_backupDbPath), "Share copy untouched");
        Assert.AreEqual("newer-writer-token", ShareWriterLock.ReadFirstLine(_paths.LockPath));
    }

    // ── Deploy handover (issue #136) ───────────────────────────────────

    [TestMethod]
    public async Task StopAsync_RunsFinalBackupThenReleasesLock()
    {
        Assert.IsTrue(await _service.RunBackupCycleAsync(CancellationToken.None));
        Assert.AreEqual(2, ReadAllRows(_backupDbPath).Count);

        // A write that lands after the last periodic cycle: exactly what a deploy lost.
        SqliteConnection.ClearAllPools();
        AddRow(_activeDbPath, "gamma", 3);

        await _service.StopAsync(CancellationToken.None);

        Assert.IsNotNull(_service.FinalBackupAtUtc, "Final backup ran on stop");
        Assert.AreEqual(3, ReadAllRows(_backupDbPath).Count, "Share copy holds the late write");
        Assert.IsFalse(File.Exists(_paths.LockPath), "Lock released after the final copy");
    }

    [TestMethod]
    public async Task StopAsync_FinalBackupIgnoresUnchangedSkip()
    {
        Assert.IsTrue(await _service.RunBackupCycleAsync(CancellationToken.None));
        // Prove the periodic skip (#128) is in effect for an unchanged DB...
        Assert.IsFalse(await _service.RunBackupCycleAsync(CancellationToken.None));
        var stale = DateTime.UtcNow - TimeSpan.FromHours(1);
        File.SetLastWriteTimeUtc(_backupDbPath, stale);

        // ...and that the final flush does not honour it.
        await _service.StopAsync(CancellationToken.None);

        Assert.IsNotNull(_service.FinalBackupAtUtc);
        Assert.IsTrue(File.GetLastWriteTimeUtc(_backupDbPath) > stale, "Share copy rewritten by the final flush");
    }

    [TestMethod]
    public async Task StopAsync_WithoutLockSkipsFinalBackupQuietly()
    {
        File.WriteAllText(_paths.LockPath, "other-writer");
        await _service.StopAsync(CancellationToken.None);
        Assert.IsNull(_service.FinalBackupAtUtc);
        Assert.IsFalse(File.Exists(_backupDbPath));
        Assert.IsTrue(File.Exists(_paths.LockPath), "Foreign lock untouched");
    }

    [TestMethod]
    public async Task Seed_WaitsWhileForeignLockIsAliveAndProceedsWhenReleased()
    {
        // The share copy is what the old revision will leave behind.
        SeedSqliteDb(_backupDbPath, ("from-share", 42));
        File.WriteAllText(_paths.LockPath, "old-revision-token\nold:1:now");

        var seed = DatabaseSeeder.WaitForLockAndSeedAsync(_paths, _lock, NullLogger.Instance, CancellationToken.None);
        await Task.Delay(600);
        Assert.IsFalse(seed.IsCompleted, "Must wait while the other writer's lock is fresh");
        Assert.AreEqual(2, ReadAllRows(_activeDbPath).Count, "Live DB untouched while waiting");

        // Old revision finishes its final backup and releases.
        File.Delete(_paths.LockPath);
        var seeded = await seed.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.IsTrue(seeded);
        Assert.IsTrue(_lock.IsHeld, "The new writer now holds the lock");
        CollectionAssert.AreEquivalent(new[] { ("from-share", 42) }, ReadAllRows(_activeDbPath).ToArray());
    }

    [TestMethod]
    public async Task Seed_ProceedsWhenForeignLockIsStale()
    {
        SeedSqliteDb(_backupDbPath, ("from-share", 42));
        File.WriteAllText(_paths.LockPath, "crashed-revision");
        File.SetLastWriteTimeUtc(_paths.LockPath, DateTime.UtcNow - TimeSpan.FromMinutes(5));

        var seeded = await DatabaseSeeder
            .WaitForLockAndSeedAsync(_paths, _lock, NullLogger.Instance, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsTrue(seeded);
        Assert.IsTrue(_lock.StillOwned(), "Stale lock taken over");
        Assert.AreEqual(1, ReadAllRows(_activeDbPath).Count);
    }

    [TestMethod]
    public async Task Lock_HeartbeatKeepsWaiterWaitingUntilRelease()
    {
        // The holder's heartbeat (200 ms here) keeps the mtime fresh, so a waiter
        // with a 10 s stale threshold must still be waiting well past one interval.
        Assert.IsTrue(_lock.TryAcquire());
        var first = File.GetLastWriteTimeUtc(_paths.LockPath);
        await Task.Delay(700);
        Assert.IsTrue(File.GetLastWriteTimeUtc(_paths.LockPath) > first, "Heartbeat moved the mtime");

        var waiter = NewLock();
        Assert.IsFalse(waiter.TryAcquire());
        var wait = waiter.AcquireAsync(CancellationToken.None);
        await Task.Delay(500);
        Assert.IsFalse(wait.IsCompleted);

        _lock.Release();
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(waiter.IsHeld);
        waiter.Release();
    }

    [TestMethod]
    public async Task Seed_CancelledWhileWaitingLeavesLockAlone()
    {
        File.WriteAllText(_paths.LockPath, "old-revision");
        using var cts = new CancellationTokenSource(300);
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() =>
            DatabaseSeeder.WaitForLockAndSeedAsync(_paths, _lock, NullLogger.Instance, cts.Token));
        Assert.IsFalse(_lock.IsHeld);
        Assert.AreEqual("old-revision", ShareWriterLock.ReadFirstLine(_paths.LockPath));
    }

    [TestMethod]
    public async Task Seed_RefusesCorruptShareCopy()
    {
        File.WriteAllText(_backupDbPath, "this is not a database");

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            DatabaseSeeder.SeedFromShareAsync(_paths, NullLogger.Instance, CancellationToken.None));

        Assert.AreEqual(2, ReadAllRows(_activeDbPath).Count, "Live DB left as it was");
        Assert.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(_activeDbPath)!, "*.seed.*").Length);
    }

    [TestMethod]
    public async Task Seed_WithoutShareCopyKeepsLocalDb()
    {
        var seeded = await DatabaseSeeder.SeedFromShareAsync(_paths, NullLogger.Instance, CancellationToken.None);
        Assert.IsFalse(seeded);
        Assert.AreEqual(2, ReadAllRows(_activeDbPath).Count);
    }

    [TestMethod]
    public async Task RunBackup_DisabledWhenBackupPathEmpty()
    {
        var paths = new DatabaseBackupToFilePaths(
            ActiveDbPath: _activeDbPath,
            BackupDbPath: string.Empty,
            Interval: TimeSpan.FromMinutes(5),
            LockStaleThreshold: TimeSpan.FromSeconds(10));
        var svc = new DatabaseBackupToFileService(
            NullLogger<DatabaseBackupToFileService>.Instance, paths);

        Assert.IsFalse(svc.Enabled);
        var ok = await svc.RunBackupCycleAsync(CancellationToken.None);
        Assert.IsFalse(ok);
    }

    [TestMethod]
    public void CopiedFileMatchesStage_RejectsTruncatedCopy()
    {
        // Models the 2026-10-02 outage: the share quota filled mid-copy, the SMB
        // client dropped the write error, and a short file was renamed into place.
        var stage = Path.Combine(_testDir, "stage.db");
        var copy = Path.Combine(_testDir, "copy.db");
        File.WriteAllBytes(stage, new byte[8192]);
        File.WriteAllBytes(copy, new byte[8192 - 1]);

        Assert.IsFalse(DatabaseBackupToFileService.CopiedFileMatchesStage(stage, copy, out var expected, out var actual));
        Assert.AreEqual(8192, expected);
        Assert.AreEqual(8191, actual);
    }

    [TestMethod]
    public void CopiedFileMatchesStage_AcceptsFullCopy()
    {
        var stage = Path.Combine(_testDir, "stage.db");
        var copy = Path.Combine(_testDir, "copy.db");
        File.WriteAllBytes(stage, new byte[8192]);
        File.Copy(stage, copy);

        Assert.IsTrue(DatabaseBackupToFileService.CopiedFileMatchesStage(stage, copy, out _, out _));
    }

    [TestMethod]
    public void CopiedFileMatchesStage_RejectsMissingCopy()
    {
        var stage = Path.Combine(_testDir, "stage.db");
        File.WriteAllBytes(stage, new byte[8192]);

        Assert.IsFalse(DatabaseBackupToFileService.CopiedFileMatchesStage(stage, Path.Combine(_testDir, "nope.db"), out _, out var actual));
        Assert.AreEqual(-1, actual);
    }

    [TestMethod]
    public async Task RunBackup_RefusesWhenActiveDbMissing()
    {
        File.Delete(_activeDbPath);

        var ok = await _service.RunBackupCycleAsync(CancellationToken.None);

        Assert.IsFalse(ok);
        Assert.IsFalse(File.Exists(_backupDbPath),
            "Must not overwrite backup with empty data when source is missing");
    }

    [TestMethod]
    public void CleanupStaleTempFiles_RemovesOldTempFilesOnly()
    {
        var backupDir = Path.GetDirectoryName(_backupDbPath)!;
        var basename = Path.GetFileName(_backupDbPath);

        var stale = Path.Combine(backupDir, $"{basename}.tmp.host.123.aaaa");
        var fresh = Path.Combine(backupDir, $"{basename}.tmp.host.456.bbbb");
        var unrelated = Path.Combine(backupDir, "unrelated-file.txt");

        File.WriteAllText(stale, "stale");
        File.WriteAllText(fresh, "fresh");
        File.WriteAllText(unrelated, "unrelated");

        // Cutoff = 2× LockStaleThreshold (10s in tests) = 20s.
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow - TimeSpan.FromMinutes(5));
        File.SetLastWriteTimeUtc(fresh, DateTime.UtcNow);

        _service.CleanupStaleTempFiles();

        Assert.IsFalse(File.Exists(stale), "Stale tmp file should be deleted");
        Assert.IsTrue(File.Exists(fresh), "Fresh tmp file should be preserved");
        Assert.IsTrue(File.Exists(unrelated), "Unrelated files should not be touched");
    }

    [TestMethod]
    public async Task RunBackup_SkipsWhenActiveDbUnchanged()
    {
        Assert.IsTrue(await _service.RunBackupCycleAsync(CancellationToken.None));
        var firstWrite = File.GetLastWriteTimeUtc(_backupDbPath);
        var firstSuccess = _service.LastSuccessAtUtc;

        // No writes to the active DB: second cycle must not touch the share.
        var ok = await _service.RunBackupCycleAsync(CancellationToken.None);
        Assert.IsFalse(ok, "Unchanged DB should be skipped");
        Assert.AreEqual(firstWrite, File.GetLastWriteTimeUtc(_backupDbPath));
        Assert.AreEqual(firstSuccess, _service.LastSuccessAtUtc);

        // Mutate, and the next cycle backs up again.
        SqliteConnection.ClearAllPools();
        AddRow(_activeDbPath, "gamma", 3);
        Assert.IsTrue(await _service.RunBackupCycleAsync(CancellationToken.None));
        Assert.AreEqual(3, ReadAllRows(_backupDbPath).Count);
    }

    [TestMethod]
    public async Task RunBackup_OverwritesPreviousBackupFile()
    {
        // First cycle.
        await _service.RunBackupCycleAsync(CancellationToken.None);
        Assert.AreEqual(2, ReadAllRows(_backupDbPath).Count);

        // Mutate active DB.
        SqliteConnection.ClearAllPools();
        AddRow(_activeDbPath, "gamma", 3);

        // Second cycle.
        var ok = await _service.RunBackupCycleAsync(CancellationToken.None);
        Assert.IsTrue(ok);
        var rows = ReadAllRows(_backupDbPath);
        Assert.AreEqual(3, rows.Count, "Second backup should overwrite the first");
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static void SeedSqliteDb(string path, params (string Name, int Value)[] rows)
    {
        using var conn = new SqliteConnection($"Data Source={path};Pooling=False");
        conn.Open();
        using var create = conn.CreateCommand();
        create.CommandText = "CREATE TABLE IF NOT EXISTS kv (name TEXT, value INTEGER);";
        create.ExecuteNonQuery();
        foreach (var (n, v) in rows)
        {
            using var insert = conn.CreateCommand();
            insert.CommandText = "INSERT INTO kv (name, value) VALUES ($n, $v);";
            insert.Parameters.AddWithValue("$n", n);
            insert.Parameters.AddWithValue("$v", v);
            insert.ExecuteNonQuery();
        }
    }

    private static void AddRow(string path, string name, int value)
    {
        using var conn = new SqliteConnection($"Data Source={path};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO kv (name, value) VALUES ($n, $v);";
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    private static List<(string Name, int Value)> ReadAllRows(string path)
    {
        var rows = new List<(string, int)>();
        using var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name, value FROM kv ORDER BY name;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetInt32(1)));
        return rows;
    }
}
