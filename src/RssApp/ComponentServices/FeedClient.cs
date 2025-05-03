using System.Diagnostics;
using RssApp.Contracts;
using RssApp.Data;

namespace RssApp.RssClient;

public class FeedClient : IFeedClient, IDisposable
{
    private const int PageSize = 10;
    private HttpClient httpClient;
    private readonly ILogger<FeedClient> logger;
    private readonly IFeedRepository persistedFeeds;
    private readonly IItemRepository newsFeedItemStore;
    private readonly IUserRepository userStore;
    private RssUser loggedInUser;
    private FeedRefresher feedRefresher;
    private bool isFilterUnread;
    private string filterTag;
    private bool isFilterSaved;

    public FeedClient(
        HttpClient httpClient,
        ILogger<FeedClient> logger,
        IFeedRepository persistedFeeds,
        IItemRepository newsFeedItemStore,
        IUserRepository userStore,
        FeedRefresher feedRefresher)
    {
        this.httpClient = httpClient;
        this.logger = logger;
        this.persistedFeeds = persistedFeeds;
        this.newsFeedItemStore = newsFeedItemStore;
        this.userStore = userStore;
        this.feedRefresher = feedRefresher;
    }

// todo: apply isfilterunread to sql query logic (currently ui only)
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
            this.filterTag = value;
        }
    }

    public bool IsFilterSaved
    {
        get { return this.isFilterSaved; }
        set { this.isFilterSaved = value; }
    }

    public IEnumerable<string> GetUserTags(RssUser user)
    {
        var sw = Stopwatch.StartNew();
        var tags = this.persistedFeeds.GetFeeds(user)
            .SelectMany(f => f.Tags)
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct();
        //this.logger.LogInformation($"GetUserTags took {sw.ElapsedMilliseconds}ms");
        return tags;
    }

    public async Task<IEnumerable<NewsFeed>> GetFeedsAsync()
    {
        await Task.Yield();
        var sw = Stopwatch.StartNew();
        var feeds = this.persistedFeeds.GetFeeds(this.loggedInUser);
        //this.logger.LogInformation($"GetFeeds took {sw.ElapsedMilliseconds}ms");
        return feeds;
    }

    public async Task AddFeedAsync(NewsFeed feed)
    {
        await this.feedRefresher.AddFeedAsync(feed);
    }

    public async Task<IEnumerable<NewsFeedItem>> GetTimelineAsync(int page, int pageSize = PageSize)
    {
        var sw = Stopwatch.StartNew();
        var items = await this.GetFeedItemsHelperAsync(new NewsFeed("%", this.loggedInUser.Id), page, pageSize);
        var sorted = items
            .DistinctBy(i => i.GetHashCode())
            .OrderByDescending(i => i.ParsedDate);

        this.logger.LogInformation($"GetTimeline took {sw.ElapsedMilliseconds}ms");
        return sorted;
    }

    public async Task<IEnumerable<NewsFeedItem>> GetFeedItemsAsync(NewsFeed feed, int page)
    {
        var sw = Stopwatch.StartNew();
        var items = await this.GetFeedItemsHelperAsync(feed, page);
        this.logger.LogInformation($"GetFeedItems took {sw.ElapsedMilliseconds}ms");
        return items;
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

    public async Task SavePostAsync(NewsFeedItem item)
    {
        await Task.Yield();
        this.newsFeedItemStore.SavePost(item, this.loggedInUser);
    }

    private async Task<IEnumerable<NewsFeedItem>> GetFeedItemsHelperAsync(NewsFeed feed, int page, int pageSize = PageSize)
    {
        var response = (await this.newsFeedItemStore.GetItemsAsync(feed, this.IsFilterUnread, this.IsFilterSaved, this.filterTag, page, pageSize)).ToHashSet();
        var items = response.ToList();

        var result = items.DistinctBy(i => i.Href)
            .OrderByDescending(i => i.ParsedDate)
            .Where(i => this.filterTag == null || i.FeedTags.Contains(this.filterTag));

        return result;
    }     
}