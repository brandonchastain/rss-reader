using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using RssApp.Contracts;
using RssApp.Data;

namespace SerializerTests;

// ---------------------------------------------------------------------------
// Manual fake — no Moq dependency
// ---------------------------------------------------------------------------

internal sealed class FakeItemRepository : IItemRepository
{
    // Call counters
    public int GetItemContentCallCount { get; private set; }
    public int GetItemByHrefCallCount  { get; private set; }
    public int GetItemByIdCallCount    { get; private set; }
    public int GetItemsAsyncCallCount  { get; private set; }
    public int MarkAsReadCallCount     { get; private set; }
    public int SavePostCallCount       { get; private set; }
    public int UnsavePostCallCount     { get; private set; }
    public int UpdateTagsCallCount     { get; private set; }
    public int DisposeCallCount        { get; private set; }

    // Configurable return values
    public string?      ContentToReturn       { get; set; } = "some content";
    public NewsFeedItem? ItemByHrefToReturn   { get; set; }
    public NewsFeedItem? ItemByIdToReturn     { get; set; }

    public string GetItemContent(NewsFeedItem item)
    {
        GetItemContentCallCount++;
        return ContentToReturn!;
    }

    public NewsFeedItem GetItem(RssUser user, string href)
    {
        GetItemByHrefCallCount++;
        return ItemByHrefToReturn!;
    }

    public NewsFeedItem GetItem(RssUser user, int itemId)
    {
        GetItemByIdCallCount++;
        return ItemByIdToReturn!;
    }

    public Task<IEnumerable<NewsFeedItem>> GetItemsAsync(
        NewsFeed feed, bool isFilterUnread, bool isFilterSaved,
        string filterTag, int? page, int? pageSize, long? lastId, long? lastPublishDateOrder,
        IEnumerable<string> excludeFeedUrls = null)
    {
        GetItemsAsyncCallCount++;
        return Task.FromResult<IEnumerable<NewsFeedItem>>(Array.Empty<NewsFeedItem>());
    }

    public Task<IEnumerable<NewsFeedItem>> SearchItemsAsync(string query, RssUser user, int page, int pageSize)
        => Task.FromResult<IEnumerable<NewsFeedItem>>(Array.Empty<NewsFeedItem>());

    public Task AddItemsAsync(IEnumerable<NewsFeedItem> item)
        => Task.CompletedTask;

    public void MarkAsRead(NewsFeedItem item, bool isRead, RssUser user) => MarkAsReadCallCount++;
    public void SavePost(NewsFeedItem item, RssUser user)                => SavePostCallCount++;
    public void UnsavePost(NewsFeedItem item, RssUser user)              => UnsavePostCallCount++;
    public void UpdateTags(NewsFeedItem item, string tags)               => UpdateTagsCallCount++;
    public Task DeleteAllItemsAsync(RssUser user)                        => Task.CompletedTask;
    public void Dispose()                                                => DisposeCallCount++;
}

// ---------------------------------------------------------------------------
// Test helpers
// ---------------------------------------------------------------------------

