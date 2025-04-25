using RssApp.Contracts;

namespace RssApp.RssClient;

public interface IFeedClient
{
    string FilterTag { get; set; }
    Task<IEnumerable<NewsFeedItem>> GetTimelineAsync(int page, int pageSize = 10);
    Task<IEnumerable<NewsFeedItem>> GetFeedItemsAsync(NewsFeed feed, int page);
    void MarkAsRead(NewsFeedItem item, bool isRead);
    bool IsFilterUnread { get; set; }
    Task<RssUser> RegisterUserAsync(string username);
    Task AddFeedAsync(NewsFeed feed);
    IEnumerable<string> GetUserTags(RssUser user);
}