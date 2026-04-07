namespace SerializerTests;
using RssApp.Serialization;
using RssApp.Contracts;
using RssApp.RssClient;
using RssApp.Config;
using Microsoft.Extensions.DependencyInjection;
using RssApp.ComponentServices;
using Microsoft.Extensions.Logging;
using Moq;
using RssApp.Data;
using System.Threading.Tasks;

[TestClass]
public sealed class FeedRefresherTests
{
    private static (FeedRefresher refresher, Mock<IItemRepository> itemRepo) CreateRefresher(
        Mock<IFeedRepository> feedRepo = null,
        Mock<IUserRepository> userRepo = null,
        RssAppConfig config = null)
    {
        feedRepo ??= new Mock<IFeedRepository>();
        var mockItemRepo = new Mock<IItemRepository>();
        userRepo ??= new Mock<IUserRepository>();
        config ??= new RssAppConfig();

        userRepo
            .Setup(repo => repo.GetUserById(It.IsAny<int>()))
            .Returns(new RssUser("testUser", 0));
        var serviceCollection = new ServiceCollection();
        serviceCollection
            .AddLogging(b => { b.ClearProviders(); b.AddConsole(); b.AddDebug(); })
            .AddSingleton(config)
            .AddSingleton<IFeedRepository>(feedRepo.Object)
            .AddSingleton<IItemRepository>(mockItemRepo.Object)
            .AddSingleton<IUserRepository>(userRepo.Object)
            .AddSingleton<BackgroundWorkQueue>()
            .AddHostedService<BackgroundWorker>()
            .AddSingleton<RssDeserializer>()
            .AddSingleton<IFeedRefresher, FeedRefresher>()
            .AddTransient<RedirectDowngradeHandler>()
            .AddHttpClient("RssClient")
            .AddHttpMessageHandler<RedirectDowngradeHandler>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseDefaultCredentials = true
            });

        var provider = serviceCollection.BuildServiceProvider();
        var refresher = (FeedRefresher)provider.GetRequiredService<IFeedRefresher>();
        return (refresher, mockItemRepo);
    }

    [TestMethod]
    public async Task AddFeedAsync_Should_Add_Items_To_Store()
    {
        var (feedRefresher, mockItemRepo) = CreateRefresher();

        var file = "allBrokenFeeds.txt";
        var content = File.ReadAllLines(file);

        foreach (var line in content)
        {
            var feed = new NewsFeed(line, userId: 0);
            await feedRefresher.AddFeedAsync(feed);
            mockItemRepo.Verify(m => m.AddItemsAsync(It.IsAny<IEnumerable<NewsFeedItem>>()), Times.AtLeast(1));
            mockItemRepo.Invocations.Clear();
        }
    }

    [TestMethod]
    public async Task GetRefreshStatus_ReportsNoNewItems_WhenCooldownActive()
    {
        // Cooldown path no longer fakes hasNewItems — it returns honestly
        var config = new RssAppConfig { CacheReloadStartupDelay = TimeSpan.FromMinutes(5) };
        var (refresher, _) = CreateRefresher(config: config);
        var user = new RssUser("testUser", 1);

        await refresher.RefreshAsync(user);

        var status = refresher.GetRefreshStatus(user);
        Assert.IsFalse(status.HasNewItems, "Cooldown should not fake hasNewItems");
        Assert.IsFalse(status.IsRefreshing, "Cooldown should not start a refresh");
        Assert.AreEqual(0, status.PendingFeeds, "Cooldown should not queue feeds");
    }

    [TestMethod]
    public async Task GetRefreshStatus_ReportsRefreshing_WhenFeedsAreQueued()
    {
        var feedRepo = new Mock<IFeedRepository>();
        feedRepo.Setup(r => r.GetFeeds(It.IsAny<RssUser>()))
            .Returns(new List<NewsFeed>
            {
                new NewsFeed("https://example.com/feed1", userId: 1),
                new NewsFeed("https://example.com/feed2", userId: 1),
            });

        var (refresher, _) = CreateRefresher(feedRepo: feedRepo);
        var user = new RssUser("testUser", 1);

        await refresher.RefreshAsync(user);

        var status = refresher.GetRefreshStatus(user);
        Assert.IsTrue(status.IsRefreshing, "Should report refreshing after queuing feeds");
        Assert.AreEqual(2, status.PendingFeeds, "Should report correct pending feed count");
    }

    [TestMethod]
    public async Task RefreshAsync_IgnoresDuplicateRequests_WhileRefreshing()
    {
        var feedRepo = new Mock<IFeedRepository>();
        feedRepo.Setup(r => r.GetFeeds(It.IsAny<RssUser>()))
            .Returns(new List<NewsFeed>
            {
                new NewsFeed("https://example.com/feed1", userId: 1),
            });

        var (refresher, _) = CreateRefresher(feedRepo: feedRepo);
        var user = new RssUser("testUser", 1);

        // First refresh starts normally
        await refresher.RefreshAsync(user);
        var status1 = refresher.GetRefreshStatus(user);
        Assert.IsTrue(status1.IsRefreshing);
        Assert.AreEqual(1, status1.PendingFeeds);

        // Second refresh while first is still running — should be ignored
        await refresher.RefreshAsync(user);
        var status2 = refresher.GetRefreshStatus(user);
        Assert.AreEqual(1, status2.PendingFeeds, "Duplicate refresh should not re-queue feeds");
    }

    [TestMethod]
    public void GetRefreshStatus_ReturnsDefault_ForUnknownUser()
    {
        var (refresher, _) = CreateRefresher();
        var user = new RssUser("unknownUser", 999);

        var status = refresher.GetRefreshStatus(user);
        Assert.IsFalse(status.HasNewItems);
        Assert.IsFalse(status.IsRefreshing);
        Assert.AreEqual(0, status.PendingFeeds);
    }

    [TestMethod]
    public async Task RefreshAsync_WithNoFeeds_DoesNotStartRefresh()
    {
        var feedRepo = new Mock<IFeedRepository>();
        feedRepo.Setup(r => r.GetFeeds(It.IsAny<RssUser>()))
            .Returns(new List<NewsFeed>());

        var (refresher, _) = CreateRefresher(feedRepo: feedRepo);
        var user = new RssUser("testUser", 1);

        await refresher.RefreshAsync(user);

        var status = refresher.GetRefreshStatus(user);
        Assert.IsFalse(status.IsRefreshing, "Should not be refreshing with no feeds");
    }
}
