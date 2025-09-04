namespace SerializerTests;
using RssApp.Serialization;
using RssApp.Contracts;
using RssApp.RssClient;
using Microsoft.Extensions.DependencyInjection;
using RssApp.ComponentServices;
using Microsoft.Extensions.Logging;
using Moq;
using RssApp.Data;
using System.Threading.Tasks;

[TestClass]
public sealed class FeedRefresherTests
{
    [TestMethod]
    public async Task AddFeedAsync_Should_Add_Items_To_Store()
    {
        var mockFeedRepo = new Mock<IFeedRepository>();
        var mockItemRepo = new Mock<IItemRepository>();
        var mockUserRepo = new Mock<IUserRepository>();

        mockUserRepo
        .Setup(repo => repo.GetUserById(It.IsAny<int>()))
        .Returns(new RssUser("testUser", 0));

        var serviceCollection = new ServiceCollection();
        serviceCollection
        .AddLogging(loggingBuilder =>
        {
            loggingBuilder.ClearProviders();
            loggingBuilder.AddConsole();
            loggingBuilder.AddDebug();
        })
        .AddSingleton<BackgroundWorkQueue>()
        .AddHostedService<BackgroundWorker>()
        .AddSingleton<RssDeserializer>()
        .AddSingleton<IFeedRefresher>(sp =>
        {
            return new FeedRefresher(
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<RssDeserializer>(),
                sp.GetRequiredService<ILogger<FeedRefresher>>(),
                mockFeedRepo.Object,
                mockItemRepo.Object,
                mockUserRepo.Object,
                sp.GetRequiredService<BackgroundWorkQueue>(),
                cacheReloadInterval: TimeSpan.FromMinutes(5),
                cacheReloadStartupDelay: TimeSpan.FromSeconds(10));
        })
        .AddTransient<RedirectDowngradeHandler>()
        .AddHttpClient("RssClient")
        .AddHttpMessageHandler<RedirectDowngradeHandler>()
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseDefaultCredentials = true
        });
        var provider = serviceCollection.BuildServiceProvider();
        var feedRefresher = (FeedRefresher)provider.GetRequiredService<IFeedRefresher>();

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
}
