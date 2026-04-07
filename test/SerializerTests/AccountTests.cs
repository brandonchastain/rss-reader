namespace SerializerTests;

using RssApp.Contracts;
using RssApp.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RssReader.Server.Services;

[TestClass]
public sealed class AccountTests
{
    private string testDbFile;
    private string connectionString;

    private SQLiteUserRepository userRepo;
    private SQLiteFeedRepository feedRepo;
    private IItemRepository itemRepo;
    private ServiceProvider serviceProvider;

    [TestInitialize]
    public void Setup()
    {
        testDbFile = $"account-{Guid.NewGuid():N}.db";
        connectionString = $"Data Source={testDbFile}";

        Directory.CreateDirectory(Path.Combine("wwwroot", "images"));
        // Pre-create favicon to skip network download in AddItemsAsync
        var icoPath = Path.Combine("wwwroot", "images", "example.com.ico");
        try
        {
            if (!File.Exists(icoPath))
                File.WriteAllBytes(icoPath, Array.Empty<byte>());
        }
        catch (Exception) { /* ignore — another parallel test may be writing it */ }

        userRepo = new SQLiteUserRepository(connectionString, new NullLogger<SQLiteUserRepository>());
        feedRepo = new SQLiteFeedRepository(connectionString, new NullLogger<SQLiteFeedRepository>());

        var serviceCollection = new ServiceCollection();
        serviceCollection
            .AddLogging(b => { b.ClearProviders(); b.AddConsole(); })
            .AddSingleton(new RssApp.Config.RssAppConfig())
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
    public async Task DataReport_ReturnsCorrectFeedAndItemCounts()
    {
        // Arrange: create user with 2 feeds and 3 items total
        // Use explicit id=0 so item.UserId matches (same pattern as ItemRepoTests)
        userRepo.AddUser("reportuser", 0);
        var user = new RssUser("reportuser", 0);
        feedRepo.AddFeed(new NewsFeed("https://example.com/feed1", user.Id));
        feedRepo.AddFeed(new NewsFeed("https://example.com/feed2", user.Id));

        var item1 = new NewsFeedItem("1", user.Id, "Post A", "https://example.com/a", null, null, "content a", null)
            { FeedUrl = "https://example.com/feed1" };
        var item2 = new NewsFeedItem("2", user.Id, "Post B", "https://example.com/b", null, null, "content b", null)
            { FeedUrl = "https://example.com/feed1" };
        var item3 = new NewsFeedItem("3", user.Id, "Post C", "https://example.com/c", null, null, "content c", null)
            { FeedUrl = "https://example.com/feed2" };

        await itemRepo.AddItemsAsync(new[] { item1, item2, item3 });

        // Act
        var feeds = feedRepo.GetFeeds(user).ToList();
        var feedSummaries = feeds.Select(f => new FeedSummary
        {
            Url = f.Href,
            Tags = f.Tags?.ToList() ?? new List<string>(),
            ItemCount = itemRepo.GetItemCountForFeed(user, f.Href)
        }).ToList();

        var report = new UserDataReport
        {
            Username = user.Username,
            FeedCount = feeds.Count,
            TotalItemCount = feedSummaries.Sum(f => f.ItemCount),
            Feeds = feedSummaries
        };

        // Assert
        Assert.AreEqual("reportuser", report.Username);
        Assert.AreEqual(2, report.FeedCount);
        Assert.AreEqual(3, report.TotalItemCount);

        var feed1Summary = report.Feeds.First(f => f.Url == "https://example.com/feed1");
        var feed2Summary = report.Feeds.First(f => f.Url == "https://example.com/feed2");
        Assert.AreEqual(2, feed1Summary.ItemCount);
        Assert.AreEqual(1, feed2Summary.ItemCount);
    }

    [TestMethod]
    public async Task DeleteCascade_RemovesAllUserData()
    {
        // Arrange: create user with feed and items (explicit id=0 for consistent userId)
        userRepo.AddUser("deleteuser", 0);
        var user = new RssUser("deleteuser", 0);
        feedRepo.AddFeed(new NewsFeed("https://example.com/feed", user.Id));

        var item = new NewsFeedItem("1", user.Id, "Post", "https://example.com/post", null, null, "content", null)
            { FeedUrl = "https://example.com/feed" };
        await itemRepo.AddItemsAsync(new[] { item });

        // Act: cascade delete
        await itemRepo.DeleteAllItemsAsync(user);
        feedRepo.DeleteAllFeeds(user);
        userRepo.DeleteUser(user.Id);

        // Assert: everything is gone
        var deletedUser = userRepo.GetUserById(user.Id);
        Assert.IsNull(deletedUser, "User should be deleted");

        var feeds = feedRepo.GetFeeds(user).ToList();
        Assert.AreEqual(0, feeds.Count, "Feeds should be deleted");

        var itemCount = itemRepo.GetItemCountForFeed(user, "https://example.com/feed");
        Assert.AreEqual(0, itemCount, "Items should be deleted");
    }

    [TestMethod]
    public void DeleteAllFeeds_RemovesFeedsAndTagSettings()
    {
        // Arrange: create user with 2 feeds and tag settings
        userRepo.AddUser("feeddeleteuser", 0);
        var user = new RssUser("feeddeleteuser", 0);
        feedRepo.AddFeed(new NewsFeed("https://example.com/f1", user.Id));
        feedRepo.AddFeed(new NewsFeed("https://example.com/f2", user.Id));

        var feeds = feedRepo.GetFeeds(user).ToList();
        feedRepo.AddTag(feeds[0], "tech");
        feedRepo.AddTag(feeds[1], "news");
        feedRepo.SetTagHidden(user, "tech", true);

        // Verify setup
        Assert.AreEqual(2, feedRepo.GetFeeds(user).Count());
        Assert.AreEqual(2, feedRepo.GetTagSettings(user).Count());

        // Act
        feedRepo.DeleteAllFeeds(user);

        // Assert
        var remainingFeeds = feedRepo.GetFeeds(user).ToList();
        Assert.AreEqual(0, remainingFeeds.Count, "All feeds should be deleted");

        var tagSettings = feedRepo.GetTagSettings(user).ToList();
        Assert.AreEqual(0, tagSettings.Count, "All tag settings should be deleted");
    }

    [TestMethod]
    public void DeleteUser_RemovesUserFromDatabase()
    {
        // Arrange
        userRepo.AddUser("removeme", 0);
        var user = new RssUser("removeme", 0);
        Assert.IsNotNull(userRepo.GetUserById(user.Id), "User should exist before deletion");

        // Act
        userRepo.DeleteUser(user.Id);

        // Assert
        var deleted = userRepo.GetUserById(user.Id);
        Assert.IsNull(deleted, "User should not be found after deletion");

        var byName = userRepo.GetUserByName("removeme");
        Assert.IsNull(byName, "User should not be found by name after deletion");
    }
}
