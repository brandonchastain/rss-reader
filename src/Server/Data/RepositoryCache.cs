using Microsoft.Extensions.Caching.Memory;
using RssApp.Contracts;

namespace RssApp.Data;

/// <summary>
/// The memory cache behind the caching repositories, and the sizes its entries
/// carry. The cache has a size limit, so every entry needs a size: an approximate
/// byte count (strings are UTF-16, two bytes a character, plus a flat overhead per
/// object). Once the total passes the limit the cache compacts, dropping expired
/// and least recently used entries first. Without a limit, an hour of post content
/// grew the cache past what a 0.5 GiB replica can hold.
/// </summary>
public static class RepositoryCache
{
    public const long DefaultSizeLimitBytes = 32L * 1024 * 1024;

    // Rough cost of one small object: a feed, a user, an item's fields minus content.
    public const long SmallEntryBytes = 512;

    public static MemoryCache Create(long sizeLimitBytes = DefaultSizeLimitBytes)
        => new(new MemoryCacheOptions { SizeLimit = sizeLimitBytes, CompactionPercentage = 0.25 });

    public static long SizeOf(string value)
        => SmallEntryBytes + 2L * (value?.Length ?? 0);

    public static long SizeOf(NewsFeedItem item)
        => SmallEntryBytes + 2L * ((item?.Content?.Length ?? 0) + (item?.Title?.Length ?? 0));

    public static long SizeOfEntries(int count)
        => SmallEntryBytes * Math.Max(1, count);
}
