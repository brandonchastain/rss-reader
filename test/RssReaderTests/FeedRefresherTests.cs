namespace RssReaderTests;
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
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

[TestClass]
public sealed class FeedRefresherTests
{
    /// <summary>
    /// Test HttpMessageHandler that returns canned responses, counts how many
    /// requests reached the network, and records the conditional-GET headers on
    /// the most recent request. Lets us assert politeness + validator behavior
    /// (Retry-After backoff, conditional GETs) deterministically and offline.
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<int, HttpResponseMessage> responder;
        private int calls;

        public int CallCount => Volatile.Read(ref calls);
        public string LastIfNoneMatch { get; private set; }
        public DateTimeOffset? LastIfModifiedSince { get; private set; }

        public StubHandler(Func<int, HttpResponseMessage> responder) => this.responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var n = Interlocked.Increment(ref calls);
            this.LastIfNoneMatch = request.Headers.IfNoneMatch.Count > 0 ? request.Headers.IfNoneMatch.ToString() : null;
            this.LastIfModifiedSince = request.Headers.IfModifiedSince;
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

    /// <summary>
    /// In-memory IFeedRepository: subscriptions plus the per-feed fetch state
    /// the Feeds row carries in production, so schedule, validators and the
    /// one-fetch-per-feed rule are observable in tests.
    /// </summary>
    private sealed class InMemoryFeedRepository : IFeedRepository
    {
        private readonly ConcurrentDictionary<string, FeedFetchState> states = new();
        public ConcurrentDictionary<string, string> LastErrors { get; } = new();
        public List<NewsFeed> Subscriptions { get; } = new();
        public int RecordCount;

        public void Seed(string url, DateTimeOffset? lastFetched = null, DateTimeOffset? next = null, string etag = null, DateTimeOffset? lastModified = null, int failures = 0)
            => this.states[url] = new FeedFetchState(this.states.Count + 1, url, etag, lastModified, lastFetched, next, failures);

        public IEnumerable<NewsFeed> GetFeeds(RssUser user) => this.Subscriptions.Where(f => f.UserId == user.Id).ToList();
        public NewsFeed GetFeed(RssUser user, string url) => this.Subscriptions.FirstOrDefault(f => f.UserId == user.Id && f.Href == url);
        public void AddFeed(NewsFeed feed)
        {
            this.Subscriptions.Add(feed);
            this.states.TryAdd(feed.Href, new FeedFetchState(this.states.Count + 1, feed.Href, null, null, null, null, 0));
        }
        public void AddFeeds(RssUser user, IEnumerable<NewsFeed> feeds) { foreach (var f in feeds) { f.UserId = user.Id; AddFeed(f); } }
        public void AddTag(NewsFeed feed, string tag) { }
        public void DeleteFeed(RssUser user, string url) => this.Subscriptions.RemoveAll(f => f.UserId == user.Id && f.Href == url);
        public IEnumerable<TagSetting> GetTagSettings(RssUser user) => Array.Empty<TagSetting>();
        public void SetTagHidden(RssUser user, string tag, bool isHidden) { }
        public IEnumerable<string> GetHiddenFeedUrls(RssUser user) => Array.Empty<string>();
        public void DeleteAllFeeds(RssUser user) => this.Subscriptions.RemoveAll(f => f.UserId == user.Id);

        public IReadOnlyList<FeedFetchState> GetFeedFetchStates()
            => this.states.Values.Where(s => this.Subscriptions.Any(f => f.Href == s.Url)).OrderBy(s => s.FeedId).ToList();
        public FeedFetchState GetFeedFetchState(string url) => this.states.TryGetValue(url, out var s) ? s : null;
        public IReadOnlyList<FeedFetchState> GetDueFeeds(DateTimeOffset now)
            => GetFeedFetchStates().Where(s => s.NextFetchUtc == null || s.NextFetchUtc <= now).ToList();

