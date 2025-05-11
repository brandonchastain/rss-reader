using RssApp.Contracts;
using RssApp.Serialization;
using RssApp.Data;

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
    private Exception lastRefreshException;

    public FeedRefresher(
        RssDeserializer deserializer,
        ILogger<FeedRefresher> logger,
        IFeedRepository persistedFeeds,
        IItemRepository newsFeedItemStore,
        IUserRepository userStore,
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
    }

    public void Dispose()
    {
        this.httpClient.Dispose();
    }
    private Task bgTask;

    public Task StartAsync(CancellationToken token)
    {
        this.bgTask = RunAsync(token);
        return Task.CompletedTask;
    }

    public async Task AddFeedAsync(NewsFeed feed)
    {
        await ReloadCachedItemsAsync(feed);
    }

    private async Task RunAsync(CancellationToken token)
    {
        await Task.Delay(this.cacheReloadStartupDelay, token);

        while (!token.IsCancellationRequested)
        {
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
                            await this.ReloadCachedItemsAsync(feed);
                        }
                        catch (Exception ex)
                        {
                            this.logger.LogError(ex, "Error reloading feed: {feed}", feed.FeedUrl);
                        }

                        await Task.Delay(10000);
                    }
                }
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "Error reloading cache");
            }

            await Task.Delay(this.cacheReloadInterval);
        }
    }

    private async Task ReloadCachedItemsAsync(NewsFeed feed)
    {
        var url = feed.FeedUrl;
        var user = this.userStore.GetUserById(feed.UserId);
        var freshItems = new HashSet<NewsFeedItem>();
        string response = null;

        string[] agents = ["reader.brandonchastain.com/1.1", "curl/7.79.1"];

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
        }

        var size = Math.Max(10, freshItems.Count);
        var cachedItems = (await this.newsFeedItemStore.GetItemsAsync(feed, isFilterUnread: false, isFilterSaved: false, filterTag: null, page: 0, pageSize: size)).ToHashSet();
        var newItems = freshItems.Except(cachedItems);
        this.newsFeedItemStore.AddItems(newItems);
    }
}