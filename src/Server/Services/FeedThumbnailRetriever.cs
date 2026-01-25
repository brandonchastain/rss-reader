

using RssApp.Config;
using RssApp.Contracts;
using RssReader.Shared.Extensions;

namespace RssReader.Server.Services;

public class FeedThumbnailRetriever
{
    private readonly RssAppConfig _config;

    public FeedThumbnailRetriever(RssAppConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public async Task<string> RetrieveThumbnailUrlAsync(NewsFeed feed)
    {

        //await semaphore.WaitAsync();
        try
        {
            return await GetOrDownloadFaviconAsync(feed);
        }
        finally
        {
            //semaphore.Release();
        }
    }

    private async Task<string> GetOrDownloadFaviconAsync(NewsFeed feed)
    {
        // Use domain as filename to ensure consistency across users and feed instances
        var hostname = feed.Href.GetRootDomain();
        var imgSrc = Path.Combine("images", $"{hostname}.ico");
        var filePath = Path.Combine("wwwroot", imgSrc);

        if (File.Exists(filePath))
        {
            return _config.ServerHostName + imgSrc;
        }

        // Download the icon
        string favicon = $"https://icons.duckduckgo.com/ip2/{hostname}.ico";

        using (var httpClient = new HttpClient())
        {
            var response = await httpClient.GetAsync(favicon);
            if (response.IsSuccessStatusCode)
            {

                using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write))
                {
                    await response.Content.CopyToAsync(fileStream);
                }

                return _config.ServerHostName + imgSrc;
            }
        }

        return _config.ServerHostName + "images/placeholder.jpg";
    }
}