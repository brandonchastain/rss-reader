using Microsoft.Extensions.Caching.Memory;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RssApp.Contracts;
using RssApp.Data;

namespace SerializerTests;

// ---------------------------------------------------------------------------
// Manual fake — no Moq dependency
// ---------------------------------------------------------------------------

internal sealed class FakeFeedRepository : IFeedRepository
{
    // Backing store — tests can mutate this to simulate data changes.
    public List<NewsFeed> Feeds { get; } = new();

    // Call counters so tests can assert how many times each method was called.
    public int GetFeedsCallCount { get; private set; }
    public int GetFeedCallCount { get; private set; }
    public int AddFeedCallCount { get; private set; }
    public int AddFeedsCallCount { get; private set; }
    public int DeleteFeedCallCount { get; private set; }
    public int AddTagCallCount { get; private set; }

    public IEnumerable<NewsFeed> GetFeeds(RssUser user)
    {
        GetFeedsCallCount++;
        // Return a new list each time so lazy-vs-materialized bugs surface clearly.
        return Feeds.Where(f => f.UserId == user.Id).ToList();
    }

    public NewsFeed GetFeed(RssUser user, string url)
    {
        GetFeedCallCount++;
        return Feeds.FirstOrDefault(f => f.UserId == user.Id &&
                                         string.Equals(f.Href, url, StringComparison.OrdinalIgnoreCase))!;
    }

    public void AddFeed(NewsFeed feed)
    {
        AddFeedCallCount++;
        Feeds.Add(feed);
    }

    public void AddFeeds(RssUser user, IEnumerable<NewsFeed> feeds)
    {
        AddFeedsCallCount++;
        Feeds.AddRange(feeds);
    }

    public void DeleteFeed(RssUser user, string url)
    {
        DeleteFeedCallCount++;
        Feeds.RemoveAll(f => f.UserId == user.Id &&
                              string.Equals(f.Href, url, StringComparison.OrdinalIgnoreCase));
    }

    public void AddTag(NewsFeed feed, string tag)
    {
        AddTagCallCount++;
        var existing = Feeds.FirstOrDefault(f => f.FeedId == feed.FeedId);
        existing?.Tags?.Add(tag);
    }

