using Azure.Extensions.AspNetCore.DataProtection.Blobs;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Server.Authentication;

namespace RssReaderTests;

/// <summary>
/// BLOB-DURABILITY-SPEC.md 3.6 / rollout step 4: the share's key files move into the blob key
/// ring without signing anyone out. Runs against Azurite like <see cref="BlobWriterLeaseTests"/>
/// and is inconclusive without RSSREADER_TEST_AZURITE_CONNECTION.
/// </summary>
[TestClass]
[TestCategory("Azurite")]
public class DataProtectionKeyStoreTests
{
    private static string? _connection;
    private static BlobContainerClient _container = null!;
    private string _keyDir = null!;

    [ClassInitialize]
    public static async Task ClassInit(TestContext _)
    {
        _connection = Environment.GetEnvironmentVariable("RSSREADER_TEST_AZURITE_CONNECTION");
        if (string.IsNullOrWhiteSpace(_connection)) return;
        _container = new BlobContainerClient(_connection, "dp-tests-" + Guid.NewGuid().ToString("N")[..8]);
        await _container.CreateIfNotExistsAsync();
    }

    [ClassCleanup]
    public static async Task ClassCleanup()
    {
        if (_connection != null) await _container.DeleteIfExistsAsync();
    }

    [TestInitialize]
    public void Init()
    {
        if (string.IsNullOrWhiteSpace(_connection))
            Assert.Inconclusive("RSSREADER_TEST_AZURITE_CONNECTION is not set; Azurite is not available.");
        _keyDir = Path.Combine(Path.GetTempPath(), "dp-keys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_keyDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (_keyDir != null && Directory.Exists(_keyDir)) Directory.Delete(_keyDir, recursive: true);
    }

    private static IDataProtector Protector(Action<IDataProtectionBuilder> persist)
    {
        var services = new ServiceCollection();
        persist(services.AddDataProtection().SetApplicationName("rss-reader"));
        return services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>().CreateProtector("test");
    }

    [TestMethod]
    public async Task Import_MovesShareKeysIntoBlob_SoOldCookiesStillDecrypt()
    {
        var sealedByShare = Protector(b => b.PersistKeysToFileSystem(new DirectoryInfo(_keyDir))).Protect("session");
        Assert.IsTrue(Directory.EnumerateFiles(_keyDir, "*.xml").Any());
        var blob = _container.GetBlobClient($"{TestContext!.TestName}.xml");

        await DataProtectionKeyStore.ImportLegacyKeysAsync(blob, _keyDir, NullLogger.Instance);

        var fromBlob = Protector(b => b.PersistKeysToAzureBlobStorage(blob));
        Assert.AreEqual("session", fromBlob.Unprotect(sealedByShare));
    }

    [TestMethod]
    public async Task Import_WhenBlobExists_LeavesItAlone()
    {
        var blob = _container.GetBlobClient($"{TestContext!.TestName}.xml");
        await blob.UploadAsync(BinaryData.FromString("<repository />"));
        Protector(b => b.PersistKeysToFileSystem(new DirectoryInfo(_keyDir))).Protect("x");

        await DataProtectionKeyStore.ImportLegacyKeysAsync(blob, _keyDir, NullLogger.Instance);

        Assert.AreEqual("<repository />", (await blob.DownloadContentAsync()).Value.Content.ToString());
    }

    [TestMethod]
    public async Task Import_WithNoKeyFiles_CreatesNothing()
    {
        var blob = _container.GetBlobClient($"{TestContext!.TestName}.xml");

        await DataProtectionKeyStore.ImportLegacyKeysAsync(blob, _keyDir, NullLogger.Instance);
        await DataProtectionKeyStore.ImportLegacyKeysAsync(blob, Path.Combine(_keyDir, "missing"), NullLogger.Instance);

        Assert.IsFalse((await blob.ExistsAsync()).Value);
    }

    public TestContext? TestContext { get; set; }
}
