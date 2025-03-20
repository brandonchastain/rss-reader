using Microsoft.Extensions.Caching.Memory;
using RssApp.Contracts;
using RssApp.Serialization;
using RssApp.Persistence;

namespace RssApp.RssClient;

public class FeedClient : IFeedClient
{
    private const int PageSize = 10;
    private static readonly TimeSpan CacheReloadInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CacheReloadStartupDelay = TimeSpan.FromSeconds(3);
    private static readonly bool EnableHttpLookup = true;

    private IMemoryCache feedCache;
    private HttpClient httpClient;
    private readonly RssDeserializer deserializer;
    private readonly ILogger<FeedClient> logger;
    private readonly PersistedHiddenItems hiddenItems;
    private readonly IPersistedFeeds persistedFeeds;
    private readonly Timer timer;
    private readonly INewsFeedItemStore newsFeedItemStore;

    public FeedClient(
        IMemoryCache cache,
        HttpClient httpClient,
        RssDeserializer deserializer,
        PersistedHiddenItems hiddenItems,
        ILogger<FeedClient> logger,
        IPersistedFeeds persistedFeeds,
        INewsFeedItemStore newsFeedItemStore)
    {
        this.feedCache = cache;
        this.httpClient = httpClient;
        this.deserializer = deserializer;
        this.hiddenItems = hiddenItems;
        this.logger = logger;
        this.persistedFeeds = persistedFeeds;
        this.newsFeedItemStore = newsFeedItemStore;
        this.timer = new Timer(this.ReloadCache, null, CacheReloadStartupDelay, CacheReloadInterval);
    }

    public async Task<IEnumerable<NewsFeedItem>> GetFeedItems(IEnumerable<string> urls, int page)
    {
        var items = new List<NewsFeedItem>();
        foreach (var url in urls)
        {
            items.AddRange(await this.GetFeedItems(url));
        }
        
        var sorted = items.DistinctBy(i => i.GetHashCode()).OrderByDescending(i => i.ParsedDate);
        return sorted.Skip(page * PageSize).Take(PageSize);
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
        if (!feedCache.TryGetValue(url, out ISet<NewsFeedItem> response))
        {
            response = this.newsFeedItemStore.GetItems(url).ToHashSet();
        }

        // this.logger.LogInformation(response);
        var hidden = this.hiddenItems.GetHidden();
        var items = response.ToList();

        var result = items.DistinctBy(i => i.Href)
            .OrderByDescending(i => i.ParsedDate)
            .Where(i => !hidden.Contains(i.Href));

        return result;
    }

    public void HidePost(string id)
    {
        this.hiddenItems.HidePost(id);
    }

    private async void ReloadCache(object state)
    {
        _ = state;
        
        var urls = this.persistedFeeds.GetFeeds();

        foreach (var url in urls)
        {
            await this.ReloadCachedItems(url);
        }
    }

    public async Task ReloadCachedItems(string url)
    {
        try
        {
            if (!feedCache.TryGetValue(url, out ISet<NewsFeedItem> cachedItems))
            {
                cachedItems = this.newsFeedItemStore.GetItems(url).ToHashSet();
            }

            var freshItems = new HashSet<NewsFeedItem>();
            if (EnableHttpLookup)
            {
                var httpRes = await this.httpClient.GetAsync(url);
                var response = await httpRes.Content.ReadAsStringAsync();

                if (string.IsNullOrEmpty(response))
                {
                    this.logger.LogWarning($"Empty response when refreshing feed: {url}");
                    return;
                }

                freshItems = this.deserializer.FromString(response).ToHashSet();
            }

            foreach (var item in freshItems)
            {
                item.FeedUrl = url;
            }

            var newItems = freshItems.Except(cachedItems);

            foreach (var item in newItems.ToList())
            {
                this.newsFeedItemStore.AddItem(item);
            }

            cachedItems.UnionWith(freshItems);
            feedCache.Set(url, cachedItems);
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Error reloading feeds");
        }
    }
}