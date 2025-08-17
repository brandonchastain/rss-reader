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
using Microsoft.Extensions.Logging.Abstractions;

[TestClass]
public sealed class ItemRepoTests
{
    [TestMethod]
    public void MarkItemAsRead_Should_Update_Item()
    {
        if (File.Exists("tests.db"))
        {
            File.Delete("tests.db");
        }

        var userRepo = new SQLiteUserRepository(
            $"Data Source=tests.db",
            new NullLogger<SQLiteUserRepository>());
        userRepo.AddUser("testUser", 0);
        var feedRepo = new SQLiteFeedRepository(
            $"Data Source=tests.db",
            new NullLogger<SQLiteFeedRepository>());
        feedRepo.AddFeed(new NewsFeed(1, "https://example.com/feed", 0));

        var user = new RssUser("testUser", 0);

        var serviceCollection = new ServiceCollection();
        serviceCollection
        .AddLogging(loggingBuilder =>
        {
            loggingBuilder.ClearProviders();
            loggingBuilder.AddConsole();
            loggingBuilder.AddDebug();
        })
        .AddSingleton<IFeedRepository>(feedRepo)
        .AddSingleton<IUserRepository>(userRepo)
        .AddSingleton<IItemRepository>(sb =>
        {
            return new SQLiteItemRepository(
                $"Data Source=tests.db",
                sb.GetRequiredService<ILogger<SQLiteItemRepository>>(),
                sb.GetRequiredService<IFeedRepository>(),
                sb.GetRequiredService<IUserRepository>());
        });

        var provider = serviceCollection.BuildServiceProvider();
        var itemRepo = provider.GetRequiredService<IItemRepository>();
        var item = new NewsFeedItem("0", 0, "abc", "https://example.com", null, null, "abc", null);
        item.FeedUrl = "https://example.com/feed";

        itemRepo.AddItems(new[] { item });

        item = itemRepo.GetItem(user, item.Href);
        item.FeedUrl = "https://example.com/feed";
        itemRepo.MarkAsRead(item, true);

        item = itemRepo.GetItem(user, item.Href);
        Assert.IsTrue(item.IsRead);
    }
}
