using RssApp.Contracts;
using RssApp.Serialization;
using RssApp.Data;
using RssApp.ComponentServices;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using RssApp.Config;

namespace RssApp.RssClient;

public class FeedRefresher : IFeedRefresher
{
    private static readonly bool EnableHttpLookup = true;
    private readonly IHttpClientFactory httpClientFactory;
    private readonly RssDeserializer deserializer;
    private readonly ILogger<FeedRefresher> logger;
    private readonly IFeedRepository persistedFeeds;
    private readonly IItemRepository newsFeedItemStore;
    private readonly IUserRepository userStore;
    private readonly BackgroundWorkQueue backgroundWorkQueue;
    private readonly RssAppConfig config;

    // Lock-free state: Interlocked for counter, ConcurrentDictionary for flags.
    private readonly ConcurrentDictionary<int, bool> _hasNewItems = new();
    private long _lastCacheReloadTicks = 0;
    private DateTime startupTime = DateTime.UtcNow;
    private Exception lastRefreshException;
    private int _pendingRefreshes = 0;

    public FeedRefresher(
        IHttpClientFactory httpClientFactory,
        RssDeserializer deserializer,
        ILogger<FeedRefresher> logger,
        IFeedRepository persistedFeeds,
        IItemRepository newsFeedItemStore,
        IUserRepository userStore,
        BackgroundWorkQueue backgroundWorkQueue,
        RssAppConfig config)
    {
        this.httpClientFactory = httpClientFactory;
        this.deserializer = deserializer;
        this.logger = logger;
        this.persistedFeeds = persistedFeeds;
        this.newsFeedItemStore = newsFeedItemStore;
        this.userStore = userStore;
        this.backgroundWorkQueue = backgroundWorkQueue;
        this.config = config;
    }

    public DateTime? LastCacheReloadTime
    {
        get
        {
            long ticks = Interlocked.Read(ref _lastCacheReloadTicks);
            return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
        }
    }

    public bool IsRefreshing => Volatile.Read(ref _pendingRefreshes) > 0;

    public Task<bool> HasNewItemsAsync(RssUser user)
    {
        // Atomically read and reset the flag. No lock needed.
        var hasNew = _hasNewItems.TryRemove(user.Id, out var val) && val;
        return Task.FromResult(hasNew);
    }

    public async Task AddFeedAsync(NewsFeed feed)
    {
        await ReloadCachedItemsAsync(feed);
    }

    public async Task RefreshAsync(RssUser user)
    {
        bool isJustStarted = this.startupTime + this.config.CacheReloadStartupDelay > DateTime.UtcNow;
        long lastTicks = Interlocked.Read(ref _lastCacheReloadTicks);
        bool isRecentRefresh = lastTicks > 0
            && new DateTime(lastTicks, DateTimeKind.Utc) + this.config.CacheReloadInterval > DateTime.UtcNow;

        if (isJustStarted || isRecentRefresh)
        {
            _hasNewItems[user.Id] = true;
            return;
        }

        try
        {
            var feeds = this.persistedFeeds.GetFeeds(user).ToList();

            if (feeds.Count == 0)
            {
                Interlocked.Exchange(ref _lastCacheReloadTicks, DateTime.UtcNow.Ticks);
                return;
            }

            Interlocked.Exchange(ref _pendingRefreshes, feeds.Count);

            foreach (var feed in feeds)
            {
                await this.backgroundWorkQueue.QueueBackgroundWorkItemAsync(async token =>
                {
                    try
                    {
                        await ReloadCachedItemsAsync(feed);
                    }
                    catch (Exception ex)
                    {
                        this.logger.LogError(ex, "Error reloading feed: {feed}", feed.Href);
                    }
                    finally
                    {
                        if (Interlocked.Decrement(ref _pendingRefreshes) == 0)
                        {
                            Interlocked.Exchange(ref _lastCacheReloadTicks, DateTime.UtcNow.Ticks);
                            _hasNewItems[user.Id] = true;
                        }
                    }
                });
            }
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Error reloading cache");
            Interlocked.Exchange(ref _pendingRefreshes, 0);
        }
    }

    /// <summary>
    /// Fetches fresh items from a feed and passes ALL of them to AddItemsAsync.
    /// Deduplication is handled inside AddItemsAsync via pre-fetch + HashSet,
    /// so no per-item GetItem() calls are needed here.
    /// </summary>
    private async Task ReloadCachedItemsAsync(NewsFeed feed)
    {
        var url = feed.Href;
        var user = this.userStore.GetUserById(feed.UserId);

        if (user == null)
        {
            this.logger.LogError("User not found: {userId}", feed.UserId);
            return;
        }

        var freshItems = await this.FetchItemsFromFeedAsync(user, url);

        foreach (var item in freshItems)
        {
            item.FeedUrl = url;
            item.FeedTags = feed.Tags;
        }

        if (freshItems.Any())
        {
            await this.newsFeedItemStore.AddItemsAsync(freshItems);
        }
    }

    private async Task<HashSet<NewsFeedItem>> FetchItemsFromFeedAsync(RssUser user, string url)
    {
        var freshItems = new HashSet<NewsFeedItem>();
        string response = null;
        string[] agents = ["rss.brandonchastain.com/1.1", "curl/7.79.1"];

        foreach (string agent in agents)
        {
            try
            {
                if (EnableHttpLookup)
                {
                    using var httpClient = httpClientFactory.CreateClient("RssClient");
                    using var browserRequest = new HttpRequestMessage(HttpMethod.Get, url);
                    browserRequest.Headers.UserAgent.ParseAdd(agent);
                    browserRequest.Headers.Accept.ParseAdd("text/xml");
                    browserRequest.Headers.Accept.ParseAdd("application/xml");
                    browserRequest.Headers.Accept.ParseAdd("application/rss+xml");
                    browserRequest.Headers.Accept.ParseAdd("application/atom+xml");

                    using var httpRes = await httpClient.SendAsync(browserRequest);
                    response = await httpRes.Content.ReadAsStringAsync();

                    if (string.IsNullOrEmpty(response))
                    {
                        this.logger.LogWarning($"Empty response when refreshing feed: {url}");
                        this.logger.LogWarning($"Empty response headers: {httpRes.Headers}" );
                        this.logger.LogError($"Error response: {response}");
                        continue;
                    }

                    var items = this.deserializer.FromString(response, user);
                    freshItems.UnionWith(items);

                    // It worked. Exit the loop.
                    break;
                }
            }
            catch (Exception ex)
            {
                if (lastRefreshException != null)
                {
                    throw;
                }

                int len = Math.Min(500, response?.Length ?? 0);
                this.logger.LogError(ex, "Error reloading feeds. Bad RSS response.\n{url}\n{response}", url, response?.Substring(0, len));
                lastRefreshException = ex;
            }
        }

        return freshItems;
    }
}