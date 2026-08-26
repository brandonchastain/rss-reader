using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RssApp.Config;
using RssApp.Contracts;
using WasmApp.Services;

namespace SerializerTests;

/// <summary>
/// The cache warm tops the stored timeline up to a full page. Without it the
/// cache only grew when the user happened to scroll, because a cache hit skips
/// the initial fetch and nothing re-persists.
/// </summary>
[TestClass]
public class CacheWarmTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode status;
        private readonly string body;

        public CapturingHandler(HttpStatusCode status, string body)
        {
            this.status = status;
            this.body = body;
        }

        // A warm issues two requests -- the timeline page, then the content
        // batch -- so tests need all of them, not just the last.
        public List<Uri> Requests { get; } = new();
        public int CallCount => this.Requests.Count;

        public Uri RequestFor(string pathFragment)
            => this.Requests.FirstOrDefault(u => u.AbsolutePath.Contains(pathFragment));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            this.Requests.Add(request.RequestUri);
            return Task.FromResult(new HttpResponseMessage(this.status)
            {
                Content = new StringContent(this.body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private static List<NewsFeedItem> Items(int count, int startId = 1)
        => Enumerable.Range(startId, count)
            .Select(i => new NewsFeedItem { Id = i.ToString(), Href = "https://post/" + i, FeedUrl = "https://feed", UserId = 7 })
            .ToList();

    private static (FeedClient client, CapturingHandler handler, Mock<IPostCache> cache) Create(
        List<NewsFeedItem> cached, string responseBody, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new CapturingHandler(status, responseBody);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://test.local/") };

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var cache = new Mock<IPostCache>();
        cache.Setup(c => c.GetTimelineAsync(It.IsAny<string>())).ReturnsAsync(cached);
        cache.Setup(c => c.GetPendingWritesAsync()).ReturnsAsync(new List<PendingWrite>());

        var config = new RssWasmConfig { ApiBaseUrl = "https://test.local/" };
        var client = new FeedClient(
            config, NullLogger<FeedClient>.Instance, new Mock<IUserClient>().Object, cache.Object, factory.Object);

        return (client, handler, cache);
    }

    [TestMethod]
    public async Task Warm_SkipsEntirelyWhenTheCacheIsAlreadyFull()
    {
        var (client, handler, cache) = Create(Items(PostCache.MaxTimelineItems), "[]");

        var warm = await client.WarmTimelineCacheAsync();

        Assert.IsTrue(warm);
        Assert.AreEqual(0, handler.CallCount, "a full cache should cost no request");
        cache.Verify(c => c.SetTimelineAsync(It.IsAny<string>(), It.IsAny<IEnumerable<NewsFeedItem>>()), Times.Never);
    }

    [TestMethod]
    public async Task Warm_FetchesAFullPageWhenTheCacheIsShort()
    {
        var (client, handler, cache) = Create(Items(20), JsonSerializer.Serialize(Items(50)));

        var warm = await client.WarmTimelineCacheAsync();

        Assert.IsTrue(warm);
        cache.Verify(c => c.SetTimelineAsync(It.IsAny<string>(), It.Is<IEnumerable<NewsFeedItem>>(i => i.Count() == 50)), Times.Once);
    }

    [TestMethod]
    public async Task Warm_RequestsTheUnfilteredTimeline()
    {
        // Reusing the client's live filter state would store a tag or unread
        // slice under the key hydration reads back as the default timeline.
        var (client, handler, _) = Create(Items(5), JsonSerializer.Serialize(Items(50)));
        client.IsFilterUnread = true;
        client.IsFilterSaved = true;
        client.FilterTag = "tech";

        await client.WarmTimelineCacheAsync();

        var timelineRequest = handler.RequestFor("api/item/timeline");
        Assert.IsNotNull(timelineRequest, "the warm should fetch the timeline");
        var query = timelineRequest.Query;
        StringAssert.Contains(query, "isFilterUnread=False");
        StringAssert.Contains(query, "isFilterSaved=False");
        StringAssert.Contains(query, "filterTag=&");
        StringAssert.Contains(query, $"pageSize={PostCache.MaxTimelineItems}");
        StringAssert.Contains(query, "page=0");
    }

    [TestMethod]
    public async Task Warm_AlsoPrefetchesTheBodiesForThatPage()
    {
        // Caching headlines the user still can't read defeats the point.
        var (client, handler, _) = Create(Items(5), JsonSerializer.Serialize(Items(50)));

        await client.WarmTimelineCacheAsync();

        Assert.IsNotNull(handler.RequestFor("api/item/contentBatch"), "bodies should be prefetched in one batch");
    }

    [TestMethod]
    public async Task Warm_KeepsUnsyncedFlagsFromTheOutbox()
    {
        var (client, handler, cache) = Create(Items(10), JsonSerializer.Serialize(Items(50)));
        cache.Setup(c => c.GetPendingWritesAsync()).ReturnsAsync(new List<PendingWrite>
        {
            new("3", PendingWrite.ReadKind, true, 1),
        });

        List<NewsFeedItem> persisted = null;
        cache.Setup(c => c.SetTimelineAsync(It.IsAny<string>(), It.IsAny<IEnumerable<NewsFeedItem>>()))
            .Callback<string, IEnumerable<NewsFeedItem>>((_, i) => persisted = i.ToList())
            .Returns(Task.CompletedTask);

        await client.WarmTimelineCacheAsync();

        // The server page says unread; the queued write must win, or the warm
        // would overwrite the cache with staler flags than the screen shows.
        Assert.IsNotNull(persisted);
        Assert.IsTrue(persisted.Single(i => i.Id == "3").IsRead);
    }

    [TestMethod]
    public async Task Warm_ReportsFailureSoItCanBeRetried()
    {
        var (client, _, cache) = Create(Items(5), "nope", HttpStatusCode.ServiceUnavailable);

        var warm = await client.WarmTimelineCacheAsync();

        Assert.IsFalse(warm);
        cache.Verify(c => c.SetTimelineAsync(It.IsAny<string>(), It.IsAny<IEnumerable<NewsFeedItem>>()), Times.Never);
    }

    [TestMethod]
    public async Task Warm_TreatsAnEmptyTimelineAsDone()
    {
        // A brand-new account has nothing to cache; retrying every probe would
        // be pointless traffic.
        var (client, _, cache) = Create(new List<NewsFeedItem>(), "[]");

        Assert.IsTrue(await client.WarmTimelineCacheAsync());
        cache.Verify(c => c.SetTimelineAsync(It.IsAny<string>(), It.IsAny<IEnumerable<NewsFeedItem>>()), Times.Never);
    }
}
