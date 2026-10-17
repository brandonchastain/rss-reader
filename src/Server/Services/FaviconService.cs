namespace RssReader.Server.Services;

/// <summary>
/// Fetches and disk-caches site favicons (via DuckDuckGo's icon service) so the
/// browser loads them from our own origin instead of contacting a third party
/// directly. Exposed through GET /api/feed/icon.
///
/// Replaces FeedThumbnailRetriever: uses IHttpClientFactory instead of
/// `new HttpClient()`, and returns a local file path rather than baking
/// ServerHostName into a stored absolute URL (which broke whenever the host
/// differed between environments).
/// </summary>
public class FaviconService
{
    private readonly IHttpClientFactory httpClientFactory;
    private readonly ILogger<FaviconService> logger;
    private readonly string imagesDir = Path.Combine("wwwroot", "images");
    private readonly string backupImagesDir;

    public FaviconService(IHttpClientFactory httpClientFactory, ILogger<FaviconService> logger)
        : this(httpClientFactory, logger, null)
    {
    }

    public FaviconService(IHttpClientFactory httpClientFactory, ILogger<FaviconService> logger, DatabaseBackupPaths backupPaths)
    {
        this.httpClientFactory = httpClientFactory;
        this.logger = logger;
        this.backupImagesDir = backupPaths?.BackupImagesPath;
    }

    /// <summary>
    /// Returns the absolute path to the cached favicon for a domain, downloading
    /// it on first request. Returns null if the domain is invalid or the icon
    /// cannot be fetched.
    /// </summary>
    public async Task<string> GetFaviconPathAsync(string domain)
    {
        var safe = SanitizeDomain(domain);
        if (safe == null)
        {
            return null;
        }

        Directory.CreateDirectory(imagesDir);
        var filePath = Path.GetFullPath(Path.Combine(imagesDir, $"{safe}.ico"));

        if (File.Exists(filePath))
        {
            return filePath;
        }

        try
        {
            var client = httpClientFactory.CreateClient("RssClient");
            using var resp = await client.GetAsync($"https://icons.duckduckgo.com/ip2/{safe}.ico");
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            var bytes = await resp.Content.ReadAsByteArrayAsync();
            if (bytes.Length == 0)
            {
                return null;
            }

            // Write to a unique temp file then move, so concurrent requests for the
            // same domain can't serve a half-written file.
            var tmp = Path.Combine(imagesDir, $"{safe}.{Guid.NewGuid():N}.tmp");
            await File.WriteAllBytesAsync(tmp, bytes);
            File.Move(tmp, filePath, overwrite: true);
            await PersistToBackupAsync(filePath, $"{safe}.ico");
            return filePath;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Favicon download failed for {Domain}", safe);
            return null;
        }
    }

    /// <summary>
    /// Write a freshly cached icon through to the persistent image directory so it
    /// survives a container recycle. Event-driven: this replaced a 5-minute loop that
    /// stat'ed every cached image on the SMB share looking for new ones. Best effort:
    /// a miss after a recycle just re-downloads. Skipped when the persistent mount
    /// is not present (local dev, reader replicas).
    /// </summary>
    internal async Task PersistToBackupAsync(string localPath, string fileName)
    {
        if (string.IsNullOrEmpty(backupImagesDir))
        {
            return;
        }

        try
        {
            var mountRoot = Path.GetDirectoryName(Path.GetFullPath(backupImagesDir.TrimEnd('/', '\\')));
            if (string.IsNullOrEmpty(mountRoot) || !Directory.Exists(mountRoot))
            {
                return;
            }

            Directory.CreateDirectory(backupImagesDir);
            var dest = Path.Combine(backupImagesDir, fileName);
            if (File.Exists(dest))
            {
                return;
            }

            var tmp = Path.Combine(backupImagesDir, $"{fileName}.{Guid.NewGuid():N}.tmp");
            using (var src = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var dst = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await src.CopyToAsync(dst, 1024 * 1024);
            }
            File.Move(tmp, dest, overwrite: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not persist favicon {File} to {Dir}", fileName, backupImagesDir);
        }
    }

    /// <summary>Allow only plausible hostnames (letters, digits, dot, hyphen) to keep this off the filesystem/SSRF surface.</summary>
    private static string SanitizeDomain(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            return null;
        }

        domain = domain.Trim().ToLowerInvariant();
        if (domain.Length is 0 or > 253)
        {
            return null;
        }

        foreach (var c in domain)
        {
            if (!(char.IsLetterOrDigit(c) || c == '.' || c == '-'))
            {
                return null;
            }
        }

        return domain;
    }
}
