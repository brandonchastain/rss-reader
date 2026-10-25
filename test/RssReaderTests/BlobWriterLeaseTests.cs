using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging.Abstractions;
using RssApp.Config;
using RssReader.Server.Services;

namespace RssReaderTests;

/// <summary>
/// BLOB-DURABILITY-SPEC.md 9.1, "Lease wrapper", against Azurite. The connection string
/// comes from RSSREADER_TEST_AZURITE_CONNECTION (set in ci.yml, where Azurite is a service
/// container); without it the tests are inconclusive rather than failing, so a plain
/// `dotnet test` on a laptop still passes. Locally:
///   docker run -d -p 10000:10000 mcr.microsoft.com/azure-storage/azurite azurite-blob --blobHost 0.0.0.0
///   $env:RSSREADER_TEST_AZURITE_CONNECTION = "UseDevelopmentStorage=true"
/// Lease durations are the real Azure minimum (15 s), so the expiry test takes about 20 s.
/// </summary>
[TestClass]
[TestCategory("Azurite")]
public class BlobWriterLeaseTests
{
    private static string? _connection;
    private static BlobContainerClient _container = null!;
    private readonly List<BlobWriterLease> _leases = new();

    [ClassInitialize]
    public static async Task ClassInit(TestContext _)
    {
        _connection = Environment.GetEnvironmentVariable("RSSREADER_TEST_AZURITE_CONNECTION");
        if (string.IsNullOrWhiteSpace(_connection)) return;
        _container = new BlobContainerClient(_connection, "lease-tests-" + Guid.NewGuid().ToString("N")[..8]);
        await _container.CreateIfNotExistsAsync();
    }

    [ClassCleanup]
    public static async Task ClassCleanup()
    {
        if (_connection != null) await _container.DeleteIfExistsAsync();
    }

    [TestInitialize]
    public void RequireAzurite()
    {
        if (string.IsNullOrWhiteSpace(_connection))
            Assert.Inconclusive("RSSREADER_TEST_AZURITE_CONNECTION is not set; Azurite is not available.");
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        foreach (var l in _leases) await l.DisposeAsync();
    }

    private BlobWriterLease NewLease(string? blob = null, TimeSpan? duration = null, TimeSpan? renew = null)
    {
        var l = new BlobWriterLease(
            _container, blob ?? TestContext!.TestName!, duration ?? TimeSpan.FromSeconds(15), renew ?? TimeSpan.FromSeconds(5),
            NullLogger.Instance)
        {
            PollInterval = TimeSpan.FromMilliseconds(100),
            GiveUpAfter = TimeSpan.FromSeconds(2),
        };
        _leases.Add(l);
        return l;
    }

    public TestContext? TestContext { get; set; }

    [TestMethod]
    public async Task Acquire_WhenFree_Holds()
    {
        var l = NewLease();
        var (acquired, error) = await l.TryAcquireAsync(CancellationToken.None);
        Assert.IsTrue(acquired, error?.ToString());
        Assert.IsTrue(l.IsHeld);
        Assert.IsNotNull(l.LeaseId);
    }

    [TestMethod]
    public async Task Acquire_WhenHeldByAnother_IsRefusedNotErrored()
    {
        var a = NewLease();
        var b = NewLease();
        Assert.IsTrue((await a.TryAcquireAsync(CancellationToken.None)).Acquired);
        var (acquired, error) = await b.TryAcquireAsync(CancellationToken.None);
        Assert.IsFalse(acquired);
        Assert.IsNull(error, "contention is not an error");
        Assert.IsFalse(b.IsHeld);
    }