file static class TestData
{
    public static RssUser      User()    => new("alice", 42);
    public static NewsFeedItem Item()    => new("99", 42, "Title", "https://example.com/post", null, null, null, null) { FeedUrl = "https://feed.example.com" };
    public static NewsFeed     Feed()    => new(1, "https://feed.example.com", 42);
    public static IMemoryCache NewCache()
        => new MemoryCache(new MemoryCacheOptions());
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

[TestClass]
public sealed class CachingItemRepositoryTests
{
    // 1 -----------------------------------------------------------------------
    [TestMethod]
    public void GetItemContent_ReturnsCachedOnSecondCall()
    {
        var fake  = new FakeItemRepository { ContentToReturn = "article body" };
        var sut   = new CachingItemRepository(fake, TestData.NewCache());
        var item  = TestData.Item();

        var first  = sut.GetItemContent(item);
        var second = sut.GetItemContent(item);

        Assert.AreEqual("article body", first);
        Assert.AreEqual("article body", second);
        Assert.AreEqual(1, fake.GetItemContentCallCount, "Inner should be called exactly once; second call must be served from cache.");
    }

    // 2 -----------------------------------------------------------------------
    [TestMethod]
    public void GetItemContent_DoesNotCacheNullOrEmpty()
    {
        var fake = new FakeItemRepository();
        var sut  = new CachingItemRepository(fake, TestData.NewCache());
        var item = TestData.Item();

        // Test null
        fake.ContentToReturn = null;
        sut.GetItemContent(item);
        sut.GetItemContent(item);
        Assert.AreEqual(2, fake.GetItemContentCallCount, "Null content must not be cached — inner should be called both times.");

        // Test empty string
        fake.ContentToReturn = string.Empty;
        sut.GetItemContent(item);
        sut.GetItemContent(item);
        Assert.AreEqual(4, fake.GetItemContentCallCount, "Empty content must not be cached — inner should be called both times.");
    }

    // 3 -----------------------------------------------------------------------
    [TestMethod]
    public void GetItemByHref_ReturnsCachedOnSecondCall()
    {
        var item = TestData.Item();
        var user = TestData.User();
        var fake = new FakeItemRepository { ItemByHrefToReturn = item };
        var sut  = new CachingItemRepository(fake, TestData.NewCache());

        var first  = sut.GetItem(user, item.Href);
        var second = sut.GetItem(user, item.Href);

        Assert.AreEqual(item, first);
        Assert.AreEqual(item, second);
        Assert.AreEqual(1, fake.GetItemByHrefCallCount, "Inner should be called exactly once for the same href.");
    }

    // 4 -----------------------------------------------------------------------
    [TestMethod]
    public void GetItemById_ReturnsCachedOnSecondCall()
    {
        var item   = TestData.Item();
        var user   = TestData.User();
        var fake   = new FakeItemRepository { ItemByIdToReturn = item };
        var sut    = new CachingItemRepository(fake, TestData.NewCache());

        var first  = sut.GetItem(user, 99);
        var second = sut.GetItem(user, 99);

        Assert.AreEqual(item, first);
        Assert.AreEqual(item, second);
        Assert.AreEqual(1, fake.GetItemByIdCallCount, "Inner should be called exactly once for the same itemId.");
    }

    // 5 -----------------------------------------------------------------------
    [TestMethod]
    public void MarkAsRead_InvalidatesGetItemCache()
    {
        var item = TestData.Item();
        var user = TestData.User();
        var fake = new FakeItemRepository { ItemByHrefToReturn = item };
        var sut  = new CachingItemRepository(fake, TestData.NewCache());

        // Populate cache
        sut.GetItem(user, item.Href);
        Assert.AreEqual(1, fake.GetItemByHrefCallCount);

        // Invalidate
        sut.MarkAsRead(item, true, user);
        Assert.AreEqual(1, fake.MarkAsReadCallCount);

        // Should hit inner again because cache was evicted
        sut.GetItem(user, item.Href);
        Assert.AreEqual(2, fake.GetItemByHrefCallCount, "After MarkAsRead, GetItem should re-query inner.");
    }

    // 6 -----------------------------------------------------------------------
    [TestMethod]
    public void SavePost_InvalidatesGetItemCache()
    {
        var item = TestData.Item();
        var user = TestData.User();
        var fake = new FakeItemRepository { ItemByHrefToReturn = item };
        var sut  = new CachingItemRepository(fake, TestData.NewCache());

        sut.GetItem(user, item.Href);
        Assert.AreEqual(1, fake.GetItemByHrefCallCount);

        sut.SavePost(item, user);
        Assert.AreEqual(1, fake.SavePostCallCount);

        sut.GetItem(user, item.Href);
        Assert.AreEqual(2, fake.GetItemByHrefCallCount, "After SavePost, GetItem should re-query inner.");
    }

    // 7 -----------------------------------------------------------------------
    [TestMethod]
    public async Task GetItemsAsync_PassesThrough()
    {
        var fake = new FakeItemRepository();
        var sut  = new CachingItemRepository(fake, TestData.NewCache());
        var feed = TestData.Feed();

        await sut.GetItemsAsync(feed, false, false, null);
        await sut.GetItemsAsync(feed, false, false, null);

        Assert.AreEqual(2, fake.GetItemsAsyncCallCount,
            "GetItemsAsync must always delegate to inner — no caching should occur.");
    }

    // 8 -----------------------------------------------------------------------
    [TestMethod]
    public void Dispose_ForwardsToInner()
    {
        var fake = new FakeItemRepository();
        var sut  = new CachingItemRepository(fake, TestData.NewCache());

        sut.Dispose();

        Assert.AreEqual(1, fake.DisposeCallCount, "Dispose must be forwarded to the inner repository.");
    }
}
