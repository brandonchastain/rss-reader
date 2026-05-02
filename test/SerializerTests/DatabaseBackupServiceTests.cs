using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RssReader.Server.Services;

namespace SerializerTests;

[TestClass]
public class DatabaseBackupServiceTests
{
    private string _testDir = null!;
    private string _activeDbPath = null!;
    private string _backupDbPath = null!;
    private string _tempBackupDbPath = null!;
    private string _activeImagesPath = null!;
    private string _backupImagesPath = null!;
    private DatabaseBackupPaths _paths = null!;
    private DatabaseBackupService _service = null!;

    [TestInitialize]
    public void Setup()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "DatabaseBackupServiceTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDir);

        _activeDbPath = Path.Combine(_testDir, "active", "storage.db");
        _backupDbPath = Path.Combine(_testDir, "backup", "storage.db");
        _tempBackupDbPath = Path.Combine(_testDir, "active", "storage-backup.db");
        _activeImagesPath = Path.Combine(_testDir, "active", "images");
        _backupImagesPath = Path.Combine(_testDir, "backup", "images");

        Directory.CreateDirectory(Path.GetDirectoryName(_activeDbPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(_backupDbPath)!);

        _paths = new DatabaseBackupPaths(
            ActiveDbPath: _activeDbPath,
            BackupDbPath: _backupDbPath,
            TempBackupDbPath: _tempBackupDbPath,
            ActiveImagesPath: _activeImagesPath,
            BackupImagesPath: _backupImagesPath);

        _service = new DatabaseBackupService(
            NullLogger<DatabaseBackupService>.Instance,
            _paths);
    }

