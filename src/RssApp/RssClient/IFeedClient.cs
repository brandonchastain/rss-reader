using Microsoft.Extensions.Caching.Memory;
using RssApp.Contracts;
using RssApp.Serialization;
using RssApp.Persistence;

namespace RssApp.RssClient;

public interface IFeedClient
{
    IEnumerable<NewsFeedItem> GetFeedItems(IEnumerable<NewsFeed> urls, int page);
    IEnumerable<NewsFeedItem> GetFeedItems(NewsFeed feed, int page);
    void HidePost(string href);
    Task MarkAsReadAsync(NewsFeedItem item, bool isRead);
}