    [TestMethod]
    public async Task AcquireAsync_WaitsForRelease()
    {
        var a = NewLease();
        var b = NewLease();
        Assert.IsTrue(await a.AcquireAsync(CancellationToken.None));
        var waiting = b.AcquireAsync(CancellationToken.None);
        await Task.Delay(500);
        Assert.IsFalse(waiting.IsCompleted, "b must wait while a holds the lease");
        await a.ReleaseAsync();
        Assert.IsTrue(await waiting.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.IsTrue(b.IsHeld);
        Assert.IsFalse(a.IsHeld);
    }

    [TestMethod]
    public async Task Renew_KeepsTheLease()
    {
        var a = NewLease();
        var b = NewLease();
        Assert.IsTrue(await a.AcquireAsync(CancellationToken.None));
        // Past the 15 s duration, renewing every 5 s: still held, b still refused.
        await Task.Delay(TimeSpan.FromSeconds(17));
        Assert.IsTrue(a.IsHeld);
        Assert.IsFalse(a.IsLost);
        Assert.IsFalse((await b.TryAcquireAsync(CancellationToken.None)).Acquired);
    }

    [TestMethod]
    public async Task Expiry_FreesTheLeaseWithoutRelease()
    {
        // A holder that stops renewing (a crashed writer) loses the lease after the duration:
        // taken with the raw client so nothing renews it.
        var blob = _container.GetBlobClient(TestContext!.TestName!);
        await blob.UploadAsync(BinaryData.FromString(string.Empty), overwrite: true);
        await new Azure.Storage.Blobs.Specialized.BlobLeaseClient(blob).AcquireAsync(TimeSpan.FromSeconds(15));
        var b = NewLease();
        Assert.IsFalse((await b.TryAcquireAsync(CancellationToken.None)).Acquired);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.IsTrue(await b.AcquireAsync(new CancellationTokenSource(TimeSpan.FromSeconds(40)).Token));
        Assert.IsTrue(sw.Elapsed >= TimeSpan.FromSeconds(10), $"took the lease too early: {sw.Elapsed}");
        Assert.IsTrue(sw.Elapsed <= TimeSpan.FromSeconds(30), $"took too long: {sw.Elapsed}");
    }

    [TestMethod]
    public async Task Release_IsIdempotentAndFreesIt()
    {
        var a = NewLease();
        Assert.IsTrue(await a.AcquireAsync(CancellationToken.None));
        await a.ReleaseAsync();
        await a.ReleaseAsync();
        Assert.IsFalse(a.IsHeld);
        var b = NewLease();
        Assert.IsTrue((await b.TryAcquireAsync(CancellationToken.None)).Acquired);
    }

    [TestMethod]
    public async Task Release_StampsMetadata()
    {
        var a = NewLease();
        Assert.IsTrue(await a.AcquireAsync(CancellationToken.None));
        await a.ReleaseAsync(new Dictionary<string, string> { ["txid"] = "00000000000000ff" });
        var props = await _container.GetBlobClient(TestContext!.TestName!).GetPropertiesAsync();
        Assert.AreEqual("00000000000000ff", props.Value.Metadata["txid"]);
    }

    [TestMethod]
    public async Task RenewFailure_LongerThanDuration_ReportsLost()
    {
        var a = NewLease(renew: TimeSpan.FromSeconds(1));
        Assert.IsTrue(await a.AcquireAsync(CancellationToken.None));
        // Break the lease from outside: another client takes it by force (Azure's "break" API),
        // which is what a lease that expired and was re-acquired by a new writer looks like.
        var leaseClient = new Azure.Storage.Blobs.Specialized.BlobLeaseClient(_container.GetBlobClient(TestContext!.TestName!));
        await leaseClient.BreakAsync(TimeSpan.Zero);
        var usurper = NewLease(duration: TimeSpan.FromSeconds(60), renew: TimeSpan.FromSeconds(20));
        Assert.IsTrue((await usurper.TryAcquireAsync(CancellationToken.None)).Acquired);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(40);
        while (!a.IsLost && DateTime.UtcNow < deadline) await Task.Delay(250);
        Assert.IsTrue(a.IsLost, "renewal kept failing for longer than the duration, the lease must be reported lost");
    }

    [TestMethod]
    public async Task Create_ReturnsNullWhenNotConfigured()
    {
        Assert.IsNull(BlobWriterLease.Create(new DurabilityConfig(), NullLogger.Instance));
        var configured = BlobWriterLease.Create(new DurabilityConfig { ConnectionString = _connection!, LeaseContainer = _container.Name }, NullLogger.Instance);
        Assert.IsNotNull(configured);
        await configured.DisposeAsync();
    }

    [TestMethod]
    public async Task UnreachableService_GivesUpAfterTimeout()
    {
        // NOT A SECRET: Azurite's published well-known test key for devstoreaccount1, pointed at a
        // closed port so the service is unreachable.
        var bogus = new BlobContainerClient("DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://127.0.0.1:1/devstoreaccount1;", "x");
        var l = new BlobWriterLease(bogus, "lease", TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(5), NullLogger.Instance)
        {
            PollInterval = TimeSpan.FromMilliseconds(50),
            GiveUpAfter = TimeSpan.FromSeconds(1),
        };
        _leases.Add(l);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.IsFalse(await l.AcquireAsync(new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token));
        Assert.IsFalse(l.IsHeld);
        Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(45), $"gave up only after {sw.Elapsed}");
    }
}
