using RssApp.Contracts;
using WasmApp.Services;

namespace RssReaderTests;

/// <summary>
/// Cached timelines paint before the first response arrives. The cache patches
/// read state into every slot, and a row marked read deliberately stays on
/// screen until the next server fetch, so the unread view has to drop read
/// posts at hydration time or returning to it would briefly show them.
/// </summary>
[TestClass]
public class CacheHydrationTests
{
    private static NewsFeedItem Item(string id, bool isRead) => new() { Id = id, IsRead = isRead };

    [TestMethod]
    public void UnreadFilter_DropsReadPosts_KeepsOrder()
    {
        var cached = new List<NewsFeedItem> { Item("1", false), Item("2", true), Item("3", false) };

        var result = PostCache.ForHydration(cached, isFilterUnread: true);

        CollectionAssert.AreEqual(new[] { "1", "3" }, result.Select(i => i.Id).ToArray());
    }

    [TestMethod]
    public void UnreadFilter_AllRead_IsEmptySoHydrationMisses()
    {
        var cached = new List<NewsFeedItem> { Item("1", true), Item("2", true) };

        Assert.AreEqual(0, PostCache.ForHydration(cached, isFilterUnread: true).Count);
    }

    [TestMethod]
    public void OtherViews_KeepReadPosts()
    {
        var cached = new List<NewsFeedItem> { Item("1", false), Item("2", true) };

        var result = PostCache.ForHydration(cached, isFilterUnread: false);

        CollectionAssert.AreEqual(new[] { "1", "2" }, result.Select(i => i.Id).ToArray());
    }

    [TestMethod]
    public void NullCache_IsEmpty()
    {
        Assert.AreEqual(0, PostCache.ForHydration(null!, isFilterUnread: true).Count);
    }
}
