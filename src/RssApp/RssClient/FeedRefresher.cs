using RssApp.Contracts;
using RssApp.Serialization;
using RssApp.Persistence;

namespace RssApp.RssClient;

public class FeedRefresher : IDisposable
{
    private const int PageSize = 10;
    private static readonly TimeSpan CacheReloadInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CacheReloadStartupDelay = TimeSpan.FromMinutes(0);
    private static readonly bool EnableHttpLookup = true;
    private HttpClient httpClient;
    private readonly RssDeserializer deserializer;
    private readonly ILogger<FeedClient> logger;
    private readonly IFeedRepository persistedFeeds;
    private readonly IItemRepository newsFeedItemStore;
    private readonly IUserRepository userStore;
    public FeedRefresher(
        HttpClient httpClient,
        RssDeserializer deserializer,
        ILogger<FeedClient> logger,
        IFeedRepository persistedFeeds,
        IItemRepository newsFeedItemStore,
        IUserRepository userStore)
    {
        this.httpClient = httpClient;
        this.deserializer = deserializer;
        this.logger = logger;
        this.persistedFeeds = persistedFeeds;
        this.newsFeedItemStore = newsFeedItemStore;
        this.userStore = userStore;
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

    private async Task RunAsync(CancellationToken token)
    {
        await Task.Delay(CacheReloadStartupDelay, token);

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

            await Task.Delay(CacheReloadInterval);
        }
    }

    private async Task ReloadCachedItemsAsync(NewsFeed feed)
    {
        var url = feed.FeedUrl;
        var user = this.userStore.GetUserById(feed.UserId);
        try
        {
            var cachedItems = this.newsFeedItemStore.GetItems(feed).ToHashSet();
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

                freshItems = this.deserializer.FromString(response, user).ToHashSet();
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
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Error reloading feeds");
        }
    }
}