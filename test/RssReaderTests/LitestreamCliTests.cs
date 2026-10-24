using RssReader.Server.Services;

namespace RssReaderTests;

/// <summary>BLOB-DURABILITY-SPEC.md 9.1, "Litestream process wrapper": the process is faked.</summary>
[TestClass]
public class LitestreamCliTests
{
    [TestMethod]
    public async Task Sync_ExitZero_IsOk()
    {
        string? seen = null;
        var cli = new LitestreamCli("litestream", (file, args, _) => { seen = file + " " + args; return Task.FromResult((0, "{\"synced\":true}")); });
        var (result, output) = await cli.SyncAsync("/tmp/storage.db", "/tmp/litestream.sock", TimeSpan.FromSeconds(20), CancellationToken.None);
        Assert.AreEqual(LitestreamSyncResult.Ok, result);
        StringAssert.Contains(output, "synced");
        Assert.AreEqual("litestream sync -wait -timeout 20 -socket \"/tmp/litestream.sock\" \"/tmp/storage.db\"", seen);
    }

    [TestMethod]
    public async Task Sync_NonZeroExit_IsFailed()
    {
        var cli = new LitestreamCli("litestream", (_, _, _) => Task.FromResult((1, "failed to connect to control socket")));
        var (result, output) = await cli.SyncAsync("/tmp/storage.db", "/tmp/litestream.sock", TimeSpan.FromSeconds(20), CancellationToken.None);
        Assert.AreEqual(LitestreamSyncResult.Failed, result);
        StringAssert.Contains(output, "control socket");
    }

    [TestMethod]
    public async Task Sync_HangingProcess_TimesOut()
    {
        var cli = new LitestreamCli("litestream", async (_, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return (0, ""); });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (result, _) = await cli.SyncAsync("/tmp/storage.db", "/tmp/litestream.sock", TimeSpan.FromMilliseconds(300), CancellationToken.None);
        Assert.AreEqual(LitestreamSyncResult.TimedOut, result);
        Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task Sync_CallerCancellation_Propagates()
    {
        var cli = new LitestreamCli("litestream", async (_, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return (0, ""); });
        using var cts = new CancellationTokenSource(100);
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() =>
            cli.SyncAsync("/tmp/storage.db", "/tmp/litestream.sock", TimeSpan.FromSeconds(30), cts.Token));
    }

    [TestMethod]
    public async Task Sync_TimeoutRoundsUpToWholeSeconds()
    {
        string? args = null;
        var cli = new LitestreamCli("litestream", (_, a, _) => { args = a; return Task.FromResult((0, "")); });
        await cli.SyncAsync("/db", "/sock", TimeSpan.FromMilliseconds(1500), CancellationToken.None);
        StringAssert.Contains(args, "-timeout 2 ");
    }
}
