using RssApp.Contracts;
using RssApp.Serialization;
using RssApp.Persistence;

namespace RssApp.RssClient;

public class FeedRefresher : IDisposable
{
    private const int PageSize = 10;
    private static readonly bool EnableHttpLookup = true;
    private HttpClient httpClient;
    private readonly RssDeserializer deserializer;
    private readonly ILogger<FeedClient> logger;
    private readonly IFeedRepository persistedFeeds;
    private readonly IItemRepository newsFeedItemStore;
    private readonly IUserRepository userStore;
    private readonly TimeSpan cacheReloadInterval;
    private readonly TimeSpan cacheReloadStartupDelay;
    private SemaphoreSlim semaphore = new SemaphoreSlim(1, 1);
    public FeedRefresher(
        HttpClient httpClient,
        RssDeserializer deserializer,
        ILogger<FeedClient> logger,
        IFeedRepository persistedFeeds,
        IItemRepository newsFeedItemStore,
        IUserRepository userStore,
        TimeSpan? cacheReloadInterval = null,
        TimeSpan? cacheReloadStartupDelay = null)
    {
        cacheReloadInterval ??= TimeSpan.FromMinutes(10);
        cacheReloadStartupDelay ??= TimeSpan.FromMinutes(10000);
        
        this.httpClient = httpClient;
        this.deserializer = deserializer;
        this.logger = logger;
        this.persistedFeeds = persistedFeeds;
        this.newsFeedItemStore = newsFeedItemStore;
        this.userStore = userStore;
        this.cacheReloadInterval = cacheReloadInterval.Value;
        this.cacheReloadStartupDelay = cacheReloadStartupDelay.Value;
    }

    public void Dispose()
    {
        this.httpClient.Dispose();
        this.semaphore.Dispose();
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
                        await this.ReloadCachedItemsAsync(feed);
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
        this.logger.LogInformation($"Waiting for lock...");

        await this.semaphore.WaitAsync();

        this.logger.LogInformation($"Lock acquired.");

        try
        {
            var url = feed.FeedUrl;
            var user = this.userStore.GetUserById(feed.UserId);
            var cachedItems = this.newsFeedItemStore.GetItems(feed).ToHashSet();
            var freshItems = new HashSet<NewsFeedItem>();
            string response = null;

            try
            {
                if (EnableHttpLookup)
                {
                    var browserRequest = new HttpRequestMessage(HttpMethod.Get, url);
                    browserRequest.Headers.UserAgent.ParseAdd("curl/7.79.1");
                    browserRequest.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
                    var httpRes = await this.httpClient.SendAsync(browserRequest);
                    response = await httpRes.Content.ReadAsStringAsync();

                    if (string.IsNullOrEmpty(response))
                    {
                        this.logger.LogWarning($"Empty response when refreshing feed: {url}");
                        return;
                    }

                    freshItems = this.deserializer.FromString(response, user).ToHashSet();
                }
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "Error reloading feeds. Bad RSS response.\n{url}\n{response}", url, response);
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
        }
        finally
        {
            this.semaphore.Release();
        }
    }
}