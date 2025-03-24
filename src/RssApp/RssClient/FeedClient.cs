using Microsoft.Extensions.Caching.Memory;
using RssApp.Contracts;
using RssApp.Serialization;
using RssApp.Persistence;

namespace RssApp.RssClient;

public class FeedClient : IFeedClient
{
    private const int PageSize = 10;
    private static readonly TimeSpan CacheReloadInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CacheReloadStartupDelay = TimeSpan.FromMinutes(0);
    private static readonly bool EnableHttpLookup = true;

    private IMemoryCache feedCache;
    private HttpClient httpClient;
    private readonly RssDeserializer deserializer;
    private readonly ILogger<FeedClient> logger;
    private readonly PersistedHiddenItems hiddenItems;
    private readonly IFeedRepository persistedFeeds;
    private readonly Timer timer;
    private readonly IItemRepository newsFeedItemStore;

    public FeedClient(
        IMemoryCache cache,
        HttpClient httpClient,
        RssDeserializer deserializer,
        PersistedHiddenItems hiddenItems,
        ILogger<FeedClient> logger,
        IFeedRepository persistedFeeds,
        IItemRepository newsFeedItemStore)
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

    public bool IsFilterUnread { get; set; } = false;

    public IEnumerable<NewsFeedItem> GetFeedItems(IEnumerable<NewsFeed> feeds, int page)
    {
        var items = new List<NewsFeedItem>();
        foreach (var feed in feeds)
        {
            items.AddRange(this.GetFeedItemsHelper(feed));
        }
        
        var sorted = items
            .DistinctBy(i => i.GetHashCode())
            .OrderByDescending(i => i.ParsedDate);

        return sorted
            .Skip(page * PageSize)
            .Take(PageSize);
    }

    public IEnumerable<NewsFeedItem> GetFeedItems(NewsFeed feed, int page)
    {
        var items = this.GetFeedItemsHelper(feed);
        return items
            .Skip(page * PageSize)
            .Take(PageSize);
    }

    public void HidePost(string id)
    {
        this.hiddenItems.HidePost(id);
    }

    public async Task MarkAsReadAsync(NewsFeedItem item, bool isRead)
    {
        await Task.Yield();
        this.newsFeedItemStore.MarkAsRead(item, isRead);
        this.feedCache.Remove(item.FeedUrl);
        this.ClearCachedItemsForFeed(new NewsFeed(item.FeedUrl));
    }

    private IEnumerable<NewsFeedItem> GetFeedItemsHelper(NewsFeed feed)
    {
        var url = feed.FeedUrl;
        if (!feedCache.TryGetValue(url, out ISet<NewsFeedItem> response))
        {
            response = this.newsFeedItemStore.GetItems(feed).ToHashSet();
        }

        var hidden = this.hiddenItems.GetHidden();
        var items = response.ToList();

        var result = items.DistinctBy(i => i.Href)
            .OrderByDescending(i => i.ParsedDate)
            .Where(i => !hidden.Contains(i.Href))
            .Where(i => !this.IsFilterUnread || !i.IsRead);

        return result;
    }

#pragma warning disable VSTHRD100 // Avoid async void methods
    private async void ReloadCache(object state)
#pragma warning restore VSTHRD100 // Avoid async void methods
    {
        try
        {
            _ = state;
            
            var feeds = this.persistedFeeds.GetFeeds();

            foreach (var feed in feeds)
            {
                await this.ReloadCachedItemsAsync(feed);
                await Task.Delay(10000);
            }
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Error reloading cache");
        }
    }

    private void ClearCachedItemsForFeed(NewsFeed feed)
    {
        feedCache.Set(feed.FeedUrl, this.newsFeedItemStore.GetItems(feed).ToHashSet());
    }

    private async Task ReloadCachedItemsAsync(NewsFeed feed)
    {
        var url = feed.FeedUrl;
        try
        {
            if (!feedCache.TryGetValue(url, out ISet<NewsFeedItem> cachedItems))
            {
                cachedItems = this.newsFeedItemStore.GetItems(feed).ToHashSet();
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
            feedCache.Set(url, cachedItems, TimeSpan.FromMinutes(5));
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Error reloading feeds");
        }
    }
}