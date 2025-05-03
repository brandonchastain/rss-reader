using RssApp.Contracts;

namespace RssApp.RssClient;

public interface IFeedClient
{
    Task<IEnumerable<NewsFeed>> GetFeedsAsync();
    Task AddFeedAsync(NewsFeed feed);
    Task<IEnumerable<NewsFeedItem>> GetTimelineAsync(int page, int pageSize = 10);
    Task<IEnumerable<NewsFeedItem>> GetFeedItemsAsync(NewsFeed feed, int page);
    void MarkAsRead(NewsFeedItem item, bool isRead);
    Task<RssUser> RegisterUserAsync(string username);
    IEnumerable<string> GetUserTags(RssUser user);
    Task SavePostAsync(NewsFeedItem item);
    bool IsFilterUnread { get; set; }
    string FilterTag { get; set; }
    bool IsFilterSaved { get; set; }
}