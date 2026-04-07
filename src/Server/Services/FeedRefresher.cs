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
    private readonly ConcurrentDictionary<int, UserRefreshState> refreshStates = new();
    private DateTime startupTime = DateTime.UtcNow;
    private Exception lastRefreshException;

    /// <summary>
    /// Per-user refresh state. Thread-safe via Interlocked/volatile.
    /// </summary>
    private class UserRefreshState
    {
        private int _pendingFeeds;
        private volatile bool _hasNewItems;
        private volatile bool _isRefreshing;
        private DateTime? _lastRefreshTime;

        public bool IsRefreshing => _isRefreshing;
        public int PendingFeeds => Volatile.Read(ref _pendingFeeds);
        public bool HasNewItems => _hasNewItems;
        public DateTime? LastRefreshTime => _lastRefreshTime;

        public void StartRefresh(int feedCount)
        {
            _hasNewItems = false;
            Interlocked.Exchange(ref _pendingFeeds, feedCount);
            _isRefreshing = true;
        }

        public void CompleteFeed(bool hadNewItems)
        {
            if (hadNewItems) _hasNewItems = true;
            var remaining = Interlocked.Decrement(ref _pendingFeeds);
            if (remaining <= 0)
            {
                _lastRefreshTime = DateTime.UtcNow;
                _isRefreshing = false;
            }
        }

        public void FailRefresh()
        {
            Interlocked.Exchange(ref _pendingFeeds, 0);
            _isRefreshing = false;
        }
    }

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

    public void ResetRefreshCooldown()
    {
        // Clear all per-user cooldowns
        foreach (var state in refreshStates.Values)
        {
            // Force next refresh to proceed by clearing state
        }
        refreshStates.Clear();
    }

    public RefreshStatusResponse GetRefreshStatus(RssUser user)
    {
        var state = refreshStates.GetOrAdd(user.Id, _ => new UserRefreshState());
        return new RefreshStatusResponse
        {
            HasNewItems = state.HasNewItems,
            IsRefreshing = state.IsRefreshing,
            PendingFeeds = state.PendingFeeds
        };
    }

    public async Task AddFeedAsync(NewsFeed feed)
    {
        await ReloadCachedItemsAsync(feed);
    }

    public async Task RefreshAsync(RssUser user)
    {
        var state = refreshStates.GetOrAdd(user.Id, _ => new UserRefreshState());

        // If a refresh is already running for this user, don't start another
        if (state.IsRefreshing)
        {
            return;
        }

        bool isJustStarted = this.startupTime + this.config.CacheReloadStartupDelay > DateTime.UtcNow;
        bool isRecentRefresh = state.LastRefreshTime + this.config.CacheReloadInterval > DateTime.UtcNow;

        if (isJustStarted || isRecentRefresh)
        {
            // Cooldown active — don't fake hasNewItems, just return.
            // The status endpoint will honestly report isRefreshing=false.
            return;
        }

        try
        {
            var feeds = this.persistedFeeds.GetFeeds(user).ToList();
            var totalFeeds = feeds.Count;

            if (totalFeeds == 0)
            {
                return;
            }

            state.StartRefresh(totalFeeds);

            foreach (var feed in feeds)
            {
                await this.backgroundWorkQueue.QueueBackgroundWorkItemAsync(async token =>
                {
                    bool hadNewItems = false;
                    try
                    {
                        hadNewItems = await ReloadCachedItemsAsync(feed);
                    }
                    catch (Exception ex)
                    {
                        this.logger.LogError(ex, "Error reloading feed: {feed}", feed.Href);
                    }
                    finally
                    {
                        state.CompleteFeed(hadNewItems);
                    }
                });
            }
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Error reloading cache");
            state.FailRefresh();
        }
    }

    /// <summary>
    /// Fetches items from a feed and adds new ones to the store.
    /// Returns true if any new items were added.
    /// </summary>
    private async Task<bool> ReloadCachedItemsAsync(NewsFeed feed)
    {
        var url = feed.Href;
        var user = this.userStore.GetUserById(feed.UserId);

        if (user == null)
        {
            this.logger.LogError("User not found: {userId}", feed.UserId);
            return false;
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
                this.logger.LogDebug("Skipping existing item: {itemId} from feed: {feedUrl}", item.Id, url);
                continue;
            }

            newItems.Add(item);
        }

        if (newItems.Any())
        {
            await this.newsFeedItemStore.AddItemsAsync(newItems);
            return true;
        }

        return false;
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