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
    public async Task HasNewItemsAsync_ResetsFlag_AfterReturningTrue()
    {
        var (refresher, _) = CreateRefresher();
        var user = new RssUser("testUser", 1);

        // Use startup delay to trigger the cooldown path (sets hasNewItems immediately)
        var config = new RssAppConfig { CacheReloadStartupDelay = TimeSpan.FromMinutes(5) };
        var (refresherWithDelay, _) = CreateRefresher(config: config);

        await refresherWithDelay.RefreshAsync(user);

        // First check should return true
        bool firstCheck = await refresherWithDelay.HasNewItemsAsync(user);
        Assert.IsTrue(firstCheck, "First check should return true after refresh with cooldown");

        // Second check should return false (flag was reset)
        bool secondCheck = await refresherWithDelay.HasNewItemsAsync(user);
        Assert.IsFalse(secondCheck, "Second check should return false after flag was consumed");
    }

    [TestMethod]
    public async Task RefreshAsync_WithCooldown_SetsHasNewItemsImmediately()
    {
        var config = new RssAppConfig { CacheReloadStartupDelay = TimeSpan.FromMinutes(5) };
        var (refresher, _) = CreateRefresher(config: config);
        var user = new RssUser("testUser", 1);

        await refresher.RefreshAsync(user);
        bool hasItems = await refresher.HasNewItemsAsync(user);
        Assert.IsTrue(hasItems, "Should set hasNewItems immediately when cooldown is active");
    }
}
