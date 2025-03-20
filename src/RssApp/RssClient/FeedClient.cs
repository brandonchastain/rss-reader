using Microsoft.Extensions.Caching.Memory;
using RssApp.Contracts;
using RssApp.Serialization;
using RssApp.Persistence;

namespace RssApp.RssClient;

public class FeedClient : IFeedClient
{
    private const int PageSize = 10;
    private IMemoryCache feedCache;
    private HttpClient httpClient;
    private readonly RssDeserializer deserializer;
    private readonly ILogger<FeedClient> logger;
    private readonly PersistedHiddenItems hiddenItems;

    public FeedClient(IMemoryCache cache, HttpClient httpClient, RssDeserializer deserializer, PersistedHiddenItems hiddenItems, ILogger<FeedClient> logger)
    {
        this.feedCache = cache;
        this.httpClient = httpClient;
        this.deserializer = deserializer;
        this.hiddenItems = hiddenItems;
        this.logger = logger;
    }

    public async Task<IEnumerable<NewsFeedItem>> GetFeedItems(IEnumerable<string> urls, int page)
    {
        var items = new List<NewsFeedItem>();
        foreach (var url in urls)
        {
            items.AddRange(await this.GetFeedItems(url));
        }
        
        var sorted = items.OrderByDescending(i => i.ParsedDate);
        return sorted.DistinctBy(i => i.Id).Skip(page * PageSize).Take(PageSize);
    }

    public async Task<IEnumerable<NewsFeedItem>> GetFeedItems(string url, int page)
    {
        var items = await this.GetFeedItems(url);
        return items
            .Skip(page * PageSize)
            .Take(PageSize);
    }

    private async Task<IEnumerable<NewsFeedItem>> GetFeedItems(string url)
    {
        if (!feedCache.TryGetValue(url, out string response))
        {
            var httpRes = await this.httpClient.GetAsync(url);
            response = await httpRes.Content.ReadAsStringAsync();
            feedCache.Set(url, response, TimeSpan.FromMinutes(2));
        }

        response = feedCache.Get<string>(url);

        // this.logger.LogInformation(response);
        var hidden = this.hiddenItems.GetHidden();
        var items = this.deserializer.FromString(response).ToList();
        foreach (var item in items)
        {
            item.FeedUrl = url;
        }
        var result = items.DistinctBy(i => i.Id)
            .OrderByDescending(i => i.ParsedDate)
            .Where(i => !hidden.Contains(i.Id));
        return result;
    }

    public void HidePost(string id)
    {
        this.hiddenItems.HidePost(id);
    }
}