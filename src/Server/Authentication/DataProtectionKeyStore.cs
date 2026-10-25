#nullable enable
using System.Xml.Linq;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
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

    /// <summary>
    /// One-time move of the share's key files into the blob so nobody is signed out by the
    /// switch. Does nothing when the blob already exists, so it is safe on every boot and on
    /// two overlapping revisions: the create is conditional and the loser's write is refused.
    /// The blob holds one <c>&lt;repository&gt;</c> element whose children are the
    /// <c>&lt;key&gt;</c> and <c>&lt;revocation&gt;</c> elements of the key files, which is what
    /// the blob key store reads.
    /// </summary>
    public static async Task ImportLegacyKeysAsync(BlobClient blob, string legacyDirectory, ILogger logger, CancellationToken cancellationToken = default)
    {
        if (await blob.ExistsAsync(cancellationToken))
        {
            logger.LogInformation("Data-protection key ring already in blob; nothing to import.");
            return;
        }
        if (string.IsNullOrWhiteSpace(legacyDirectory) || !Directory.Exists(legacyDirectory))
        {
            logger.LogWarning("No data-protection key ring in blob and no key directory at '{Directory}'; a new ring is created and existing sessions end.", legacyDirectory);
            return;
        }

        var repository = new XElement("repository");
        foreach (var file in Directory.EnumerateFiles(legacyDirectory, "*.xml").OrderBy(f => f, StringComparer.Ordinal))
        {
            repository.Add(XDocument.Load(file).Root!);
        }
        if (!repository.HasElements)
        {
            logger.LogWarning("No key files in '{Directory}'; a new ring is created and existing sessions end.", legacyDirectory);
            return;
        }

        var options = new BlobUploadOptions
        {
            Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
        };
        try
        {
            await blob.UploadAsync(BinaryData.FromString(new XDocument(repository).ToString(SaveOptions.DisableFormatting)), options, cancellationToken);
            logger.LogInformation("Imported {Count} data-protection key element(s) from '{Directory}' into blob.", repository.Elements().Count(), legacyDirectory);
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            logger.LogInformation("Data-protection key ring was created by another revision while importing; kept theirs.");
        }
    }
}
