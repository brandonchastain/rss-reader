using Microsoft.Extensions.Caching.Memory;
using RssApp.Contracts;
using RssApp.Serialization;
using RssApp.Persistence;

namespace RssApp.RssClient;

public interface IFeedClient
{
    IEnumerable<NewsFeedItem> GetFeedItems(IEnumerable<string> urls, int page);
    IEnumerable<NewsFeedItem> GetFeedItems(string url, int page);
    void HidePost(string href);
    Task ReloadCachedItemsAsync(string url);
}