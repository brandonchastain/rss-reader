using RssApp.Contracts;

namespace RssApp.Data;

public interface IItemRepository : IDisposable
{
    Task<IEnumerable<NewsFeedItem>> GetItemsAsync(NewsFeed feed, bool isFilterUnread, bool isFilterSaved, string filterTag, int? page = null, int? pageSize = null, long? lastId = null, string lastPublishDate = null);
    //Task<IEnumerable<NewsFeedItem>> GetItemsWithCursorAsync(NewsFeed feed, bool isFilterUnread, bool isFilterSaved, string filterTag, int pageSize, long? lastId = null, string? lastPublishDate = null);
    Task<IEnumerable<NewsFeedItem>> SearchItemsAsync(string query, RssUser user, int page, int pageSize);
    NewsFeedItem GetItem(RssUser user, string href);
    NewsFeedItem GetItem(RssUser user, int itemId);
    void AddItems(IEnumerable<NewsFeedItem> item);
    void MarkAsRead(NewsFeedItem item, bool isRead, RssUser user);
    void SavePost(NewsFeedItem item, RssUser user);
    void UnsavePost(NewsFeedItem item, RssUser user);
    void UpdateTags(NewsFeedItem item, string tags);
    string GetItemContent(NewsFeedItem item);
}