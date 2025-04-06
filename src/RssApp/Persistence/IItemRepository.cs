
using RssApp.Contracts;

namespace RssApp.Persistence;

public interface IItemRepository
{
    Task<IEnumerable<NewsFeedItem>> GetItemsAsync(NewsFeed feed, string filterTag, int? page, int? pageSize);
    NewsFeedItem GetItem(RssUser user, string href);

    void AddItems(IEnumerable<NewsFeedItem> item);

    void MarkAsRead(NewsFeedItem item, bool isRead);
}