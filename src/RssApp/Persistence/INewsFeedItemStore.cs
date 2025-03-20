
using RssApp.Contracts;

namespace RssApp.Persistence;

public interface INewsFeedItemStore
{
    IEnumerable<NewsFeedItem> GetItems(string url);

    void AddItem(NewsFeedItem item);

    void MarkAsRead(NewsFeedItem item, bool isRead);
}