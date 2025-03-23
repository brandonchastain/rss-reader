
using RssApp.Contracts;

namespace RssApp.Persistence;

public interface IItemRepository
{
    IEnumerable<NewsFeedItem> GetItems(NewsFeed feed);
    NewsFeedItem GetItem(string href);

    void AddItem(NewsFeedItem item);

    void MarkAsRead(NewsFeedItem item, bool isRead);
}