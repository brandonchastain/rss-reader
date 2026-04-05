namespace SerializerTests;
using RssApp.Contracts;
using RssApp.Data;
using RssApp.Config;
using Microsoft.Extensions.Logging.Abstractions;
using RssApp.ComponentServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RssReader.Server.Services;

[TestClass]
public sealed class TagSettingsTests
{
    private string testDbFile;
    private string connectionString;

    private SQLiteUserRepository userRepo;
    private SQLiteFeedRepository feedRepo;
    private IItemRepository itemRepo;
    private RssUser testUser;
    private ServiceProvider serviceProvider;

    [TestInitialize]
    public void Setup()
    {
        testDbFile = $"tagsettings-{Guid.NewGuid():N}.db";
        connectionString = $"Data Source={testDbFile}";

        Directory.CreateDirectory(Path.Combine("wwwroot", "images"));

        userRepo = new SQLiteUserRepository(connectionString, new NullLogger<SQLiteUserRepository>());
        userRepo.AddUser("tagTestUser", 0);
        testUser = new RssUser("tagTestUser", 0);

        feedRepo = new SQLiteFeedRepository(connectionString, new NullLogger<SQLiteFeedRepository>());

        var serviceCollection = new ServiceCollection();
        serviceCollection
            .AddLogging(b => { b.ClearProviders(); b.AddConsole(); })
            .AddSingleton(new RssAppConfig())
            .AddSingleton<FeedThumbnailRetriever>()
            .AddSingleton<IFeedRepository>(feedRepo)
            .AddSingleton<IUserRepository>(userRepo)
            .AddSingleton<IItemRepository>(sb =>
                new SQLiteItemRepository(
                    connectionString,
                    sb.GetRequiredService<ILogger<SQLiteItemRepository>>(),
                    sb.GetRequiredService<IFeedRepository>(),
                    sb.GetRequiredService<IUserRepository>(),
                    sb.GetRequiredService<FeedThumbnailRetriever>()));
        serviceProvider = serviceCollection.BuildServiceProvider();
        itemRepo = serviceProvider.GetRequiredService<IItemRepository>();
    }

