#nullable enable
using Azure.Storage.Blobs;
using RssApp.Config;
using RssReader.Server.Services;

namespace Server.Authentication;

/// <summary>
/// Where the session-cookie key ring lives (BLOB-DURABILITY-SPEC.md 3.6): one blob,
/// <c>keys.xml</c>, in the <c>dataprotection</c> container, reached with the app's managed
/// identity. Replaces the key files on the Azure Files share.
/// </summary>
public static class DataProtectionKeyStore
{
    public const string BlobName = "keys.xml";

    /// <summary>The key ring blob, or null when no blob key store is configured.</summary>
    public static BlobClient? CreateBlobClient(RssAppConfig config)
    {
        var container = config.Auth.DataProtectionKeysContainer;
        if (string.IsNullOrWhiteSpace(container) || !config.Durability.LeaseEnabled) return null;
        return BlobWriterLease.CreateContainerClient(config.Durability, container).GetBlobClient(BlobName);
    }
}
