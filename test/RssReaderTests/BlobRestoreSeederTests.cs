using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RssApp.Config;
using RssReader.Server.Services;

namespace RssReaderTests;

/// <summary>
/// Rollout step 3 restore path (BLOB-DURABILITY-SPEC.md 3.2 steps 4 and 5) with the
/// <c>litestream</c> process faked: the runner writes whatever the test wants at the
/// <c>-o</c> path and returns the exit code the test chooses.
/// </summary>
[TestClass]
public class BlobRestoreSeederTests
{
    private string _dir = null!;
    private string _active = null!;
    private DurabilityConfig _config = null!;

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "BlobRestoreSeederTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _active = Path.Combine(_dir, "live", "storage.db");
        _config = new DurabilityConfig
        {
            LitestreamEnabled = true,
            LitestreamConfigPath = "/etc/litestream.yml",
            RestoreTimeout = TimeSpan.FromSeconds(5),
        };
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static string OutputPathOf(string arguments) =>
        Regex.Match(arguments, "-o \"([^\"]+)\"").Groups[1].Value;

    private static void CreateDb(string path, string marker)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE TABLE t (v TEXT); INSERT INTO t VALUES ('{marker}');";
        cmd.ExecuteNonQuery();
    }

    private static string ReadMarker(string path)
    {
        using var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT v FROM t LIMIT 1";
        return (string)cmd.ExecuteScalar()!;
    }

    private Task<bool> Restore(LitestreamCli.Runner runner) =>
        BlobRestoreSeeder.RestoreFromReplicaAsync(
            _config, _active, new LitestreamCli("litestream", runner), NullLogger.Instance, CancellationToken.None);

    [TestMethod]
    public async Task Restore_WritesStagedFileThenRenamesOverLive_DropsStaleSidecars()
    {
        CreateDb(_active, "old-live");
        File.WriteAllText(_active + "-wal", "stale");
        File.WriteAllText(_active + "-shm", "stale");
        string? seenArgs = null;

        var ok = await Restore((file, args, ct) =>
        {
            seenArgs = args;
            CreateDb(OutputPathOf(args), "from-replica");
            return Task.FromResult((0, "restored"));
        });

        Assert.IsTrue(ok);
        Assert.AreEqual("from-replica", ReadMarker(_active));
        Assert.IsFalse(File.Exists(_active + "-wal"), "stale WAL must not be replayed onto the restored file");
        Assert.IsFalse(File.Exists(_active + "-shm"));
        Assert.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(_active)!, "storage.db.restore.*").Length, "no staging file left behind");
        StringAssert.StartsWith(seenArgs, "restore -config \"/etc/litestream.yml\" -o \"");
        StringAssert.EndsWith(seenArgs, $"\" \"{_active}\"");
        Assert.AreNotEqual(_active, OutputPathOf(seenArgs!), "restore must go to a staging path, not the live path");
    }

    [TestMethod]
    public async Task Restore_CreatesMissingLiveDirectory()
    {
        Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(_active)));
        var ok = await Restore((file, args, ct) => { CreateDb(OutputPathOf(args), "x"); return Task.FromResult((0, "")); });
        Assert.IsTrue(ok);
        Assert.IsTrue(File.Exists(_active));
    }

    [TestMethod]
    public async Task Restore_NonZeroExit_ReturnsFalseAndLeavesLiveDbAlone()
    {
        CreateDb(_active, "old-live");
        var ok = await Restore((file, args, ct) => Task.FromResult((1, "cannot find snapshot")));
        Assert.IsFalse(ok);
        Assert.AreEqual("old-live", ReadMarker(_active));
    }

    [TestMethod]
    public async Task Restore_ExitZeroButNoFile_ReturnsFalse()
    {
        var ok = await Restore((file, args, ct) => Task.FromResult((0, "no replica exists")));
        Assert.IsFalse(ok);
        Assert.IsFalse(File.Exists(_active));
    }

    [TestMethod]
    public async Task Restore_CorruptResult_FailsQuickCheck_ReturnsFalseAndCleansStage()
    {
        CreateDb(_active, "old-live");
        var ok = await Restore((file, args, ct) =>
        {
            File.WriteAllText(OutputPathOf(args), "this is not a database");
            return Task.FromResult((0, ""));
        });
        Assert.IsFalse(ok);
        Assert.AreEqual("old-live", ReadMarker(_active));
        Assert.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(_active)!, "storage.db.restore.*").Length);
    }

    [TestMethod]
    public async Task Restore_Timeout_ReturnsFalse()
    {
        _config.RestoreTimeout = TimeSpan.FromMilliseconds(100);
        var ok = await Restore(async (file, args, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return (0, "");
        });
        Assert.IsFalse(ok);
    }

    [TestMethod]
    public async Task Restore_RunnerThrows_ReturnsFalse()
    {
        var ok = await Restore((file, args, ct) => throw new InvalidOperationException("litestream binary missing"));
        Assert.IsFalse(ok);
    }

    [TestMethod]
    public async Task Restore_CallerCancelled_Propagates()
    {
        using var cts = new CancellationTokenSource();
        var task = BlobRestoreSeeder.RestoreFromReplicaAsync(
            _config, _active,
            new LitestreamCli("litestream", async (file, args, ct) => { await Task.Delay(TimeSpan.FromSeconds(10), ct); return (0, ""); }),
            NullLogger.Instance, cts.Token);
        cts.Cancel();
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => task);
    }

    [TestMethod]
    public void RestoreEnabled_RequiresFlagReplicationAndLease()
    {
        Assert.IsFalse(new DurabilityConfig { RestoreFromBlob = true }.RestoreEnabled);
        Assert.IsFalse(new DurabilityConfig { RestoreFromBlob = true, LitestreamEnabled = true }.RestoreEnabled);
        Assert.IsFalse(new DurabilityConfig { LitestreamEnabled = true, BlobServiceUri = "https://x.blob.core.windows.net" }.RestoreEnabled, "default off");
        Assert.IsTrue(new DurabilityConfig { RestoreFromBlob = true, LitestreamEnabled = true, BlobServiceUri = "https://x.blob.core.windows.net" }.RestoreEnabled);
    }
}
