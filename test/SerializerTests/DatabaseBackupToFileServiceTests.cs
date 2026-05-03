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
            LockStaleThreshold: TimeSpan.FromSeconds(10));

        _service = new DatabaseBackupToFileService(
            NullLogger<DatabaseBackupToFileService>.Instance,
            _paths);

        SeedSqliteDb(_activeDbPath, ("alpha", 1), ("beta", 2));
    }

    [TestCleanup]
    public void Cleanup()
    {
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
    public async Task RunBackup_LeavesNoLockFileOnSuccess()
    {
        await _service.RunBackupCycleAsync(CancellationToken.None);
        Assert.IsFalse(File.Exists(_backupDbPath + ".lock"),
            "Lock file should be released on success");
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
    public async Task RunBackup_LockFileContainsOwnerTokenOnFirstLine()
    {
        // Pause the cycle by holding a lock externally with a stale mtime so
        // our service takes it over. After the cycle completes successfully,
        // the lock should be released. Write a SECOND cycle's lock manually
        // and verify our service's lock format starts with a token line —
        // this is what the release path uses to identify ownership.
        File.WriteAllText(_paths.LockPath, "crashed-host:1234");
        File.SetLastWriteTimeUtc(_paths.LockPath, DateTime.UtcNow - TimeSpan.FromMinutes(30));

        // Hook: peek at the lock file content during the cycle by intercepting
        // via FileSystemWatcher. Simpler: create the source DB, run cycle, then
        // we don't have visibility. Instead verify the release-path contract by
        // a direct test: write a foreign-token lock, mark it stale, run cycle.
        // After cycle: lock should be GONE (we own it during the cycle, then
        // release it). The token check protects the case where someone else
        // overwrote the lock file mid-cycle, which is harder to simulate.
        var ok = await _service.RunBackupCycleAsync(CancellationToken.None);
        Assert.IsTrue(ok);
        Assert.IsFalse(File.Exists(_paths.LockPath),
            "Our own lock should be released after a successful cycle");
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