    [TestCleanup]
    public void Cleanup()
    {
        // SQLite connection pooling keeps file handles open on Windows.
        // Must clear all pools before deleting temp files.
        SqliteConnection.ClearAllPools();

        // Brief delay to let OS release file handles
        Thread.Sleep(50);

        try
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; temp dir will be cleaned by OS
        }
    }

    // ── Restore Tests ─────────────────────────────────────────────────

    [TestMethod]
    public async Task Restore_WhenActiveDbAlreadyExists_SkipsRestore()
    {
        // Arrange: Litestream already restored the DB (active exists)
        // Backup has DIFFERENT content — if restore runs, it would overwrite
        CreateSqliteDb(_activeDbPath, "active_data");
        CreateSqliteDb(_backupDbPath, "stale_backup_data");

        // Act
        await _service.RestoreFromBackupAsync(CancellationToken.None);

        // Assert: active DB is untouched (still has original content)
        var content = ReadSqliteData(_activeDbPath);
        Assert.AreEqual("active_data", content,
            "Active DB must NOT be overwritten when it already exists (Litestream coexistence contract)");
    }

    [TestMethod]
    public async Task Restore_WhenBackupExistsAndActiveDoesNot_CopiesBackup()
    {
        // Arrange: first boot, Litestream had nothing, DatabaseBackupService restores from Azure Files
        CreateSqliteDb(_backupDbPath, "backup_data");

        // Act
        await _service.RestoreFromBackupAsync(CancellationToken.None);

        // Assert
        Assert.IsTrue(File.Exists(_activeDbPath), "Active DB should be created from backup");
        var content = ReadSqliteData(_activeDbPath);
        Assert.AreEqual("backup_data", content);
    }

    [TestMethod]
    public async Task Restore_WhenNoBackupExists_DoesNothing()
    {
        // Arrange: brand new container, no data anywhere
        // Neither active nor backup DB exists

        // Act
        await _service.RestoreFromBackupAsync(CancellationToken.None);

        // Assert
        Assert.IsFalse(File.Exists(_activeDbPath), "No DB should be created when no backup exists");
    }

    [TestMethod]
    public async Task Restore_WhenBackupExistsAndImagesExist_CopiesImages()
    {
        // Arrange
        CreateSqliteDb(_backupDbPath, "data");
        Directory.CreateDirectory(_backupImagesPath);
        await File.WriteAllTextAsync(Path.Combine(_backupImagesPath, "logo.png"), "image_bytes");

        // Act
        await _service.RestoreFromBackupAsync(CancellationToken.None);

        // Assert
        Assert.IsTrue(File.Exists(Path.Combine(_activeImagesPath, "logo.png")));
    }

    [TestMethod]
    public async Task Restore_WhenBackupDbIsCorrupt_HandlesGracefully()
    {
        // Arrange: backup file exists but isn't a valid SQLite DB
        // RestoreFromBackupAsync does a raw file copy — it should still succeed
        await File.WriteAllTextAsync(_backupDbPath, "not_a_real_database");

        // Act
        await _service.RestoreFromBackupAsync(CancellationToken.None);

        // Assert: file is copied as-is (raw copy, no validation)
        Assert.IsTrue(File.Exists(_activeDbPath));
        var content = await File.ReadAllTextAsync(_activeDbPath);
        Assert.AreEqual("not_a_real_database", content);
    }

    // ── Backup Tests ──────────────────────────────────────────────────

    [TestMethod]
    public async Task Backup_WhenNoActiveDb_SkipsBackup()
    {
        // Arrange: no active DB

        // Act
        await _service.BackupToStorageAsync(CancellationToken.None);

        // Assert: no backup created
        Assert.IsFalse(File.Exists(_backupDbPath), "No backup should be created when no active DB exists");
    }

    [TestMethod]
    public async Task Backup_WhenActiveDbExists_CreatesBackup()
    {
        // Arrange
        CreateSqliteDb(_activeDbPath, "live_data");

        // Act
        await _service.BackupToStorageAsync(CancellationToken.None);

        // Assert
        Assert.IsTrue(File.Exists(_backupDbPath), "Backup should be created from active DB");
    }

    [TestMethod]
    public async Task Backup_WhenCalledTwiceWithNoChanges_SkipsSecondWrite()
    {
        // Arrange
        CreateSqliteDb(_activeDbPath, "stable_data");

        // Act — first backup
        await _service.BackupToStorageAsync(CancellationToken.None);
        Assert.IsTrue(File.Exists(_backupDbPath));
        var firstWriteTime = File.GetLastWriteTimeUtc(_backupDbPath);

        // Small delay to ensure timestamp would differ
        await Task.Delay(100);

        // Second backup (same data)
        await _service.BackupToStorageAsync(CancellationToken.None);

        // Assert: backup file was NOT re-written (hash match optimization)
        var secondWriteTime = File.GetLastWriteTimeUtc(_backupDbPath);
        Assert.AreEqual(firstWriteTime, secondWriteTime,
            "Backup should be skipped when DB hash is unchanged (cost optimization)");
    }

    [TestMethod]
    public async Task Backup_WhenDataChanges_WritesNewBackup()
    {
        // Arrange
        CreateSqliteDb(_activeDbPath, "original_data");

        // First backup
        await _service.BackupToStorageAsync(CancellationToken.None);
        Assert.IsTrue(File.Exists(_backupDbPath));
        var firstWriteTime = File.GetLastWriteTimeUtc(_backupDbPath);
        await Task.Delay(100);

        // Change the active DB
        InsertSqliteData(_activeDbPath, "new_data");

        // Second backup
        await _service.BackupToStorageAsync(CancellationToken.None);

        // Assert: backup was updated
        var secondWriteTime = File.GetLastWriteTimeUtc(_backupDbPath);
        Assert.AreNotEqual(firstWriteTime, secondWriteTime,
            "Backup should be updated when DB content changes");
    }

    [TestMethod]
    public async Task Backup_CleansUpTempFile()
    {
        // Arrange
        CreateSqliteDb(_activeDbPath, "data");

        // Act
        await _service.BackupToStorageAsync(CancellationToken.None);

        // Assert: temp backup file is cleaned up
        Assert.IsFalse(File.Exists(_tempBackupDbPath),
            "Temp backup file should be cleaned up after backup completes");
    }

    [TestMethod]
    public async Task Restore_WhenActiveDbExistsAndImagesExist_StillRestoresImages()
    {
        // Arrange: Litestream restored the DB, but images are only in Azure Files backup
        CreateSqliteDb(_activeDbPath, "litestream_data");
        CreateSqliteDb(_backupDbPath, "backup_data");
        Directory.CreateDirectory(_backupImagesPath);
        await File.WriteAllTextAsync(Path.Combine(_backupImagesPath, "feed-icon.png"), "image_bytes");

        // Act
        await _service.RestoreFromBackupAsync(CancellationToken.None);

        // Assert: DB is untouched (Litestream's data preserved)
        var content = ReadSqliteData(_activeDbPath);
        Assert.AreEqual("litestream_data", content,
            "Active DB must not be overwritten");

        // Assert: images ARE restored even though DB restore was skipped
        Assert.IsTrue(File.Exists(Path.Combine(_activeImagesPath, "feed-icon.png")),
            "Images must be restored even when DB restore is skipped (Litestream coexistence)");
    }

    // ── Full Lifecycle Tests (Litestream coexistence) ─────────────────

    [TestMethod]
    public async Task Lifecycle_LitestreamRestoredFirst_BackupServiceSkipsThenBacksUp()
    {
        // Simulates the Litestream coexistence scenario:
        // 1. Litestream restores DB before app starts (active DB exists)
        // 2. DatabaseBackupService.Restore skips (doesn't overwrite)
        // 3. DatabaseBackupService.Backup still runs (writes to Azure Files)

        // Step 1: Simulate Litestream restore
        CreateSqliteDb(_activeDbPath, "litestream_restored_data");

        // Step 2: DatabaseBackupService restore — should skip
        await _service.RestoreFromBackupAsync(CancellationToken.None);
        var contentAfterRestore = ReadSqliteData(_activeDbPath);
        Assert.AreEqual("litestream_restored_data", contentAfterRestore,
            "Restore must not overwrite Litestream's data");

        // Step 3: DatabaseBackupService backup — should still work
        await _service.BackupToStorageAsync(CancellationToken.None);
        Assert.IsTrue(File.Exists(_backupDbPath),
            "Backup should still run even when restore was skipped");
    }

    [TestMethod]
    public async Task Lifecycle_FirstBoot_RestoresFromBackupThenBacksUp()
    {
        // Simulates first boot after Litestream is added:
        // 1. Litestream restore is no-op (nothing in blob yet)
        // 2. DatabaseBackupService restores from Azure Files
        // 3. Subsequent backup works

        // Step 1: Litestream no-op (we don't create active DB)
        CreateSqliteDb(_backupDbPath, "azure_files_data");

        // Step 2: DatabaseBackupService restores
        await _service.RestoreFromBackupAsync(CancellationToken.None);
        Assert.IsTrue(File.Exists(_activeDbPath));
        var content = ReadSqliteData(_activeDbPath);
        Assert.AreEqual("azure_files_data", content);

        // Step 3: Mutate and backup
        InsertSqliteData(_activeDbPath, "new_entry");
        await _service.BackupToStorageAsync(CancellationToken.None);
        Assert.IsTrue(File.Exists(_backupDbPath));
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static void CreateSqliteDb(string path, string seedValue)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS test_data (value TEXT); INSERT INTO test_data VALUES ($v);";
        cmd.Parameters.AddWithValue("$v", seedValue);
        cmd.ExecuteNonQuery();
    }

    private static void InsertSqliteData(string path, string value)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO test_data VALUES ($v);";
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    private static string ReadSqliteData(string path)
    {
        using var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM test_data LIMIT 1;";
        var result = cmd.ExecuteScalar();
        return result?.ToString() ?? string.Empty;
    }
}
