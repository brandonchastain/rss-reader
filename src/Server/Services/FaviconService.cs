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

    public FaviconService(IHttpClientFactory httpClientFactory, ILogger<FaviconService> logger)
    {
        this.httpClientFactory = httpClientFactory;
        this.logger = logger;
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
            return filePath;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Favicon download failed for {Domain}", safe);
            return null;
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
