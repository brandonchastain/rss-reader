using Microsoft.Extensions.Caching.Memory;
using RssApp.Contracts;

namespace RssApp.Data;

public class CachingFeedRepository : IFeedRepository
{
    private readonly IFeedRepository _inner;
    private readonly IMemoryCache _cache;

    private static readonly TimeSpan SlidingExpiration = TimeSpan.FromMinutes(5);

    public CachingFeedRepository(IFeedRepository inner, IMemoryCache cache)
    {
        _inner = inner;
        _cache = cache;
    }

    // --- Cache key helpers ---

    private static string FeedsKey(int userId) => $"feeds:{userId}";
    private static string FeedKey(int userId, string url) => $"feed:{userId}:{url}";

    // --- Read methods (cache-aside) ---

    public IEnumerable<NewsFeed> GetFeeds(RssUser user)
    {
        var key = FeedsKey(user.Id);
        if (_cache.TryGetValue(key, out List<NewsFeed> cached))
            return cached;

        // Materialize to List<NewsFeed> before caching — prevents caching a dead lazy enumerator.
        var result = _inner.GetFeeds(user).ToList();

        _cache.Set(key, result, new MemoryCacheEntryOptions
        {
            SlidingExpiration = SlidingExpiration
        });

        return result;
    }

    public NewsFeed GetFeed(RssUser user, string url)
    {
        var key = FeedKey(user.Id, url);
        if (_cache.TryGetValue(key, out NewsFeed cached))
            return cached;

        var result = _inner.GetFeed(user, url);

        if (result is not null)
        {
            _cache.Set(key, result, new MemoryCacheEntryOptions
            {
                SlidingExpiration = SlidingExpiration
            });
        }

        return result;;
    }

    // --- Write methods (evict then delegate) ---

    public void AddFeed(NewsFeed feed)
    {
        _cache.Remove(FeedsKey(feed.UserId));
        _cache.Remove(FeedKey(feed.UserId, feed.Href));
        _inner.AddFeed(feed);
    }

    public void AddFeeds(RssUser user, IEnumerable<NewsFeed> feeds)
    {
        _cache.Remove(FeedsKey(user.Id));
        _inner.AddFeeds(user, feeds);
    }

    public void DeleteFeed(RssUser user, string url)
    {
        _cache.Remove(FeedsKey(user.Id));
        _cache.Remove(FeedKey(user.Id, url));
        _inner.DeleteFeed(user, url);
    }

    public void AddTag(NewsFeed feed, string tag)
    {
        _cache.Remove(FeedsKey(feed.UserId));
        _cache.Remove(FeedKey(feed.UserId, feed.Href));
        _inner.AddTag(feed, tag);
    }

    // --- Tag settings (pass-through, no caching) ---

    public IEnumerable<TagSetting> GetTagSettings(RssUser user)
        => _inner.GetTagSettings(user);

    public void SetTagHidden(RssUser user, string tag, bool isHidden)
    {
        _cache.Remove(FeedsKey(user.Id));
        _inner.SetTagHidden(user, tag, isHidden);
    }

    public IEnumerable<string> GetHiddenFeedUrls(RssUser user)
        => _inner.GetHiddenFeedUrls(user);

    public void DeleteAllFeeds(RssUser user)
    {
        _inner.DeleteAllFeeds(user);
        _cache.Remove(FeedsKey(user.Id));
    }
}
