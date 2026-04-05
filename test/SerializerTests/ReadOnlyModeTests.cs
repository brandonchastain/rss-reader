namespace SerializerTests;
using RssApp.ComponentServices;
using RssApp.Config;
using RssApp.Contracts;

[TestClass]
public sealed class ReadOnlyModeTests
{
    [TestMethod]
    public void IsReadOnly_DefaultsToFalse()
    {
        var config = new RssAppConfig();
        Assert.IsFalse(config.IsReadOnly);
    }

    [TestMethod]
    public async Task NoOpFeedRefresher_AddFeed_CompletesWithoutError()
    {
        var refresher = new NoOpFeedRefresher();
        await refresher.AddFeedAsync(new NewsFeed("https://example.com/rss", 1));
    }

    [TestMethod]
    public async Task NoOpFeedRefresher_Refresh_CompletesWithoutError()
    {
        var refresher = new NoOpFeedRefresher();
        await refresher.RefreshAsync(new RssUser("testuser", 1));
    }

    [TestMethod]
    public async Task NoOpFeedRefresher_HasNewItems_AlwaysReturnsFalse()
    {
        var refresher = new NoOpFeedRefresher();
        var result = await refresher.HasNewItemsAsync(new RssUser("testuser", 1));
        Assert.IsFalse(result);
    }

    [TestMethod]
    public void NoOpFeedRefresher_ResetRefreshCooldown_DoesNotThrow()
    {
        var refresher = new NoOpFeedRefresher();
        refresher.ResetRefreshCooldown();
    }
}
