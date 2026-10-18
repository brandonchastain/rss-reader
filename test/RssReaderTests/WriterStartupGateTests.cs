using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RssReader.Server.Services;

namespace RssReaderTests;

[TestClass]
public class WriterStartupGateTests
{
    private static readonly string[] Origins = { "http://localhost:4280" };
    private string _dir = null!;
    private DatabaseBackupToFilePaths _paths = null!;
    private ShareWriterLock _lock = null!;

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "WriterStartupGateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_dir, "share"));
        Directory.CreateDirectory(Path.Combine(_dir, "live"));
        _paths = new DatabaseBackupToFilePaths(
            ActiveDbPath: Path.Combine(_dir, "live", "storage.db"),
            BackupDbPath: Path.Combine(_dir, "share", "storage.db"),
            Interval: TimeSpan.FromMinutes(5),
            LockStaleThreshold: TimeSpan.FromSeconds(30),
            LockHeartbeatInterval: TimeSpan.FromSeconds(30));
        _lock = new ShareWriterLock(_paths.LockPath, _paths.LockStaleThreshold, _paths.HeartbeatInterval, NullLogger.Instance)
        { PollInterval = TimeSpan.FromMilliseconds(20) };
    }

    [TestCleanup]
    public void Cleanup()
    {
        _lock.Release();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static bool PortAccepts(int port)
    {
        try
        {
            using var c = new TcpClient();
            c.Connect(IPAddress.Loopback, port);
            return true;
        }
        catch (SocketException) { return false; }
    }

    [TestMethod]
    public async Task Gate_AnswersHealthzWith200()
    {
        await using var gate = await WriterStartupGate.StartAsync(Array.Empty<string>(), Origins, "http://127.0.0.1:0", CancellationToken.None);
        using var http = new HttpClient { BaseAddress = new Uri(gate.Addresses.Single()) };

        var res = await http.GetAsync("/api/healthz");

        Assert.AreEqual(HttpStatusCode.OK, res.StatusCode);
        StringAssert.Contains(await res.Content.ReadAsStringAsync(), "seeding");
        await gate.StopAsync();
    }

    [TestMethod]
    public async Task Gate_AnswersOtherPathsWith503RetryAfterAndCorsForAllowedOrigin()
    {
        await using var gate = await WriterStartupGate.StartAsync(Array.Empty<string>(), Origins, "http://127.0.0.1:0", CancellationToken.None);
        using var http = new HttpClient { BaseAddress = new Uri(gate.Addresses.Single()) };

        foreach (var path in new[] { "/api/feeds", "/auth/me", "/" })
        {
            var req = new HttpRequestMessage(HttpMethod.Get, path);
            req.Headers.Add("Origin", "http://localhost:4280");
            var res = await http.SendAsync(req);

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, res.StatusCode, path);
            Assert.AreEqual("5", res.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ToString(), path);
            Assert.AreEqual("http://localhost:4280", res.Headers.GetValues("Access-Control-Allow-Origin").Single(), path);
            Assert.AreEqual("true", res.Headers.GetValues("Access-Control-Allow-Credentials").Single(), path);
            Assert.AreEqual("Origin", res.Headers.Vary.Single(), path);
        }
        await gate.StopAsync();
    }

    [TestMethod]
    public async Task Gate_OmitsCorsHeadersForUnknownOrigin()
    {
        await using var gate = await WriterStartupGate.StartAsync(Array.Empty<string>(), Origins, "http://127.0.0.1:0", CancellationToken.None);
        using var http = new HttpClient { BaseAddress = new Uri(gate.Addresses.Single()) };

        var req = new HttpRequestMessage(HttpMethod.Get, "/api/feeds");
        req.Headers.Add("Origin", "https://evil.example");
        var res = await http.SendAsync(req);

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        Assert.IsFalse(res.Headers.Contains("Access-Control-Allow-Origin"));
        await gate.StopAsync();
    }

    [TestMethod]
    public async Task Gate_StopFreesThePortForTheRealHost()
    {
        var gate = await WriterStartupGate.StartAsync(Array.Empty<string>(), Origins, "http://127.0.0.1:0", CancellationToken.None);
        var port = new Uri(gate.Addresses.Single()).Port;
        Assert.IsTrue(PortAccepts(port));

        await gate.StopAsync();
        await gate.DisposeAsync();

        Assert.IsFalse(PortAccepts(port), "Gate no longer listening");
        // The real host must be able to bind the same port right after.
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        listener.Stop();
    }

    [TestMethod]
    public async Task RunAsync_ServesProbeWhileWaitingThenSeedsAndStops()
    {
        SeedSqliteDb(_paths.BackupDbPath, "from-share");
        SeedSqliteDb(_paths.ActiveDbPath, "stale-live");
        File.WriteAllText(_paths.LockPath, "old-revision");   // fresh: alive
        var port = FreePort();
        var url = $"http://127.0.0.1:{port}";

        var run = WriterStartupGate.RunAsync(
            Array.Empty<string>(), _paths, _lock, Origins, NullLoggerFactory.Instance, url);

        // Gate is up: probe passes while the seed is blocked on the old revision's lock.
        using var http = new HttpClient { BaseAddress = new Uri(url) };
        HttpResponseMessage? probe = null;
        for (var i = 0; i < 100 && probe == null; i++)
        {
            try { probe = await http.GetAsync("/api/healthz"); } catch (HttpRequestException) { await Task.Delay(50); }
        }
        Assert.IsNotNull(probe, "Gate came up");
        Assert.AreEqual(HttpStatusCode.OK, probe!.StatusCode);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, (await http.GetAsync("/api/feeds")).StatusCode);
        Assert.IsFalse(run.IsCompleted);
        Assert.AreEqual("stale-live", ReadMarker(_paths.ActiveDbPath), "Live DB untouched while waiting");

        // Old revision releases after its final copy.
        File.Delete(_paths.LockPath);
        await run.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.IsTrue(_lock.IsHeld, "New revision holds the lock");
        Assert.AreEqual("from-share", ReadMarker(_paths.ActiveDbPath), "Seeded from the share copy");
        Assert.IsFalse(PortAccepts(port), "Gate stopped and freed the port");
    }

    [TestMethod]
    public async Task RunAsync_CancelledWhileWaiting_ReleasesAndStops()
    {
        File.WriteAllText(_paths.LockPath, "old-revision");
        var port = FreePort();
        using var cts = new CancellationTokenSource();

        var run = WriterStartupGate.RunAsync(
            Array.Empty<string>(), _paths, _lock, Origins, NullLoggerFactory.Instance,
            $"http://127.0.0.1:{port}", cts.Token);

        // Cancel only once the gate is observably listening (i.e. it is in the
        // wait-for-lock phase), not on a wall-clock timer that can fire while the
        // listener is still starting under a loaded test runner.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!PortAccepts(port) && !run.IsCompleted && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
        Assert.IsTrue(PortAccepts(port), "gate should be listening before it is cancelled");
        cts.Cancel();

        try
        {
            await run;
            Assert.Fail("RunAsync should surface the cancellation");
        }
        catch (OperationCanceledException)
        {
            // TaskCanceledException or its base: either is the documented outcome.
        }

        Assert.IsFalse(_lock.IsHeld);
        Assert.IsTrue(File.Exists(_paths.LockPath), "Foreign lock untouched");
        Assert.IsFalse(PortAccepts(port));
    }

    [TestMethod]
    public async Task RunAsync_CorruptShareCopy_FailsBootAndFreesPortAndLock()
    {
        File.WriteAllText(_paths.BackupDbPath, "garbage");
        var port = FreePort();

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => WriterStartupGate.RunAsync(
            Array.Empty<string>(), _paths, _lock, Origins, NullLoggerFactory.Instance, $"http://127.0.0.1:{port}"));

        Assert.IsFalse(_lock.IsHeld);
        Assert.IsFalse(File.Exists(_paths.LockPath));
        Assert.IsFalse(PortAccepts(port));
    }

    // ── Helpers ────────────────────────────────────────────────────────

    internal static void SeedSqliteDb(string path, string marker)
    {
        using var conn = new SqliteConnection($"Data Source={path};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS marker (v TEXT); DELETE FROM marker; INSERT INTO marker VALUES ($v);";
        cmd.Parameters.AddWithValue("$v", marker);
        cmd.ExecuteNonQuery();
    }

    internal static string ReadMarker(string path)
    {
        using var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT v FROM marker";
        return (string)cmd.ExecuteScalar()!;
    }
}