        public void RecordFetchResult(string url, DateTimeOffset fetchedUtc, DateTimeOffset nextFetchUtc, TimeSpan interval,
            string etag, DateTimeOffset? lastModified, bool keepValidators, bool failed, string error)
        {
            Interlocked.Increment(ref this.RecordCount);
            this.LastErrors[url] = error;
            this.states.AddOrUpdate(url,
                _ => new FeedFetchState(this.states.Count + 1, url, etag, lastModified, fetchedUtc, nextFetchUtc, failed ? 1 : 0),
                (_, old) => old with
                {
                    LastFetchedUtc = fetchedUtc,
                    NextFetchUtc = nextFetchUtc,
                    ETag = keepValidators ? old.ETag : etag,
                    LastModified = keepValidators ? old.LastModified : lastModified,
                    ConsecutiveFailures = failed ? old.ConsecutiveFailures + 1 : 0,
                });
        }
    }

    private static (FeedRefresher refresher, Mock<IItemRepository> itemRepo) CreateRefresher(
        IFeedRepository feedRepo = null,
        Mock<IUserRepository> userRepo = null,
        RssAppConfig config = null,
        HttpMessageHandler primaryHandler = null,
        RefreshPacer pacer = null)
    {
        var (refresher, itemRepo, _) = CreateRefresherWithProvider(feedRepo, userRepo, config, primaryHandler, pacer);
        return (refresher, itemRepo);
    }

    private static (FeedRefresher refresher, Mock<IItemRepository> itemRepo, ServiceProvider provider) CreateRefresherWithProvider(
        IFeedRepository feedRepo = null,
        Mock<IUserRepository> userRepo = null,
        RssAppConfig config = null,
        HttpMessageHandler primaryHandler = null,
        RefreshPacer pacer = null)
    {
        feedRepo ??= new InMemoryFeedRepository();
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
            .AddSingleton<IFeedRepository>(feedRepo)
            .AddSingleton<IItemRepository>(mockItemRepo.Object)
            .AddSingleton<IUserRepository>(userRepo.Object)
            .AddSingleton<BackgroundWorkQueue>()
            .AddHostedService<BackgroundWorker>()
            .AddSingleton<RssDeserializer>()
            .AddSingleton<ThumbnailResolver>()
            .AddSingleton<RequestActivity>()
            .AddSingleton<RefreshPacer>(sp => pacer ?? new RefreshPacer(config, sp.GetRequiredService<RequestActivity>()))
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
        return (refresher, mockItemRepo, provider);
    }

    /// <summary>Runs the one job a manual refresh queues (the worker is not started here).</summary>
    private static async Task RunQueuedRefreshAsync(ServiceProvider provider)
    {
        var queue = provider.GetRequiredService<BackgroundWorkQueue>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var job = await queue.DequeueAsync(cts.Token);
        await job(cts.Token);
    }

