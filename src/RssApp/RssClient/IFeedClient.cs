using RssApp.Contracts;

namespace RssApp.RssClient;

public interface IFeedClient
{
    Task<IEnumerable<NewsFeedItem>> GetTimelineAsync(int page);
    Task<IEnumerable<NewsFeedItem>> GetFeedItemsAsync(NewsFeed feed, int page);
    void HidePost(string href);
    void MarkAsRead(NewsFeedItem item, bool isRead);
    bool IsFilterUnread { get; set; }
    Task<RssUser> GetLoggedInUserAsync();
}