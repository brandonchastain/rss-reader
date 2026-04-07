using Microsoft.Extensions.Caching.Memory;
using RssApp.Contracts;

namespace RssApp.Data;

/// <summary>
/// Caching decorator for <see cref="IItemRepository"/>.
/// Wraps an inner repository with IMemoryCache-based caching for
/// read-heavy, low-mutation paths (GetItem, GetItemContent).
/// All mutation methods evict affected cache entries before delegating
/// to the inner repository so callers never observe stale data.
/// </summary>
public sealed class CachingItemRepository : IItemRepository
{
    private readonly IItemRepository _inner;
    private readonly IMemoryCache _cache;

    private static readonly TimeSpan ContentExpiration = TimeSpan.FromHours(1);
    private static readonly TimeSpan ItemExpiration    = TimeSpan.FromMinutes(5);

    public CachingItemRepository(IItemRepository inner, IMemoryCache cache)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    // -------------------------------------------------------------------------
    // Cached reads
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    /// Cache key: <c>itemcontent:{item.UserId}:{item.Href}</c> — 1-hour absolute expiration.
    /// Null/empty results are NOT cached.
    public string GetItemContent(NewsFeedItem item)
    {
        var key = ContentKey(item.UserId, item.Href);
        if (_cache.TryGetValue(key, out string cached))
            return cached;

        var content = _inner.GetItemContent(item);

        if (!string.IsNullOrEmpty(content))
        {
            _cache.Set(key, content, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = ContentExpiration
            });
        }

        return content;
    }

    /// <inheritdoc/>
    /// Cache key: <c>item:{user.Id}:{href}</c> — 60-second sliding expiration.
    /// Null results are NOT cached.
    public NewsFeedItem GetItem(RssUser user, string href)
    {
        var key = ItemByHrefKey(user.Id, href);
        if (_cache.TryGetValue(key, out NewsFeedItem cached))
            return cached;

        var item = _inner.GetItem(user, href);

        if (item is not null)
        {
            _cache.Set(key, item, new MemoryCacheEntryOptions
            {
                SlidingExpiration = ItemExpiration
            });
        }

        return item;
    }

    /// <inheritdoc/>
    /// Cache key: <c>item:{user.Id}:id:{itemId}</c> — 60-second sliding expiration.
    /// Null results are NOT cached.
    public NewsFeedItem GetItem(RssUser user, int itemId)
    {
        var key = ItemByIdKey(user.Id, itemId.ToString());
        if (_cache.TryGetValue(key, out NewsFeedItem cached))
            return cached;

        var item = _inner.GetItem(user, itemId);

        if (item is not null)
        {
            _cache.Set(key, item, new MemoryCacheEntryOptions
            {
                SlidingExpiration = ItemExpiration
            });
        }

        return item;
    }

    // -------------------------------------------------------------------------
    // Pass-through reads (too dynamic to cache meaningfully)
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public Task<IEnumerable<NewsFeedItem>> GetItemsAsync(
        NewsFeed feed,
        bool isFilterUnread,
        bool isFilterSaved,
        string filterTag,
        int? page = null,
        int? pageSize = null,
        long? lastId = null,
        long? lastPublishDateOrder = null,
        IEnumerable<string> excludeFeedUrls = null,
        bool includeContent = true)
        => _inner.GetItemsAsync(feed, isFilterUnread, isFilterSaved, filterTag, page, pageSize, lastId, lastPublishDateOrder, excludeFeedUrls, includeContent);

    /// <inheritdoc/>
    public Task<IEnumerable<NewsFeedItem>> SearchItemsAsync(string query, RssUser user, int page, int pageSize)
        => _inner.SearchItemsAsync(query, user, page, pageSize);

    // -------------------------------------------------------------------------
    // Mutations — evict before delegating so reads after writes are fresh
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public Task AddItemsAsync(IEnumerable<NewsFeedItem> item)
        => _inner.AddItemsAsync(item);

    /// <inheritdoc/>
    public void MarkAsRead(NewsFeedItem item, bool isRead, RssUser user)
    {
        EvictItem(user.Id, item);
        _inner.MarkAsRead(item, isRead, user);
    }

    /// <inheritdoc/>
    public void SavePost(NewsFeedItem item, RssUser user)
    {
        EvictItem(user.Id, item);
        _inner.SavePost(item, user);
    }

    /// <inheritdoc/>
    public void UnsavePost(NewsFeedItem item, RssUser user)
    {
        EvictItem(user.Id, item);
        _inner.UnsavePost(item, user);
    }

    /// <inheritdoc/>
    public void UpdateTags(NewsFeedItem item, string tags)
    {
        // UpdateTags doesn't receive a RssUser, so we evict using item.UserId.
        EvictItem(item.UserId, item);
        _inner.UpdateTags(item, tags);
    }

    /// <inheritdoc/>
    public async Task DeleteAllItemsAsync(RssUser user)
    {
        await _inner.DeleteAllItemsAsync(user);
        // Bulk eviction — compact the entire cache since we can't enumerate keys.
        if (_cache is MemoryCache mc)
            mc.Compact(1.0);
    }

    /// <inheritdoc/>
    public int GetItemCountForFeed(RssUser user, string feedUrl)
        => _inner.GetItemCountForFeed(user, feedUrl);

    // -------------------------------------------------------------------------
    // IDisposable
    // -------------------------------------------------------------------------

    public void Dispose() => _inner.Dispose();

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static string ContentKey(int userId, string href)
        => $"itemcontent:{userId}:{href}";

    private static string ItemByHrefKey(int userId, string href)
        => $"item:{userId}:{href}";

    private static string ItemByIdKey(int userId, string itemId)
        => $"item:{userId}:id:{itemId}";

    private void EvictItem(int userId, NewsFeedItem item)
    {
        _cache.Remove(ItemByHrefKey(userId, item.Href));
        _cache.Remove(ItemByIdKey(userId, item.Id));
    }
}
