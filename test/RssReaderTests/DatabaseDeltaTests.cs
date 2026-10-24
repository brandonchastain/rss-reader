using Microsoft.Data.Sqlite;
using RssReader.Server.Services;

namespace RssReaderTests;

/// <summary>The shutdown delta format on its own: write against a base, apply to a copy of it.</summary>
[TestClass]
public class DatabaseDeltaTests
{
    private string _dir = null!;
    private string _live = null!;
    private string _base = null!;
    private string _delta = null!;

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "DatabaseDeltaTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _live = Path.Combine(_dir, "live.db");
        _base = Path.Combine(_dir, "base.db");
        _delta = Path.Combine(_dir, "live.db.delta");
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static void Exec(string path, string sql)
    {
        using var conn = new SqliteConnection($"Data Source={path};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long Count(string path)
    {
        using var conn = new SqliteConnection($"Data Source={path};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM t;";
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>A WAL-mode database of a few hundred pages, checkpointed, with a byte copy as the base.</summary>
    private async Task<BlockManifest> CreateLiveAndBase(int rows = 2_000)
    {
        Exec(_live, "PRAGMA journal_mode=WAL; CREATE TABLE t (id INTEGER PRIMARY KEY, body TEXT);");
        Exec(_live, "WITH RECURSIVE s(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM s WHERE i < " + rows + ") " +
                    "INSERT INTO t SELECT i, printf('%0500d', i) FROM s; PRAGMA wal_checkpoint(TRUNCATE);");
        File.Copy(_live, _base);
        return await BlockManifest.HashFileAsync(_base, 4096, CancellationToken.None);
    }

    private async Task<DatabaseDelta.WriteResult> WriteDelta(BlockManifest manifest)
    {
        using var dst = new FileStream(_delta, FileMode.Create);
        return await DatabaseDelta.WriteAsync(_live, _live + "-wal", manifest, dst, CancellationToken.None);
    }

    private async Task<DatabaseDelta.ApplyResult> ApplyToBase(BlockManifest baseManifest, string? deltaPath = null)
    {
        var copy = await BlockManifest.HashFileAsync(_base, 4096, CancellationToken.None);
        return await DatabaseDelta.ApplyAsync(deltaPath ?? _delta, copy.Length, copy.FileHash, _base, _base + "-wal", CancellationToken.None);
    }

    [TestMethod]
    public async Task RoundTrip_ChangedPagesOnly_RebuildsTheLiveFile()
    {
        var manifest = await CreateLiveAndBase();
        Exec(_live, "INSERT INTO t VALUES (NULL, 'late'); PRAGMA wal_checkpoint(TRUNCATE);");

        var written = await WriteDelta(manifest);
        var applied = await ApplyToBase(manifest);

        Assert.AreEqual(DatabaseDelta.ApplyOutcome.Applied, applied.Outcome);
        Assert.IsTrue(written.ChangedBlocks < written.TotalBlocks / 4, $"{written.ChangedBlocks} of {written.TotalBlocks} pages changed");
        Assert.AreEqual(0, written.WalBytes, "WAL was checkpointed, nothing to ship");
        CollectionAssert.AreEqual(File.ReadAllBytes(_live), File.ReadAllBytes(_base), "Base + delta is byte-identical to the live file");
        Assert.AreEqual(2_001, Count(_base));
    }

    [TestMethod]
    public async Task RoundTrip_UncheckpointedWrites_TravelInTheWal()
    {
        var manifest = await CreateLiveAndBase();
        // No checkpoint: the new row exists only as WAL frames. An open connection keeps
        // SQLite from folding and deleting the WAL when the writing connection closes.
        using var holder = new SqliteConnection($"Data Source={_live};Pooling=False");
        holder.Open();
        // Only a connection that has read the database has the WAL open (its -shm lock).
        using (var touch = holder.CreateCommand()) { touch.CommandText = "SELECT count(*) FROM t;"; touch.ExecuteScalar(); }
        Exec(_live, "INSERT INTO t VALUES (NULL, 'in-wal');");
        Assert.IsTrue(DatabaseBackupToFileService.WalHoldsFrames(_live));

        var written = await WriteDelta(manifest);
        var applied = await ApplyToBase(manifest);

        Assert.IsTrue(written.WalBytes > 32);
        Assert.AreEqual(DatabaseDelta.ApplyOutcome.Applied, applied.Outcome);
        Assert.IsTrue(File.Exists(_base + "-wal"), "Shipped WAL placed next to the target");
        Assert.AreEqual(2_001, Count(_base), "SQLite replays the shipped WAL on open");
    }

    [TestMethod]
    public async Task RoundTrip_FileThatShrank_IsTruncated()
    {
        var manifest = await CreateLiveAndBase();
        Exec(_live, "DELETE FROM t WHERE id > 100; VACUUM; PRAGMA wal_checkpoint(TRUNCATE);");
        Assert.IsTrue(new FileInfo(_live).Length < manifest.Length);

        await WriteDelta(manifest);
        var applied = await ApplyToBase(manifest);

        Assert.AreEqual(DatabaseDelta.ApplyOutcome.Applied, applied.Outcome);
        CollectionAssert.AreEqual(File.ReadAllBytes(_live), File.ReadAllBytes(_base));
        Assert.AreEqual(100, Count(_base));
    }

    [TestMethod]
    public async Task Apply_RefusesADeltaForAnotherBase()
    {
        var manifest = await CreateLiveAndBase();
        Exec(_live, "INSERT INTO t VALUES (NULL, 'late'); PRAGMA wal_checkpoint(TRUNCATE);");
        await WriteDelta(manifest);
        // The base moved on (a later periodic copy).
        Exec(_base, "INSERT INTO t VALUES (NULL, 'newer base'); PRAGMA wal_checkpoint(TRUNCATE);");
        File.Delete(_base + "-wal");
        var before = File.ReadAllBytes(_base);

        var applied = await ApplyToBase(manifest);

        Assert.AreEqual(DatabaseDelta.ApplyOutcome.BaseMismatch, applied.Outcome);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(_base), "Target untouched");
    }

    [TestMethod]
    public async Task Apply_RefusesATruncatedDelta_BeforeTouchingTheTarget()
    {
        var manifest = await CreateLiveAndBase();
        Exec(_live, "INSERT INTO t VALUES (NULL, 'late'); PRAGMA wal_checkpoint(TRUNCATE);");
        await WriteDelta(manifest);
        using (var f = new FileStream(_delta, FileMode.Open)) f.SetLength(f.Length - 1);
        var before = File.ReadAllBytes(_base);

        var applied = await ApplyToBase(manifest);

        Assert.AreEqual(DatabaseDelta.ApplyOutcome.Corrupt, applied.Outcome);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(_base), "Target untouched");
    }

    [TestMethod]
    public async Task Apply_RefusesAFlippedByte()
    {
        var manifest = await CreateLiveAndBase();
        Exec(_live, "INSERT INTO t VALUES (NULL, 'late'); PRAGMA wal_checkpoint(TRUNCATE);");
        await WriteDelta(manifest);
        var bytes = File.ReadAllBytes(_delta);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(_delta, bytes);

        var applied = await ApplyToBase(manifest);

        Assert.AreEqual(DatabaseDelta.ApplyOutcome.Corrupt, applied.Outcome);
    }
}
