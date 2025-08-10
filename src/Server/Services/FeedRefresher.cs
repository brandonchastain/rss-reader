using RssApp.Contracts;
using RssApp.Serialization;
using RssApp.Data;
using RssApp.ComponentServices;

namespace RssApp.RssClient;

public class FeedRefresher : IFeedRefresher, IDisposable
{
    private static readonly bool EnableHttpLookup = true;
    private readonly IHttpClientFactory httpClientFactory;
    private readonly RssDeserializer deserializer;
    private readonly ILogger<FeedRefresher> logger;
    private readonly IFeedRepository persistedFeeds;
    private readonly IItemRepository newsFeedItemStore;
    private readonly IUserRepository userStore;
    private readonly TimeSpan cacheReloadInterval;
    private readonly TimeSpan cacheReloadStartupDelay;
    private readonly BackgroundWorkQueue backgroundWorkQueue;
    private DateTime? lastCacheReloadTime;
    private DateTime startupTime = DateTime.UtcNow;
    private Exception lastRefreshException;
    private int pendingRefreshes = 0;
    private bool isRefreshing;

    public event EventHandler<List<NewsFeedItem>> OnNewItemsAvailable;
    public event EventHandler OnRefreshComplete;

    public bool IsRefreshing => isRefreshing;

    public FeedRefresher(
        IHttpClientFactory httpClientFactory,
        RssDeserializer deserializer,
        ILogger<FeedRefresher> logger,
        IFeedRepository persistedFeeds,
        IItemRepository newsFeedItemStore,
        IUserRepository userStore,
        BackgroundWorkQueue backgroundWorkQueue,
        TimeSpan cacheReloadInterval,
        TimeSpan cacheReloadStartupDelay)
    {
        this.httpClientFactory = httpClientFactory;
        this.deserializer = deserializer;
        this.logger = logger;
        this.persistedFeeds = persistedFeeds;
        this.newsFeedItemStore = newsFeedItemStore;
        this.userStore = userStore;
        this.cacheReloadInterval = cacheReloadInterval;
        this.cacheReloadStartupDelay = cacheReloadStartupDelay;
        this.backgroundWorkQueue = backgroundWorkQueue;
        this.isRefreshing = false;
    }

    public DateTime? LastCacheReloadTime => this.lastCacheReloadTime;

    public void Dispose()
    {
        // Remove httpClient.Dispose() since we're using HttpClientFactory
    }

    public async Task AddFeedAsync(NewsFeed feed)
    {
        await ReloadCachedItemsAsync(feed);
    }

    public async Task RefreshAsync(RssUser user)
    {
        bool isJustStarted = this.startupTime + this.cacheReloadStartupDelay > DateTime.UtcNow;
        bool isRecentRefresh = this.lastCacheReloadTime + this.cacheReloadInterval > DateTime.UtcNow;

        if (isJustStarted || isRecentRefresh || isRefreshing)
        {
            return;
        }

        try
        {
            var totalFeeds = 0;
            var feeds = this.persistedFeeds.GetFeeds(user);
            totalFeeds += feeds.Count();

            if (totalFeeds == 0)
            {
                this.lastCacheReloadTime = DateTime.UtcNow;
                OnRefreshComplete?.Invoke(this, EventArgs.Empty);
                return;
            }

            isRefreshing = true;
            Interlocked.Exchange(ref pendingRefreshes, totalFeeds);
            
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
                        if (Interlocked.Decrement(ref pendingRefreshes) == 0)
                        {
                            this.lastCacheReloadTime = DateTime.UtcNow;
                            isRefreshing = false;
                            OnRefreshComplete?.Invoke(this, EventArgs.Empty);
                        }
                    }
                });
            }
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Error reloading cache");
            Interlocked.Exchange(ref pendingRefreshes, 0);
            isRefreshing = false;
            OnRefreshComplete?.Invoke(this, EventArgs.Empty);
        }
    }

    private void NotifyNewItems(List<NewsFeedItem> newItems)
    {
        OnNewItemsAvailable?.Invoke(this, newItems);
    }

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
        var newItems = new List<NewsFeedItem>();

        foreach (var item in freshItems)
        {
            item.FeedUrl = url;
            item.FeedTags = feed.Tags;

            var existing = this.newsFeedItemStore.GetItem(user, item.Href);
            if (existing != null)
            {
                // Item already exists in the store, skip it
                this.logger.LogDebug("Skipping existing item: {itemId} from feed: {feedUrl}", item.Id, url);
                continue;
            }

            newItems.Add(item);
        }

        if (newItems.Any())
        {
            this.newsFeedItemStore.AddItems(newItems);
            NotifyNewItems(newItems);
        }
    }

    private async Task<HashSet<NewsFeedItem>> FetchItemsFromFeedAsync(RssUser user, string url)
    {
        var freshItems = new HashSet<NewsFeedItem>();
        string response = null;
        string[] agents = ["rssreader.brandonchastain.com/1.1", "curl/7.79.1"];

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
                    response = null;  // Help GC by clearing the response string

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
                response = null;  // Help GC by clearing the response string
            }
        }

        return freshItems;
    }
}