    private static Mock<IFeedRepository> MockFeedsFor(RssUser user, params string[] urls)
    {
        var feedRepo = new Mock<IFeedRepository>();
        feedRepo.Setup(r => r.GetFeeds(It.IsAny<RssUser>()))
            .Returns(urls.Select(u => new NewsFeed(u, user.Id)).ToList());
        return feedRepo;
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    // Fetches every URL in allBrokenFeeds.txt over the real internet, so it
    // passes or fails on whether third-party feeds happen to be reachable. That
    // makes it a useful local diagnostic for feed-parsing regressions and a
    // permanently flaky CI check -- it failed a GitHub runner on
    // thetechbubble.substack.com while passing locally. Categorised so CI can
    // exclude it; run it deliberately with:
    //   dotnet test --filter TestCategory=Network
    [TestMethod]
    [TestCategory("Network")]
    public async Task FetchFeed_Should_Add_Items_To_Store()
    {
        var (feedRefresher, mockItemRepo) = CreateRefresher();

        var file = "allBrokenFeeds.txt";
        var content = File.ReadAllLines(file);

        foreach (var line in content)
        {
            await feedRefresher.FetchFeedAsync(line, force: true, CancellationToken.None);
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
        var user = new RssUser("testUser", 1);
        var feedRepo = MockFeedsFor(user, "https://example.com/feed1", "https://example.com/feed2");

        var (refresher, _) = CreateRefresher(feedRepo: feedRepo.Object);

        await refresher.RefreshAsync(user);

        var status = refresher.GetRefreshStatus(user);
        Assert.IsTrue(status.IsRefreshing, "Should report refreshing after queuing feeds");
        Assert.AreEqual(2, status.PendingFeeds, "Should report correct pending feed count");
    }

    [TestMethod]
    public async Task RefreshAsync_IgnoresDuplicateRequests_WhileRefreshing()
    {
        var user = new RssUser("testUser", 1);
        var feedRepo = MockFeedsFor(user, "https://example.com/feed1");

        var (refresher, _) = CreateRefresher(feedRepo: feedRepo.Object);

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
        var user = new RssUser("testUser", 1);
        var (refresher, _) = CreateRefresher(feedRepo: MockFeedsFor(user).Object);

        await refresher.RefreshAsync(user);

        var status = refresher.GetRefreshStatus(user);
        Assert.IsFalse(status.IsRefreshing, "Should not be refreshing with no feeds");
    }

    [TestMethod]
    public async Task Fetch_HonorsRetryAfter_AndSkipsSecondFetch_On429()
    {
        // Origin rate-limits us with a long Retry-After. The first fetch hits it
        // once (and does NOT try the fallback user-agent against a rate-limiting
        // host); the second, even forced, must be skipped while backed off.
        var handler = new StubHandler(_ =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            res.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1));
            return res;
        });
        var (refresher, _) = CreateRefresher(primaryHandler: handler);

        var url = "https://ratelimited.example.com/feed";
        await refresher.FetchFeedAsync(url, force: true, CancellationToken.None);
        await refresher.FetchFeedAsync(url, force: true, CancellationToken.None);

