using Microsoft.Extensions.Caching.Memory;
using RssApp.Contracts;
using RssApp.Persistence;

namespace RssApp.RssClient;

public class FeedClient : IFeedClient, IDisposable
{
    private const int PageSize = 10;
    private HttpClient httpClient;
    private readonly ILogger<FeedClient> logger;
    private readonly PersistedHiddenItems hiddenItems;
    private readonly IFeedRepository persistedFeeds;
    private readonly IItemRepository newsFeedItemStore;
    private readonly IUserRepository userStore;
    private IMemoryCache memoryCache;
    private RssUser loggedInUser;
    private FeedRefresher feedRefresher;
    private bool isFilterUnread;
    private string filterTag;

    public FeedClient(
        HttpClient httpClient,
        PersistedHiddenItems hiddenItems,
        ILogger<FeedClient> logger,
        IFeedRepository persistedFeeds,
        IItemRepository newsFeedItemStore,
        IUserRepository userStore,
        FeedRefresher feedRefresher)
    {
        this.httpClient = httpClient;
        this.hiddenItems = hiddenItems;
        this.logger = logger;
        this.persistedFeeds = persistedFeeds;
        this.newsFeedItemStore = newsFeedItemStore;
        this.userStore = userStore;
        this.feedRefresher = feedRefresher;
        this.memoryCache = new MemoryCache(new MemoryCacheOptions());
    }

    public bool IsFilterUnread
    {
        get
        {
            return this.isFilterUnread;
        }
        set
        {
            this.isFilterUnread = value;
        }
    }

    public string FilterTag { 
        get
        {
            return this.filterTag;
        } 
        set
        {
            this.memoryCache = new MemoryCache(new MemoryCacheOptions());
            this.filterTag = value;
        }
    }

    public IEnumerable<string> GetUserTags(RssUser user)
    {
        var tags = this.persistedFeeds.GetFeeds(user).SelectMany(f => f.Tags).Where(f => !string.IsNullOrWhiteSpace(f));
        return tags;
    }

    public async Task<IEnumerable<NewsFeed>> GetFeedsAsync()
    {
        await Task.Yield();
        var feeds = this.persistedFeeds.GetFeeds(this.loggedInUser);
        return feeds;
    }

    public async Task AddFeedAsync(NewsFeed feed)
    {
        await this.feedRefresher.AddFeedAsync(feed);
    }

    public async Task<IEnumerable<NewsFeedItem>> GetTimelineAsync(int page)
    {
        var items = new List<NewsFeedItem>();
        var feeds = await this.GetFeedsAsync();
        
        foreach (var feed in feeds)
        {
            items.AddRange(await this.GetFeedItemsHelperAsync(feed));
        }
        
        var sorted = items
            .DistinctBy(i => i.GetHashCode())
            .OrderByDescending(i => i.ParsedDate);

        return sorted
            .Skip(page * PageSize)
            .Take(PageSize);
    }

    public async Task<IEnumerable<NewsFeedItem>> GetFeedItemsAsync(NewsFeed feed, int page)
    {
        var items = await this.GetFeedItemsHelperAsync(feed);
        return items
            .Skip(page * PageSize)
            .Take(PageSize);
    }

    public void HidePost(string id)
    {
        this.hiddenItems.HidePost(id);
    }

    public void MarkAsRead(NewsFeedItem item, bool isRead)
    {
        this.newsFeedItemStore.MarkAsRead(item, isRead);
    }

    public void Dispose()
    {
        this.httpClient.Dispose();
    }

    public async Task<RssUser> RegisterUserAsync(string username)
    {
        await Task.Yield();
        RssUser user = null;

        try
        {
            user = this.userStore.GetUserByName(username);

            if (user == null)
            {
                user = this.userStore.AddUser(username);
            }
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Error getting logged in user");
        }

        this.loggedInUser = user;
        return user;
    }

    private async Task<IEnumerable<NewsFeedItem>> GetFeedItemsHelperAsync(NewsFeed feed)
    {
        var hidden = this.hiddenItems.GetHidden();
        
        if (this.memoryCache.TryGetValue(feed.FeedUrl, out IEnumerable<NewsFeedItem> cachedItems))
        {
            return cachedItems
                .Where(i => !hidden.Contains(i.Href));
        }
        
        var url = feed.FeedUrl;
        var response = (await this.newsFeedItemStore.GetItemsAsync(feed)).ToHashSet();
        var items = response.ToList();

        var result = items.DistinctBy(i => i.Href)
            .OrderByDescending(i => i.ParsedDate)
            .Where(i => this.filterTag == null || i.FeedTags.Contains(this.filterTag));

        this.memoryCache.Set(feed.FeedUrl, result, TimeSpan.FromMinutes(5));
        return result
            .Where(i => !hidden.Contains(i.Href));
    }     
}