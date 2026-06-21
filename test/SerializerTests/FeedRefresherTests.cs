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
using RssReader.Server.Services;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

[TestClass]
public sealed class FeedRefresherTests
{
    /// <summary>
    /// Test HttpMessageHandler that returns canned responses and counts how many
    /// requests actually reached the network. Lets us assert politeness behavior
    /// (Retry-After backoff, failure backoff) deterministically and offline.
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<int, HttpResponseMessage> responder;
        private int calls;

        public int CallCount => Volatile.Read(ref calls);

        public StubHandler(Func<int, HttpResponseMessage> responder) => this.responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var n = Interlocked.Increment(ref calls);
            try
            {
                return Task.FromResult(this.responder(n));
            }
            catch (Exception ex)
            {
                return Task.FromException<HttpResponseMessage>(ex);
            }
        }
    }

    private static (FeedRefresher refresher, Mock<IItemRepository> itemRepo) CreateRefresher(
        Mock<IFeedRepository> feedRepo = null,
        Mock<IUserRepository> userRepo = null,
        RssAppConfig config = null,
        HttpMessageHandler primaryHandler = null)
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
            .AddSingleton<ThumbnailResolver>()
            .AddSingleton<IFeedRefresher, FeedRefresher>()
            .AddTransient<RedirectDowngradeHandler>()
            .AddHttpClient("RssClient")
            .AddHttpMessageHandler<RedirectDowngradeHandler>()
            .ConfigurePrimaryHttpMessageHandler(() => primaryHandler ?? new HttpClientHandler
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

    [TestMethod]
    public async Task Fetch_HonorsRetryAfter_AndSkipsSecondRefresh_On429()
    {
        // Origin rate-limits us with a long Retry-After. The first refresh hits it
        // once (and does NOT try the fallback user-agent against a rate-limiting
        // host); the second refresh must be skipped entirely while backed off.
        var handler = new StubHandler(_ =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            res.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1));
            return res;
        });
        var (refresher, _) = CreateRefresher(primaryHandler: handler);

        var feed = new NewsFeed("https://ratelimited.example.com/feed", userId: 0);
        await refresher.AddFeedAsync(feed);
        await refresher.AddFeedAsync(feed);

        Assert.AreEqual(1, handler.CallCount,
            "A 429 with Retry-After should back the feed off; the second refresh must not hit the origin.");
    }

    [TestMethod]
    public async Task Fetch_BacksOff_AfterNetworkFailure()
    {
        // Every request throws. The first refresh tries both user-agents (2 calls)
        // then schedules a backoff; the second refresh is skipped.
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));
        var (refresher, _) = CreateRefresher(primaryHandler: handler);

        var feed = new NewsFeed("https://broken.example.com/feed", userId: 0);
        await refresher.AddFeedAsync(feed);
        await refresher.AddFeedAsync(feed);

        Assert.AreEqual(2, handler.CallCount,
            "After a failed fetch the feed should back off; the second refresh must not hit the origin again.");
    }

    [TestMethod]
    public async Task Fetch_DoesNotBackOff_OnSuccess()
    {
        // A healthy 200 must not trigger backoff: each refresh fetches normally.
        var rss = File.ReadAllText(Path.Combine("feeds", "vox.xml"));
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(rss),
        });
        var (refresher, _) = CreateRefresher(primaryHandler: handler);

        var feed = new NewsFeed("https://healthy.example.com/feed", userId: 0);
        await refresher.AddFeedAsync(feed);
        await refresher.AddFeedAsync(feed);

        Assert.AreEqual(2, handler.CallCount,
            "Successful fetches must not back off; both refreshes should reach the origin.");
    }
}