    [TestCleanup]
    public void Cleanup()
    {
        serviceProvider?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(testDbFile)) File.Delete(testDbFile); } catch { }
    }

    [TestMethod]
    public void GetTagSettings_NoFeeds_ReturnsEmpty()
    {
        var result = feedRepo.GetTagSettings(testUser).ToList();
        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void GetTagSettings_FeedWithTag_ReturnsVisibleByDefault()
    {
        feedRepo.AddFeed(new NewsFeed("https://example.com/feed1", testUser.Id));
        var feeds = feedRepo.GetFeeds(testUser).ToList();
        feedRepo.AddTag(feeds[0], "tech");

        var settings = feedRepo.GetTagSettings(testUser).ToList();
        Assert.AreEqual(1, settings.Count);
        Assert.AreEqual("tech", settings[0].Tag);
        Assert.IsFalse(settings[0].IsHidden);
    }

    [TestMethod]
    public void SetTagHidden_HidesTag()
    {
        feedRepo.AddFeed(new NewsFeed("https://example.com/feed1", testUser.Id));
        var feeds = feedRepo.GetFeeds(testUser).ToList();
        feedRepo.AddTag(feeds[0], "news");

        feedRepo.SetTagHidden(testUser, "news", true);

        var settings = feedRepo.GetTagSettings(testUser).ToList();
        Assert.AreEqual(1, settings.Count);
        Assert.IsTrue(settings[0].IsHidden);
    }

    [TestMethod]
    public void SetTagHidden_ShowTag_RevertsHidden()
    {
        feedRepo.AddFeed(new NewsFeed("https://example.com/feed1", testUser.Id));
        var feeds = feedRepo.GetFeeds(testUser).ToList();
        feedRepo.AddTag(feeds[0], "news");

        feedRepo.SetTagHidden(testUser, "news", true);
        feedRepo.SetTagHidden(testUser, "news", false);

        var settings = feedRepo.GetTagSettings(testUser).ToList();
        Assert.AreEqual(1, settings.Count);
        Assert.IsFalse(settings[0].IsHidden);
    }

    [TestMethod]
    public void SetTagHidden_Idempotent()
    {
        feedRepo.AddFeed(new NewsFeed("https://example.com/feed1", testUser.Id));
        var feeds = feedRepo.GetFeeds(testUser).ToList();
        feedRepo.AddTag(feeds[0], "sports");

        feedRepo.SetTagHidden(testUser, "sports", true);
        feedRepo.SetTagHidden(testUser, "sports", true);

        var settings = feedRepo.GetTagSettings(testUser).ToList();
        Assert.AreEqual(1, settings.Count);
        Assert.IsTrue(settings[0].IsHidden);
    }

    [TestMethod]
    public void GetTagSettings_MultipleFeeds_ReturnsDistinctTags()
    {
        feedRepo.AddFeed(new NewsFeed("https://example.com/feed1", testUser.Id));
        feedRepo.AddFeed(new NewsFeed("https://example.com/feed2", testUser.Id));
        var feeds = feedRepo.GetFeeds(testUser).ToList();
        feedRepo.AddTag(feeds[0], "tech");
        feedRepo.AddTag(feeds[1], "tech");
        feedRepo.AddTag(feeds[1], "news");

        var settings = feedRepo.GetTagSettings(testUser).ToList();
        Assert.AreEqual(2, settings.Count);
        Assert.IsTrue(settings.Any(s => s.Tag == "tech"));
        Assert.IsTrue(settings.Any(s => s.Tag == "news"));
    }

    [TestMethod]
    public void GetHiddenFeedUrls_NoHiddenTags_ReturnsEmpty()
    {
        feedRepo.AddFeed(new NewsFeed("https://example.com/feed1", testUser.Id));
        var feeds = feedRepo.GetFeeds(testUser).ToList();
        feedRepo.AddTag(feeds[0], "tech");

        var hidden = feedRepo.GetHiddenFeedUrls(testUser).ToList();
        Assert.AreEqual(0, hidden.Count);
    }

    [TestMethod]
    public void GetHiddenFeedUrls_HiddenTag_ReturnsFeedUrl()
    {
        feedRepo.AddFeed(new NewsFeed("https://example.com/feed1", testUser.Id));
        var feeds = feedRepo.GetFeeds(testUser).ToList();
        feedRepo.AddTag(feeds[0], "tech");
        feedRepo.SetTagHidden(testUser, "tech", true);

        var hidden = feedRepo.GetHiddenFeedUrls(testUser).ToList();
        Assert.AreEqual(1, hidden.Count);
        Assert.AreEqual("https://example.com/feed1", hidden[0]);
    }

    [TestMethod]
    public void GetHiddenFeedUrls_MultiTagFeed_OneHidden_ExcludesFeed()
    {
        feedRepo.AddFeed(new NewsFeed("https://example.com/feed1", testUser.Id));
        var feeds = feedRepo.GetFeeds(testUser).ToList();
        feedRepo.AddTag(feeds[0], "tech");
        feedRepo.AddTag(feeds[0], "news");

        // Hide just one tag - feed should still be excluded
        feedRepo.SetTagHidden(testUser, "tech", true);

        var hidden = feedRepo.GetHiddenFeedUrls(testUser).ToList();
        Assert.AreEqual(1, hidden.Count);
        Assert.AreEqual("https://example.com/feed1", hidden[0]);
    }

    [TestMethod]
    public async Task Timeline_ExcludesHiddenTagFeeds()
    {
        feedRepo.AddFeed(new NewsFeed("https://example.com/visible", testUser.Id));
        feedRepo.AddFeed(new NewsFeed("https://example.com/hidden", testUser.Id));
        var feeds = feedRepo.GetFeeds(testUser).ToList();

        var visibleFeed = feeds.First(f => f.Href == "https://example.com/visible");
        var hiddenFeed = feeds.First(f => f.Href == "https://example.com/hidden");

        feedRepo.AddTag(visibleFeed, "visible-tag");
        feedRepo.AddTag(hiddenFeed, "hidden-tag");

        // Add items to both feeds
        var visibleItem = new NewsFeedItem("1", 0, "Visible Post", "https://example.com/visible/post1",
            null, null, "content", null) { FeedUrl = "https://example.com/visible" };
        var hiddenItem = new NewsFeedItem("2", 0, "Hidden Post", "https://example.com/hidden/post1",
            null, null, "content", null) { FeedUrl = "https://example.com/hidden" };
        await itemRepo.AddItemsAsync(new[] { visibleItem, hiddenItem });

        // Hide the tag
        feedRepo.SetTagHidden(testUser, "hidden-tag", true);
        var excludedUrls = feedRepo.GetHiddenFeedUrls(testUser);

        // Query timeline with exclusion
        var timelineFeed = new NewsFeed("%", testUser.Id);
        var items = await itemRepo.GetItemsAsync(timelineFeed, false, false, null,
            excludeFeedUrls: excludedUrls);

        Assert.IsTrue(items.Any(i => i.Title == "Visible Post"), "Visible post should appear");
        Assert.IsFalse(items.Any(i => i.Title == "Hidden Post"), "Hidden post should be excluded");
    }

    [TestMethod]
    public async Task Timeline_FilterTag_ShowsHiddenTagFeeds()
    {
        feedRepo.AddFeed(new NewsFeed("https://example.com/hidden", testUser.Id));
        var feeds = feedRepo.GetFeeds(testUser).ToList();
        feedRepo.AddTag(feeds[0], "hidden-tag");

        var item = new NewsFeedItem("1", 0, "Hidden Tag Post", "https://example.com/hidden/post1",
            null, null, "content", null) { FeedUrl = "https://example.com/hidden" };
        await itemRepo.AddItemsAsync(new[] { item });

        feedRepo.SetTagHidden(testUser, "hidden-tag", true);

        // When filtering by the hidden tag, do NOT pass excludeFeedUrls
        var timelineFeed = new NewsFeed("%", testUser.Id);
        var items = await itemRepo.GetItemsAsync(timelineFeed, false, false, "hidden-tag");

        Assert.IsTrue(items.Any(i => i.Title == "Hidden Tag Post"),
            "When explicitly filtering by a tag, posts should show even if tag is hidden");
    }

    [TestMethod]
    public void EmptyTagSettings_AllVisible_BackwardCompatible()
    {
        // No UserTagSettings rows = all tags visible
        feedRepo.AddFeed(new NewsFeed("https://example.com/feed1", testUser.Id));
        var feeds = feedRepo.GetFeeds(testUser).ToList();
        feedRepo.AddTag(feeds[0], "tech");

        var settings = feedRepo.GetTagSettings(testUser).ToList();
        Assert.AreEqual(1, settings.Count);
        Assert.IsFalse(settings[0].IsHidden, "Default state should be visible");

        var hidden = feedRepo.GetHiddenFeedUrls(testUser).ToList();
        Assert.AreEqual(0, hidden.Count, "No feeds should be hidden by default");
    }
}