    public IEnumerable<TagSetting> GetTagSettings(RssUser user) => Enumerable.Empty<TagSetting>();
    public void SetTagHidden(RssUser user, string tag, bool isHidden) { }
    public IEnumerable<string> GetHiddenFeedUrls(RssUser user) => Enumerable.Empty<string>();
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

file static class CacheFactory
{
    public static IMemoryCache Create() => new MemoryCache(new MemoryCacheOptions());
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

[TestClass]
public sealed class CachingFeedRepositoryTests
{
    private static readonly RssUser User1 = new("alice", 1);

    private static NewsFeed MakeFeed(int id, string href, int userId = 1) =>
        new(id, href, userId) { Tags = new List<string>() };

    // 1. GetFeeds — second call hits the cache, not the inner repository.
    [TestMethod]
    public void GetFeeds_ReturnsCachedListOnSecondCall()
    {
        var fake = new FakeFeedRepository();
        fake.Feeds.Add(MakeFeed(1, "https://example.com/feed"));

        var sut = new CachingFeedRepository(fake, CacheFactory.Create());

        var first = sut.GetFeeds(User1).ToList();
        var second = sut.GetFeeds(User1).ToList();

        Assert.AreEqual(1, fake.GetFeedsCallCount, "Inner should be called exactly once.");
        Assert.AreEqual(first.Count, second.Count);
        Assert.AreEqual(first[0].FeedId, second[0].FeedId);
    }

    // 2. GetFeed — second call hits the cache, not the inner repository.
    [TestMethod]
    public void GetFeed_ReturnsCachedValueOnSecondCall()
    {
        const string url = "https://example.com/feed";
        var fake = new FakeFeedRepository();
        fake.Feeds.Add(MakeFeed(1, url));

        var sut = new CachingFeedRepository(fake, CacheFactory.Create());

        var first = sut.GetFeed(User1, url);
        var second = sut.GetFeed(User1, url);

        Assert.AreEqual(1, fake.GetFeedCallCount, "Inner should be called exactly once.");
        Assert.AreEqual(first?.FeedId, second?.FeedId);
    }

    // 3. AddFeed — should evict GetFeeds cache so next call refreshes from inner.
    [TestMethod]
    public void AddFeed_InvalidatesGetFeedsCache()
    {
        var fake = new FakeFeedRepository();
        fake.Feeds.Add(MakeFeed(1, "https://example.com/feed1"));

        var sut = new CachingFeedRepository(fake, CacheFactory.Create());

        // Prime the cache.
        _ = sut.GetFeeds(User1).ToList();
        Assert.AreEqual(1, fake.GetFeedsCallCount);

        // Write should bust the cache.
        sut.AddFeed(MakeFeed(2, "https://example.com/feed2"));

        // Next read must hit inner again.
        _ = sut.GetFeeds(User1).ToList();
        Assert.AreEqual(2, fake.GetFeedsCallCount, "Inner should be called again after AddFeed.");
    }

    // 4. DeleteFeed — should evict both GetFeeds and GetFeed caches.
    [TestMethod]
    public void DeleteFeed_InvalidatesGetFeedsCache()
    {
        const string url = "https://example.com/feed";
        var fake = new FakeFeedRepository();
        fake.Feeds.Add(MakeFeed(1, url));

        var sut = new CachingFeedRepository(fake, CacheFactory.Create());

        // Prime both caches.
        _ = sut.GetFeeds(User1).ToList();
        _ = sut.GetFeed(User1, url);
        Assert.AreEqual(1, fake.GetFeedsCallCount);
        Assert.AreEqual(1, fake.GetFeedCallCount);

        // Delete should bust both entries.
        sut.DeleteFeed(User1, url);

        _ = sut.GetFeeds(User1).ToList();
        _ = sut.GetFeed(User1, url);

        Assert.AreEqual(2, fake.GetFeedsCallCount, "GetFeeds inner should be called again after DeleteFeed.");
        Assert.AreEqual(2, fake.GetFeedCallCount, "GetFeed inner should be called again after DeleteFeed.");
    }

    // 5. AddTag — should evict both GetFeeds and GetFeed caches for that feed.
    [TestMethod]
    public void AddTag_InvalidatesFeedCaches()
    {
        const string url = "https://example.com/feed";
        var feed = MakeFeed(1, url);
        var fake = new FakeFeedRepository();
        fake.Feeds.Add(feed);

        var sut = new CachingFeedRepository(fake, CacheFactory.Create());

        // Prime both caches.
        _ = sut.GetFeeds(User1).ToList();
        _ = sut.GetFeed(User1, url);
        Assert.AreEqual(1, fake.GetFeedsCallCount);
        Assert.AreEqual(1, fake.GetFeedCallCount);

        // AddTag should bust both.
        sut.AddTag(feed, "tech");

        _ = sut.GetFeeds(User1).ToList();
        _ = sut.GetFeed(User1, url);

        Assert.AreEqual(2, fake.GetFeedsCallCount, "GetFeeds inner should be called again after AddTag.");
        Assert.AreEqual(2, fake.GetFeedCallCount, "GetFeed inner should be called again after AddTag.");
    }

    // 6. GetFeeds must materialize the enumerable — mutating the source after caching
    //    must NOT change the cached result returned on the second call.
    [TestMethod]
    public void GetFeeds_MaterializesEnumerable()
    {
        var fake = new FakeFeedRepository();
        fake.Feeds.Add(MakeFeed(1, "https://example.com/feed1"));

        var sut = new CachingFeedRepository(fake, CacheFactory.Create());

        // First call — result is cached.
        var first = sut.GetFeeds(User1).ToList();
        Assert.AreEqual(1, first.Count);

        // Mutate the source AFTER the first cached call.
        fake.Feeds.Add(MakeFeed(2, "https://example.com/feed2"));

        // Second call must still return the snapshot that was cached — not the new item.
        var second = sut.GetFeeds(User1).ToList();
        Assert.AreEqual(1, fake.GetFeedsCallCount, "Inner should NOT be called again — result is cached.");
        Assert.AreEqual(1, second.Count, "Cached list should still reflect the snapshot at cache time.");
    }
}
