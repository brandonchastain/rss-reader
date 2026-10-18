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
    public async Task RunBackup_TakenOverStaleLockIsReleasedOnStop()
    {
        File.WriteAllText(_paths.LockPath, "crashed-host:1234");
        File.SetLastWriteTimeUtc(_paths.LockPath, DateTime.UtcNow - TimeSpan.FromMinutes(30));

        Assert.IsTrue(await _service.RunBackupCycleAsync(CancellationToken.None));
        Assert.IsTrue(_lock.IsHeld, "Stale lock taken over and kept");

        await _service.StopAsync(CancellationToken.None);
        Assert.IsFalse(File.Exists(_paths.LockPath), "Released once the final backup has run");
    }

    // ── Deploy handover (issue #136) ───────────────────────────────────

    [TestMethod]
    public async Task StopAsync_FinalBackupTimeout_StillReleasesLock()
    {
        // The host's shutdown timeout already expired (cancelled token), which is the
        // outer bound on ShutdownBackupTimeout: the copy must not run, nothing may
        // throw, and the lock must still be released so the next revision can boot.
        var svc = _service;
        Assert.IsTrue(await svc.RunBackupCycleAsync(CancellationToken.None));
        var before = File.GetLastWriteTimeUtc(_backupDbPath);
        SqliteConnection.ClearAllPools();
        AddRow(_activeDbPath, "gamma", 3);

        await svc.StopAsync(new CancellationToken(canceled: true));   // must not throw

        Assert.IsNull(svc.FinalBackupAtUtc, "Timed out before the copy");
        Assert.AreEqual(before, File.GetLastWriteTimeUtc(_backupDbPath), "Nothing half-written");
        Assert.IsFalse(_lock.IsHeld);
        Assert.IsFalse(File.Exists(_paths.LockPath), "Lock released even though the final copy timed out");
        Assert.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(_backupDbPath)!, "*.tmp.*").Length);
    }

    [TestMethod]
    public async Task StopAsync_FinalBackupBoundedByShutdownBackupTimeout()
    {
        // The timeout is wired as a linked CancelAfter on the copy. A tiny budget is
        // observed as the copy being cancelled: either before it starts (no new copy)
        // or mid-way (no partial publish). Either way the lock is released.
        var paths = _paths with { ShutdownBackupTimeout = TimeSpan.FromTicks(1) };
        var svc = new DatabaseBackupToFileService(NullLogger<DatabaseBackupToFileService>.Instance, paths, _lock);
        Assert.IsTrue(await svc.RunBackupCycleAsync(CancellationToken.None));
        var rowsBefore = ReadAllRows(_backupDbPath).Count;

        await svc.StopAsync(CancellationToken.None);

        var rows = ReadAllRows(_backupDbPath).Count;
        Assert.AreEqual(rowsBefore, rows, "Share copy is either untouched or a complete copy of the same rows");
        Assert.IsFalse(_lock.IsHeld);
        Assert.IsFalse(File.Exists(_paths.LockPath));
        Assert.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(_backupDbPath)!, "*.tmp.*").Length);
    }

    [TestMethod]
    public async Task StopAsync_FinalBackupFailure_StillReleasesLock()
    {
        Assert.IsTrue(await _service.RunBackupCycleAsync(CancellationToken.None));
        File.Delete(_activeDbPath);   // the copy will refuse to run

        await _service.StopAsync(CancellationToken.None);

        Assert.IsNull(_service.FinalBackupAtUtc);
        Assert.IsFalse(_lock.IsHeld);
        Assert.IsFalse(File.Exists(_paths.LockPath));
    }

    [TestMethod]
    public async Task StopAsync_LockHeldUntilTheFinalCopyIsPublished()
    {
        // The share copy is renamed into place while the lock is still held: the lock's
        // mtime (last heartbeat) and the copy's are both from the stop, and the lock
        // file was present for the whole copy. Observed through a watcher on the
        // backup directory: the lock's deletion is the last event.
        Assert.IsTrue(await _service.RunBackupCycleAsync(CancellationToken.None));
        var events = new List<string>();
        using var watcher = new FileSystemWatcher(Path.GetDirectoryName(_backupDbPath)!)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            EnableRaisingEvents = true,
        };
        watcher.Deleted += (_, e) => { lock (events) events.Add("deleted:" + e.Name); };
        watcher.Renamed += (_, e) => { lock (events) events.Add("renamed:" + e.Name); };

        await _service.StopAsync(CancellationToken.None);
        await Task.Delay(300);   // let the watcher drain

        string[] snapshot; lock (events) snapshot = events.ToArray();
        var publish = Array.FindIndex(snapshot, e => e == "renamed:" + Path.GetFileName(_backupDbPath));
        var release = Array.FindIndex(snapshot, e => e == "deleted:" + Path.GetFileName(_paths.LockPath));
        Assert.IsTrue(publish >= 0, "Final copy published: " + string.Join(",", snapshot));
        Assert.IsTrue(release > publish, "Lock deleted after the publish: " + string.Join(",", snapshot));
    }

    [TestMethod]
    public async Task Disabled_NoBackupPath_StopDoesNothingAndTouchesNoFiles()
    {
        // Self-host / local dev: BackupDbPath empty means no lock, no flush, no gate.
        var paths = _paths with { BackupDbPath = string.Empty };
        var l = new ShareWriterLock(paths.LockPath, paths.LockStaleThreshold, paths.HeartbeatInterval, NullLogger.Instance);
        var svc = new DatabaseBackupToFileService(NullLogger<DatabaseBackupToFileService>.Instance, paths, l);
        Assert.IsFalse(svc.Enabled);

        await svc.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        await svc.StopAsync(CancellationToken.None);

        Assert.IsFalse(l.IsHeld);
        Assert.IsNull(svc.FinalBackupAtUtc);
        Assert.IsFalse(File.Exists(".lock"));
        Assert.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(_backupDbPath)!).Length, "Share dir untouched");
    }

    [TestMethod]
    public async Task UnwritableLockDirectory_CyclesAndStopStillRunWithoutLock()
    {
        // Self-host with a lock that cannot be created: warn and continue. The backup
        // itself goes to a writable share dir here, so cycles still succeed.
        var l = new ShareWriterLock(Path.Combine(_testDir, "missing", "x.lock"), _paths.LockStaleThreshold, _paths.HeartbeatInterval, NullLogger.Instance)
        { PollInterval = TimeSpan.FromMilliseconds(20), GiveUpAfter = TimeSpan.FromMilliseconds(100) };
        var svc = new DatabaseBackupToFileService(NullLogger<DatabaseBackupToFileService>.Instance, _paths, l);

        Assert.IsTrue(await svc.RunBackupCycleAsync(CancellationToken.None), "Cycle runs without a lock");
        Assert.IsFalse(l.IsHeld);
        SqliteConnection.ClearAllPools();
        AddRow(_activeDbPath, "gamma", 3);

        await svc.StopAsync(CancellationToken.None);

        Assert.IsNotNull(svc.FinalBackupAtUtc, "Final copy still runs");
        Assert.AreEqual(3, ReadAllRows(_backupDbPath).Count);
    }


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
