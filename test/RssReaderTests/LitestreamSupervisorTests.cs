using Microsoft.Extensions.Logging.Abstractions;
using RssApp.Config;
using RssReader.Server.Services;

namespace RssReaderTests;

[TestClass]
public class LitestreamSupervisorTests
{
    /// <summary>
    /// Program.cs registers the supervisor as a singleton and again as a hosted service, so
    /// DI disposes the same instance twice at shutdown. The second call must not throw
    /// (it used to: ObjectDisposedException from CancelAsync on the disposed CTS).
    /// </summary>
    [TestMethod]
    public async Task DisposeAsync_CalledTwice_DoesNotThrow()
    {
        var supervisor = new LitestreamSupervisor(new DurabilityConfig(), "/tmp/storage.db", NullLogger<LitestreamSupervisor>.Instance);
        await supervisor.StartAsync(CancellationToken.None);
        await supervisor.StopAsync(CancellationToken.None);

        await supervisor.DisposeAsync();
        await supervisor.DisposeAsync();
    }

    [TestMethod]
    public void ParseSyncTxid_ReadsReplicaTxidFromLitestreamJson()
    {
        // litestream 0.5.17: sync -wait -json
        const string json = "{\n  \"db_path\": \"/tmp/storage.db\",\n  \"txid\": 2,\n  \"replica_txid\": 2,\n  \"duration_ms\": 38\n}";
        Assert.AreEqual(2L, LitestreamCli.ParseSyncTxid(json));
        Assert.AreEqual(7L, LitestreamCli.ParseSyncTxid("{\"txid\": 7}"));
        Assert.IsNull(LitestreamCli.ParseSyncTxid("sync failed"));
    }

    [TestMethod]
    public async Task SyncAsync_PassesJsonFlagAndReturnsTxid()
    {
        string? seen = null;
        var cli = new LitestreamCli("litestream", (f, a, ct) => { seen = a; return Task.FromResult((0, "{\"txid\": 5, \"replica_txid\": 5}")); });
        var (result, _, txid) = await cli.SyncAsync("/tmp/storage.db", "/tmp/ls.sock", TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.AreEqual(LitestreamSyncResult.Ok, result);
        Assert.AreEqual(5L, txid);
        StringAssert.Contains(seen, "-json");
        StringAssert.StartsWith(seen, "sync -wait -timeout 5 -socket");
    }
}
