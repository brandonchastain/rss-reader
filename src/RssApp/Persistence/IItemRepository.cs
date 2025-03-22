
using RssApp.Contracts;

namespace RssApp.Persistence;

public interface IItemRepository
{
    IEnumerable<NewsFeedItem> GetItems(NewsFeed feed);

    void AddItem(NewsFeedItem item);

    void MarkAsRead(NewsFeedItem item, bool isRead);
}