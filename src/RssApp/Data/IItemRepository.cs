using RssApp.Contracts;

namespace RssApp.Data;

public interface IItemRepository
{
    Task<IEnumerable<NewsFeedItem>> GetItemsAsync(NewsFeed feed, bool isFilterUnread, bool isFilterSaved, string filterTag, int? page, int? pageSize);
    NewsFeedItem GetItem(RssUser user, string href);
    void AddItems(IEnumerable<NewsFeedItem> item);
    void MarkAsRead(NewsFeedItem item, bool isRead);
    void SavePost(NewsFeedItem item, RssUser user);
    void UnsavePost(NewsFeedItem item, RssUser user);
}