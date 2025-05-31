using RssApp.Contracts;
using RssApp.Serialization;
using RssApp.Data;
using RssApp.ComponentServices;

namespace RssApp.RssClient;

public class FeedRefresher : IDisposable
{
    private const int PageSize = 10;
    private static readonly bool EnableHttpLookup = true;
    private HttpClient httpClient;
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
    private bool doRefresh;

    public FeedRefresher(
        RssDeserializer deserializer,
        ILogger<FeedRefresher> logger,
        IFeedRepository persistedFeeds,
        IItemRepository newsFeedItemStore,
        IUserRepository userStore,
        BackgroundWorkQueue backgroundWorkQueue,
        TimeSpan cacheReloadInterval,
        TimeSpan cacheReloadStartupDelay)
    {

        var clientHandler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
        };

        this.httpClient = new HttpClient(clientHandler);
        this.deserializer = deserializer;
        this.logger = logger;
        this.persistedFeeds = persistedFeeds;
        this.newsFeedItemStore = newsFeedItemStore;
        this.userStore = userStore;
        this.cacheReloadInterval = cacheReloadInterval;
        this.cacheReloadStartupDelay = cacheReloadStartupDelay;
        this.backgroundWorkQueue = backgroundWorkQueue;
    }

    public DateTime? LastCacheReloadTime => this.lastCacheReloadTime;

    public void Dispose()
    {
        this.httpClient.Dispose();
    }

    public async Task AddFeedAsync(NewsFeed feed)
    {
        await ReloadCachedItemsAsync(feed);
    }

    public async Task RefreshAsync()
    {
        bool isJustStarted = this.startupTime + this.cacheReloadStartupDelay > DateTime.UtcNow;
        bool isRecentRefresh = this.lastCacheReloadTime + this.cacheReloadInterval > DateTime.UtcNow;
        
        if (isJustStarted || isRecentRefresh)
        {
            return;
        }

        //this.logger.LogInformation("Refreshing feeds...");
        await this.backgroundWorkQueue.QueueBackgroundWorkItemAsync(async token =>
        {
            bool isJustStarted = this.startupTime + this.cacheReloadStartupDelay > DateTime.UtcNow;
            bool isRecentRefresh = this.lastCacheReloadTime + this.cacheReloadInterval > DateTime.UtcNow;
            
            if (isJustStarted || isRecentRefresh)
            {
                return;
            }
            try
            {
                var allUsers = this.userStore.GetAllUsers();
                foreach (var user in allUsers)
                {
                    var feeds = this.persistedFeeds.GetFeeds(user);
                    foreach (var feed in feeds)
                    {
                        try
                        {
                            await ReloadCachedItemsAsync(feed);
                        }
                        catch (Exception ex)
                        {
                            this.logger.LogError(ex, "Error reloading feed: {feed}", feed.FeedUrl);
                        }
                    }
                }

                this.lastCacheReloadTime = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "Error reloading cache");
            }
        });
    }

    private async Task ReloadCachedItemsAsync(NewsFeed feed)
    {
        var url = feed.FeedUrl;
        var user = this.userStore.GetUserById(feed.UserId);
        var freshItems = new HashSet<NewsFeedItem>();
        string response = null;

        string[] agents = ["rssreader.brandonchastain.com/1.1", "curl/7.79.1"];

        foreach (string agent in agents)
        {
            try
            {
                if (EnableHttpLookup)
                {
                    var browserRequest = new HttpRequestMessage(HttpMethod.Get, url);
                    browserRequest.Headers.UserAgent.ParseAdd(agent);
                    browserRequest.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        
                    var httpRes = await this.httpClient.SendAsync(browserRequest);
                    response = await httpRes.Content.ReadAsStringAsync();

                    if (string.IsNullOrEmpty(response))
                    {
                        this.logger.LogWarning($"Empty response when refreshing feed: {url}");
                        return;
                    }

                    freshItems = this.deserializer.FromString(response, user).ToHashSet();
                    
                    // It worked. Exit the loop.
                    break;
                }
            }
            catch (Exception ex)
            {
                int len = Math.Min(500, response?.Length ?? 0);
                this.logger.LogError(ex, "Error reloading feeds. Bad RSS response.\n{url}\n{response}", url, response?.Substring(0, len));
                lastRefreshException = ex;
            }
        }

        if (lastRefreshException != null && (freshItems == null || !freshItems.Any()))
        {
            //this.logger.LogWarning($"No items found when refreshing feed: {url}");
            throw lastRefreshException;
        }

        foreach (var item in freshItems)
        {
            item.FeedUrl = url;
            item.FeedTags = feed.Tags;
        }

        var size = Math.Max(10, freshItems.Count);
        var cachedItems = (await this.newsFeedItemStore.GetItemsAsync(feed, isFilterUnread: false, isFilterSaved: false, filterTag: null, page: 0, pageSize: size)).ToHashSet();
        var newItems = freshItems.Except(cachedItems);
        this.newsFeedItemStore.AddItems(newItems);
    }
}