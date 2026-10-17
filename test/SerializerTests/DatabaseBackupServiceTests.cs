using Moq;
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
        _activeImagesPath = Path.Combine(_testDir, "active", "images");
        _backupImagesPath = Path.Combine(_testDir, "backup", "images");

        Directory.CreateDirectory(Path.GetDirectoryName(_activeDbPath)!);

        _paths = new DatabaseBackupPaths(
            ActiveDbPath: _activeDbPath,
            ActiveImagesPath: _activeImagesPath,
            BackupImagesPath: _backupImagesPath);

        _service = new DatabaseBackupService(
            NullLogger<DatabaseBackupService>.Instance,
            _paths);
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
        catch (IOException)
        {
            // Best-effort cleanup; temp dir will be cleaned by OS
        }
    }

    // ── Image Restore Tests ───────────────────────────────────────────

    [TestMethod]
    public async Task Restore_WhenBackupImagesExist_CopiesImagesToActiveDir()
    {
        // Arrange: images exist in /data/images
        Directory.CreateDirectory(_backupImagesPath);
        await File.WriteAllTextAsync(Path.Combine(_backupImagesPath, "logo.png"), "image_bytes");

        // Act
        await _service.RestoreFromBackupAsync(CancellationToken.None);

        // Assert
        Assert.IsTrue(File.Exists(Path.Combine(_activeImagesPath, "logo.png")),
            "Cached images should be restored from /data on startup");
    }

    [TestMethod]
    public async Task Restore_WhenNoBackupImagesDir_DoesNotThrow()
    {
        // Arrange: no backup images dir at all (fresh container, fresh /data mount)

        // Act + Assert: should not throw
        await _service.RestoreFromBackupAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task Restore_DoesNotTouchActiveDatabase()
    {
        // Arrange: active DB exists (Litestream restored it). Restore must not modify it.
        CreateSqliteDb(_activeDbPath, "litestream_data");
        var beforeWriteTime = File.GetLastWriteTimeUtc(_activeDbPath);
        await Task.Delay(50);

        // Act
        await _service.RestoreFromBackupAsync(CancellationToken.None);

        // Assert: DB file unchanged (Litestream owns the DB; this service only handles images)
        var afterWriteTime = File.GetLastWriteTimeUtc(_activeDbPath);
        Assert.AreEqual(beforeWriteTime, afterWriteTime,
            "RestoreFromBackupAsync must never touch the active DB — Litestream owns DB restore");
        var content = ReadSqliteData(_activeDbPath);
        Assert.AreEqual("litestream_data", content);
    }

    // ── Favicon write-through (replaces the periodic image sync loop) ──────

    [TestMethod]
    public async Task FaviconPersist_CopiesNewIconToBackupDir()
    {
        Directory.CreateDirectory(_activeImagesPath);
        Directory.CreateDirectory(Path.GetDirectoryName(_backupImagesPath.TrimEnd('/', '\\')));
        var local = Path.Combine(_activeImagesPath, "example.com.ico");
        await File.WriteAllTextAsync(local, "icon-bytes");

        var svc = new FaviconService(
            new Mock<IHttpClientFactory>().Object,
            NullLogger<FaviconService>.Instance,
            new DatabaseBackupPaths(_activeDbPath, _activeImagesPath, _backupImagesPath));

        await svc.PersistToBackupAsync(local, "example.com.ico");

        Assert.AreEqual("icon-bytes", await File.ReadAllTextAsync(Path.Combine(_backupImagesPath, "example.com.ico")),
            "A freshly cached favicon must be written through to /data immediately");
        Assert.AreEqual(0, Directory.GetFiles(_backupImagesPath, "*.tmp").Length, "No temp files left behind");
    }

    [TestMethod]
    public async Task FaviconPersist_DoesNotOverwriteExistingBackupFile()
    {
        Directory.CreateDirectory(_activeImagesPath);
        Directory.CreateDirectory(_backupImagesPath);
        var local = Path.Combine(_activeImagesPath, "shared.ico");
        await File.WriteAllTextAsync(local, "active_version");
        await File.WriteAllTextAsync(Path.Combine(_backupImagesPath, "shared.ico"), "backup_version");

        var svc = new FaviconService(
            new Mock<IHttpClientFactory>().Object,
            NullLogger<FaviconService>.Instance,
            new DatabaseBackupPaths(_activeDbPath, _activeImagesPath, _backupImagesPath));

        await svc.PersistToBackupAsync(local, "shared.ico");

        Assert.AreEqual("backup_version", await File.ReadAllTextAsync(Path.Combine(_backupImagesPath, "shared.ico")));
    }

    [TestMethod]
    public async Task FaviconPersist_WhenMountMissing_DoesNothing()
    {
        Directory.CreateDirectory(_activeImagesPath);
        var local = Path.Combine(_activeImagesPath, "x.ico");
        await File.WriteAllTextAsync(local, "bytes");
        var missingMount = Path.Combine(_testDir, "no-such-mount", "images") + Path.DirectorySeparatorChar;

        var svc = new FaviconService(
            new Mock<IHttpClientFactory>().Object,
            NullLogger<FaviconService>.Instance,
            new DatabaseBackupPaths(_activeDbPath, _activeImagesPath, missingMount));

        await svc.PersistToBackupAsync(local, "x.ico");

        Assert.IsFalse(Directory.Exists(missingMount), "Must not create the mount directory when it is absent");
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