        Assert.AreEqual(1, handler.CallCount,
            "A 429 with Retry-After should back the feed off; the second fetch must not hit the origin.");
    }

    [TestMethod]
    public async Task Fetch_BacksOff_AfterNetworkFailure()
    {
        // Every request throws. The first fetch tries both user-agents (2 calls)
        // then schedules a backoff; the second fetch is skipped.
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));
        var feedRepo = new InMemoryFeedRepository();
        var (refresher, _) = CreateRefresher(feedRepo: feedRepo, primaryHandler: handler);

        var url = "https://broken.example.com/feed";
        feedRepo.Seed(url);
        await refresher.FetchFeedAsync(url, force: true, CancellationToken.None);
        await refresher.FetchFeedAsync(url, force: true, CancellationToken.None);

        Assert.AreEqual(2, handler.CallCount,
            "After a failed fetch the feed should back off; the second fetch must not hit the origin again.");
        Assert.AreEqual(1, feedRepo.GetFeedFetchState(url).ConsecutiveFailures, "the failure is recorded on the feed row");
    }

    [TestMethod]
    public async Task Fetch_WebPageInsteadOfFeed_RecordsAFriendlyReason()
    {
        // A retired feed URL that redirects to an HTML landing page (what NASA's
        // APOD feed did): the stored reason must say so, not quote an XML parser error.
        const string url = "https://retired.example.com/feed.rss";
        var landing = new Uri("https://www.example.com/landing/");
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<!DOCTYPE html><html><body>Moved</body></html>", System.Text.Encoding.UTF8, "text/html"),
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, landing),
        });
        var feedRepo = new InMemoryFeedRepository();
        var (refresher, _) = CreateRefresher(feedRepo: feedRepo, primaryHandler: handler);

        feedRepo.Seed(url);
        await refresher.FetchFeedAsync(url, force: true, CancellationToken.None);

        Assert.AreEqual(1, feedRepo.GetFeedFetchState(url).ConsecutiveFailures);
        Assert.AreEqual("returned a web page, not a feed (redirected to www.example.com/landing/)", feedRepo.LastErrors[url]);
    }

    [TestMethod]
    public void DescribeNonFeed_OnlyFlagsHtml()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        Assert.AreEqual("returned a web page, not a feed", FeedRefresher.DescribeNonFeed(response, "https://a.example/feed", "  <html lang=en>"));
        Assert.IsNull(FeedRefresher.DescribeNonFeed(response, "https://a.example/feed", "<?xml version=\"1.0\"?><rss/>"));
        Assert.IsNull(FeedRefresher.DescribeNonFeed(response, "https://a.example/feed", null));
    }

    [TestMethod]
    public async Task Fetch_BackoffKeepsGrowing_AcrossARestart()
    {
        // The feed already failed 8 times in a row before this process started (a fresh
        // refresher has no in-memory history). The next delay must continue the climb
        // (2 min * 2^8, capped at 6 h, jittered 0.8-1.2: at least 4.8 h), not start over at 2 min.
        var handler = new StubHandler(_ => throw new HttpRequestException("down"));
        var feedRepo = new InMemoryFeedRepository();
        var (refresher, _) = CreateRefresher(feedRepo: feedRepo, primaryHandler: handler);
        const string url = "https://dead.example.com/feed";
        feedRepo.Seed(url, lastFetched: DateTimeOffset.UtcNow.AddHours(-7), next: DateTimeOffset.UtcNow.AddHours(-1), failures: 8);

        await refresher.FetchFeedAsync(url, force: true, CancellationToken.None);

        var state = feedRepo.GetFeedFetchState(url);
        Assert.AreEqual(9, state.ConsecutiveFailures);
        Assert.IsTrue(state.NextFetchUtc > DateTimeOffset.UtcNow.AddHours(4.5), $"next attempt {state.NextFetchUtc} is too soon for 9 failures");
    }

    [TestMethod]
    public async Task Fetch_HonorsAPersistedBackoffWindow_AfterARestart()
    {
        // A refresh right after a restart must not hit a feed whose recorded backoff has not ended.
        var handler = new StubHandler(_ => throw new HttpRequestException("down"));
        var feedRepo = new InMemoryFeedRepository();
        var (refresher, _) = CreateRefresher(feedRepo: feedRepo, primaryHandler: handler);
        const string url = "https://waiting.example.com/feed";
        feedRepo.Seed(url, lastFetched: DateTimeOffset.UtcNow.AddMinutes(-40), next: DateTimeOffset.UtcNow.AddHours(3), failures: 5);

        await refresher.FetchFeedAsync(url, force: true, CancellationToken.None);

        Assert.AreEqual(0, handler.CallCount, "the origin must not be contacted inside the recorded backoff");
        Assert.AreEqual(5, feedRepo.GetFeedFetchState(url).ConsecutiveFailures, "a skipped fetch is not another failure");
    }

    [TestMethod]
    public async Task Fetch_Failure_NamesTheInnerCause()
    {
        // The expired-certificate case: the outer message only says to look at the inner exception.
        var handler = new StubHandler(_ => throw new HttpRequestException(
            "The SSL connection could not be established, see inner exception.",
            new System.Security.Authentication.AuthenticationException("The remote certificate is invalid because of errors in the certificate chain: NotTimeValid")));
        var feedRepo = new InMemoryFeedRepository();
        var (refresher, _) = CreateRefresher(feedRepo: feedRepo, primaryHandler: handler);
        const string url = "https://expired.example.com/feed";
        feedRepo.Seed(url);

        await refresher.FetchFeedAsync(url, force: true, CancellationToken.None);

        Assert.AreEqual(
            "The SSL connection could not be established - The remote certificate is invalid because of errors in the certificate chain: NotTimeValid",
            feedRepo.LastErrors[url]);
    }

    [TestMethod]
    public async Task Fetch_NetworkFailure_StoresTheMessage()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));
        var feedRepo = new InMemoryFeedRepository();
        var (refresher, _) = CreateRefresher(feedRepo: feedRepo, primaryHandler: handler);
        const string url = "https://down.example.com/feed";
        feedRepo.Seed(url);
        await refresher.FetchFeedAsync(url, force: true, CancellationToken.None);
        StringAssert.Contains(feedRepo.LastErrors[url], "connection refused");
    }

    [TestMethod]
    public async Task Fetch_DoesNotBackOff_OnSuccess()
    {
        // A healthy 200 must not trigger backoff: each forced fetch reaches the origin.
        var rss = File.ReadAllText(Path.Combine("feeds", "vox.xml"));
        var handler = new StubHandler(_ => Ok(rss));
        var (refresher, _) = CreateRefresher(primaryHandler: handler);

        var url = "https://healthy.example.com/feed";
        await refresher.FetchFeedAsync(url, force: true, CancellationToken.None);
        await refresher.FetchFeedAsync(url, force: true, CancellationToken.None);

        Assert.AreEqual(2, handler.CallCount,
            "Successful fetches must not back off; both fetches should reach the origin.");
    }

    [TestMethod]
    public async Task Fetch_PersistsValidators_OnTheFeedRow_OnSuccess()
    {
        // A successful fetch that returns ETag/Last-Modified must persist them so a
        // later process (after restart) can issue a conditional GET.
        var rss = File.ReadAllText(Path.Combine("feeds", "vox.xml"));
        var lastModified = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var handler = new StubHandler(_ =>
        {
            var res = Ok(rss);
            res.Headers.ETag = new EntityTagHeaderValue("\"vox-etag-123\"");
            res.Content.Headers.LastModified = lastModified;
            return res;
        });
        var feedRepo = new InMemoryFeedRepository();
        var (refresher, _) = CreateRefresher(feedRepo: feedRepo, primaryHandler: handler);

        var url = "https://persist.example.com/feed";
        feedRepo.Seed(url);
        await refresher.FetchFeedAsync(url, force: true, CancellationToken.None);

        var saved = feedRepo.GetFeedFetchState(url);
        StringAssert.Contains(saved.ETag, "vox-etag-123", "Persisted ETag should match the response.");
        Assert.AreEqual(lastModified, saved.LastModified, "Persisted Last-Modified should match the response.");
        Assert.IsNotNull(saved.NextFetchUtc, "the next fetch is scheduled");
    }

    [TestMethod]
    public async Task Fetch_SendsConditionalGet_FromFeedRow_AfterRestart()
    {
        // Simulate a restart: a fresh refresher, but the feed row already holds a
        // validator. The fetcher must send a conditional GET, earning a 304
        // instead of a cold full fetch.
        var url = "https://restart.example.com/feed";
        var feedRepo = new InMemoryFeedRepository();
        feedRepo.Seed(url, etag: "\"persisted-etag\"", lastModified: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotModified));
        var (refresher, itemRepo) = CreateRefresher(feedRepo: feedRepo, primaryHandler: handler);

        var added = await refresher.FetchFeedAsync(url, force: true, CancellationToken.None);

        Assert.AreEqual(1, handler.CallCount, "Should issue exactly one (conditional) request.");
        Assert.IsNotNull(handler.LastIfNoneMatch, "A conditional GET should send If-None-Match after a restart.");
        StringAssert.Contains(handler.LastIfNoneMatch, "persisted-etag",
            "If-None-Match should carry the persisted ETag.");
        Assert.IsNotNull(handler.LastIfModifiedSince, "A conditional GET should also send If-Modified-Since.");

        // 304: success is recorded (schedule advances, validators kept), nothing is written.
        Assert.AreEqual(0, added);
        itemRepo.Verify(r => r.AddItemsAsync(It.IsAny<IEnumerable<NewsFeedItem>>()), Times.Never);
        var state = feedRepo.GetFeedFetchState(url);
        Assert.AreEqual(0, state.ConsecutiveFailures);
        Assert.IsNotNull(state.LastFetchedUtc);
        StringAssert.Contains(state.ETag, "persisted-etag", "a 304 keeps the stored validators");
    }

    [TestMethod]
    public async Task Scheduler_FetchesSharedFeedOnce_AndStoresPostsOnce_RegardlessOfSubscriberCount()
    {
        // Three users subscribe to the same URL. One scheduler tick must fetch the
        // origin exactly once and write the posts once; subscribers see them
        // through their subscription, there is no per-user copy.
        var url = "https://shared.example.com/feed";
        var rss = File.ReadAllText(Path.Combine("feeds", "vox.xml"));
        var handler = new StubHandler(_ => Ok(rss));

        var feedRepo = new InMemoryFeedRepository();
        feedRepo.AddFeed(new NewsFeed(url, userId: 1));
        feedRepo.AddFeed(new NewsFeed(url, userId: 2));
        feedRepo.AddFeed(new NewsFeed(url, userId: 3));

        var (refresher, itemRepo) = CreateRefresher(feedRepo: feedRepo, primaryHandler: handler);
        itemRepo.Setup(r => r.AddItemsAsync(It.IsAny<IEnumerable<NewsFeedItem>>())).ReturnsAsync(5);

        await refresher.RunSchedulerTickAsync(CancellationToken.None);

        Assert.AreEqual(1, handler.CallCount, "A feed shared by N users must be fetched once, not N times.");
        itemRepo.Verify(r => r.AddItemsAsync(It.Is<IEnumerable<NewsFeedItem>>(items => items.All(i => i.FeedUrl == url))), Times.Once);
    }

    [TestMethod]
    public async Task NewSubscriber_OnAlreadyFetchedFeed_TriggersNoHttpCall()
    {
        // The feed was fetched a moment ago. A second user subscribing must not
        // cause a fetch: not from the scheduler (not due) and not from their
        // own refresh button (not stale). Their timeline reads the stored posts.
        var url = "https://popular.example.com/feed";
        var handler = new StubHandler(_ => Ok("<rss/>"));
        var feedRepo = new InMemoryFeedRepository();
        feedRepo.AddFeed(new NewsFeed(url, userId: 1));
        feedRepo.Seed(url, lastFetched: DateTimeOffset.UtcNow.AddMinutes(-2), next: DateTimeOffset.UtcNow.AddMinutes(58));

        var config = new RssAppConfig { CacheReloadInterval = TimeSpan.Zero };
        var (refresher, itemRepo, provider) = CreateRefresherWithProvider(feedRepo: feedRepo, config: config, primaryHandler: handler);

        var newcomer = new RssUser("newcomer", 2);
        feedRepo.AddFeed(new NewsFeed(url, newcomer.Id));

        await refresher.RunSchedulerTickAsync(CancellationToken.None);
        await refresher.RefreshAsync(newcomer);
        await RunQueuedRefreshAsync(provider);

        Assert.AreEqual(0, handler.CallCount, "subscribing to an already-fetched feed must not hit the origin");
        itemRepo.Verify(r => r.AddItemsAsync(It.IsAny<IEnumerable<NewsFeedItem>>()), Times.Never);
    }

    [TestMethod]
    public async Task FetchNewFeed_FetchesOnce_OnlyWhileNeverFetched()
    {
        // Adding a feed nobody had fetches it right away, once; adding the same
        // URL again (another subscriber) does not touch the origin.
        var url = "https://first.example.com/feed";
        var handler = new StubHandler(_ => Ok("<rss/>"));
        var feedRepo = new InMemoryFeedRepository();
        var (refresher, _) = CreateRefresher(feedRepo: feedRepo, primaryHandler: handler);

        feedRepo.AddFeed(new NewsFeed(url, userId: 1));
        await refresher.FetchNewFeedAsync(url);
        Assert.AreEqual(1, handler.CallCount, "a brand-new feed row is fetched at once");

        feedRepo.AddFeed(new NewsFeed(url, userId: 2));
        await refresher.FetchNewFeedAsync(url);
        Assert.AreEqual(1, handler.CallCount, "a second subscriber reuses the stored posts");

        await refresher.FetchNewFeedAsync("https://unknown.example.com/feed");
        Assert.AreEqual(1, handler.CallCount, "no feed row, nothing to fetch");
    }

    [TestMethod]
    public async Task Scheduler_FetchesNewlySubscribedFeed_WhichHasNoSchedule()
    {
        // A brand-new feed row (NextFetchUtc NULL) is due immediately.
        var url = "https://brandnew.example.com/feed";
        var handler = new StubHandler(_ => Ok("<rss/>"));
        var feedRepo = new InMemoryFeedRepository();
        feedRepo.AddFeed(new NewsFeed(url, userId: 1));
        var (refresher, _) = CreateRefresher(feedRepo: feedRepo, primaryHandler: handler);

        var next = await refresher.RunSchedulerTickAsync(CancellationToken.None);

        Assert.AreEqual(1, handler.CallCount);
        Assert.IsNotNull(next, "the pass reports when the feed is next due");
        Assert.IsTrue(next > DateTimeOffset.UtcNow, "the next fetch is in the future");
    }

    /// <summary>Pacer on a recorded clock: slot waits and yields are counted, never slept.</summary>
    private sealed class RecordingPacer : RefreshPacer
    {
        public int SlotWaits;
        public int Yields;
        public RecordingPacer(RssAppConfig config, RequestActivity activity) : base(config, activity, () => DateTimeOffset.UtcNow, (_, _) => Task.CompletedTask) { }
        public override Task WaitForFetchSlotAsync(CancellationToken token) { Interlocked.Increment(ref this.SlotWaits); return Task.CompletedTask; }
        public override Task<TimeSpan> YieldToRequestsAsync(CancellationToken token) { Interlocked.Increment(ref this.Yields); return Task.FromResult(TimeSpan.Zero); }
    }

    [TestMethod]
    public async Task Scheduler_TakesABudgetSlotPerFeed_AndYieldsBeforeFetchAndStore()
    {
        var handler = new StubHandler(_ => Ok(File.ReadAllText(Path.Combine("feeds", "vox.xml"))));
        var feedRepo = new InMemoryFeedRepository();
        for (int i = 0; i < 3; i++) feedRepo.AddFeed(new NewsFeed($"https://due{i}.example.com/feed", userId: 1));
        var pacer = new RecordingPacer(new RssAppConfig(), new RequestActivity());
        var (refresher, itemRepo) = CreateRefresher(feedRepo: feedRepo, primaryHandler: handler, pacer: pacer);
        itemRepo.Setup(r => r.AddItemsAsync(It.IsAny<IEnumerable<NewsFeedItem>>())).ReturnsAsync(1);

        await refresher.RunSchedulerTickAsync(CancellationToken.None);

        Assert.AreEqual(3, handler.CallCount);
        Assert.AreEqual(3, pacer.SlotWaits, "one budget slot per scheduled fetch");
        Assert.AreEqual(6, pacer.Yields, "a yield before each fetch and before each store");
    }

    [TestMethod]
    public async Task ManualRefresh_IsNotPacedByTheBudget_ButStillYields()
    {
        var url = "https://manual.example.com/feed";
        var handler = new StubHandler(_ => Ok("<rss/>"));
        var user = new RssUser("u", 1);
        var feedRepo = new InMemoryFeedRepository();
        feedRepo.AddFeed(new NewsFeed(url, user.Id));
        var pacer = new RecordingPacer(new RssAppConfig(), new RequestActivity());
        var (refresher, _, provider) = CreateRefresherWithProvider(feedRepo: feedRepo, primaryHandler: handler, pacer: pacer);

        await refresher.RefreshAsync(user);
        await RunQueuedRefreshAsync(provider);

        Assert.AreEqual(1, handler.CallCount);
        Assert.AreEqual(0, pacer.SlotWaits, "the person asked for this refresh; the budget is for the scheduler");
        Assert.AreEqual(1, pacer.Yields, "but user requests still go first (no store: the feed was empty)");
    }

    [TestMethod]
    public async Task ManualRefresh_FetchesStaleFeed_EvenWhenSchedulerWouldWait()
    {
        // Scheduled 15 minutes out, but last attempted 45 minutes ago (past the
        // 30-minute floor): the button must fetch it.
        var url = "https://stale.example.com/feed";
        var handler = new StubHandler(_ => Ok("<rss/>"));
        var user = new RssUser("u", 1);
        var feedRepo = new InMemoryFeedRepository();
        feedRepo.AddFeed(new NewsFeed(url, user.Id));
        feedRepo.Seed(url, lastFetched: DateTimeOffset.UtcNow.AddMinutes(-45), next: DateTimeOffset.UtcNow.AddMinutes(15));

        var (refresher, _, provider) = CreateRefresherWithProvider(feedRepo: feedRepo, primaryHandler: handler);
        Assert.IsTrue(refresher.IsStale(url));

        await refresher.RefreshAsync(user);
        await RunQueuedRefreshAsync(provider);

        Assert.AreEqual(1, handler.CallCount, "A stale feed must be fetched by a manual refresh.");
        Assert.IsFalse(refresher.IsStale(url), "A fetch attempt resets staleness.");
    }

    [TestMethod]
    public async Task ManualRefresh_LeavesRecentlyFetchedFeedAlone()
    {
        // Attempted 5 minutes ago: inside the floor, so the button must not re-fetch.
        var url = "https://fresh.example.com/feed";
        var handler = new StubHandler(_ => Ok("<rss/>"));
        var user = new RssUser("u", 1);
        var feedRepo = new InMemoryFeedRepository();
        feedRepo.AddFeed(new NewsFeed(url, user.Id));
        feedRepo.Seed(url, lastFetched: DateTimeOffset.UtcNow.AddMinutes(-5), next: DateTimeOffset.UtcNow.AddMinutes(55));

        var (refresher, _, provider) = CreateRefresherWithProvider(feedRepo: feedRepo, primaryHandler: handler);
        Assert.IsFalse(refresher.IsStale(url));

        await refresher.RefreshAsync(user);
        await RunQueuedRefreshAsync(provider);

        Assert.AreEqual(0, handler.CallCount, "A feed fetched within the floor must not be re-fetched by a manual refresh.");
    }

    [TestMethod]
    public async Task ManualRefresh_StaleButBackedOffFeed_IsStillLeftAlone()
    {
        // Stale by the clock, but the origin answered 429 on the previous attempt in
        // this process: the politeness backoff inside the fetch wins over staleness.
        var url = "https://backoff.example.com/feed";
        var calls = 0;
        var handler = new StubHandler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        });
        var user = new RssUser("u", 1);
        var feedRepo = new InMemoryFeedRepository();
        feedRepo.AddFeed(new NewsFeed(url, user.Id));

        var config = new RssAppConfig { CacheReloadInterval = TimeSpan.Zero, FeedRefreshIntervalFloor = TimeSpan.Zero };
        var (refresher, _, provider) = CreateRefresherWithProvider(feedRepo: feedRepo, config: config, primaryHandler: handler);

        await refresher.RefreshAsync(user);
        await RunQueuedRefreshAsync(provider);
        Assert.AreEqual(1, calls, "first attempt reaches the origin and is told to back off");

        await refresher.RefreshAsync(user);
        await RunQueuedRefreshAsync(provider);
        Assert.AreEqual(1, calls, "a backed-off feed is not hammered by another manual refresh");
    }

    [TestMethod]
    public async Task Scheduler_SkipsFeeds_NotYetDue()
    {
        // A feed whose next-fetch time is in the future must not be touched.
        var url = "https://notdue.example.com/feed";
        var handler = new StubHandler(_ => Ok("<rss/>"));

        var feedRepo = new InMemoryFeedRepository();
        feedRepo.AddFeed(new NewsFeed(url, userId: 1));
        feedRepo.Seed(url, lastFetched: DateTimeOffset.UtcNow, next: DateTimeOffset.UtcNow.AddHours(1));

        var (refresher, _) = CreateRefresher(feedRepo: feedRepo, primaryHandler: handler);

        await refresher.RunSchedulerTickAsync(CancellationToken.None);

        Assert.AreEqual(0, handler.CallCount, "A feed not yet due must not be fetched by the scheduler.");
    }
}
