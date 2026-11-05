namespace RssReaderTests;

using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RssApp.Config;
using RssApp.Contracts;
using RssApp.RssClient;
using WasmApp.Pages;
using WasmApp.Services;

/// <summary>
/// The first load of a post list when it fails with nothing on screen.
///
/// A new account is created by api/user/register, which the nav menu and the
/// toolbar call in parallel with the timeline's first fetch. When the fetch
/// arrived first the server answered 404 ("Authenticated user not found") and
/// the timeline showed "No posts found" until the reader reloaded. The page now
/// registers and retries once on a 404.
///
/// Other failures used to show the same "No posts found". Transient ones are
/// now retried with a backoff, and a load that still fails says so and offers a
/// Retry button.
/// </summary>
[TestClass]
public sealed class NewUserFirstLoadTests
{
    /// <summary>The timeline with the backoff taken out, so tests do not wait for it.</summary>
    public sealed class NoDelayTimeline : Timeline
    {
        protected override IReadOnlyList<TimeSpan> EmptyLoadRetryDelays =>
            new[] { TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero };
    }

    private const string FailedText = "Couldn't load posts";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private static NewsFeedItem Post(string id) => new()
    {
        Id = id,
        Title = "Post " + id,
        Href = "https://news.example.com/" + id,
        FeedUrl = "https://news.example.com/feed",
        UserId = 1,
        PublishDate = "2024-01-01T00:00:00Z",
        PublishDateOrder = long.Parse(id),
        FeedTags = new List<string>(),
    };

    private static (Bunit.TestContext ctx, Mock<IFeedClient> feeds, Mock<IUserClient> users) Setup()
    {
        var ctx = new Bunit.TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var feedClient = new Mock<IFeedClient>();
        feedClient.Setup(f => f.FlushPendingWritesAsync()).ReturnsAsync(0);
        feedClient.Setup(f => f.WarmTimelineCacheAsync()).ReturnsAsync(true);

        var userClient = new Mock<IUserClient>();
        userClient.Setup(u => u.GetFeedUserAsync()).ReturnsAsync(new RssUser("new@example.test", 7));

        var cache = new Mock<IPostCache>();
        cache.Setup(c => c.GetPendingWritesAsync()).ReturnsAsync(new List<PendingWrite>());

        ctx.Services.AddLogging();
        ctx.Services.AddSingleton(new RssWasmConfig { ApiBaseUrl = "https://api.example.test/" });
        ctx.Services.AddSingleton(feedClient.Object);
        ctx.Services.AddSingleton(userClient.Object);
        ctx.Services.AddSingleton(cache.Object);
        return (ctx, feedClient, userClient);
    }

    private static HttpRequestException Status(HttpStatusCode code) => new("fail", null, code);

    private static void VerifyFetches(Mock<IFeedClient> feeds, int times) =>
        feeds.Verify(f => f.GetTimelineAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<long?>(), It.IsAny<long?>()), Times.Exactly(times));

    [TestMethod]
    public void FirstFetchBeforeRegistration_RegistersAndShowsThePosts()
    {
        var (ctx, feeds, users) = Setup();
        using var _ = ctx;
        feeds
            .SetupSequence(f => f.GetTimelineAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<long?>(), It.IsAny<long?>()))
            .ThrowsAsync(Status(HttpStatusCode.NotFound))
            .ReturnsAsync(new[] { Post("2"), Post("1") });

        var cut = ctx.RenderComponent<NoDelayTimeline>();

        cut.WaitForAssertion(() =>
        {
            Assert.IsTrue(cut.Markup.Contains("Post 2"), "the retried fetch's posts render");
            Assert.IsFalse(cut.Markup.Contains("No posts found"));
        }, Wait);
        users.Verify(u => u.GetFeedUserAsync(), Times.Once);
    }

    [TestMethod]
    public void PersistentNotFound_RegistersOnceAndShowsTheFailure()
    {
        var (ctx, feeds, users) = Setup();
        using var _ = ctx;
        feeds
            .Setup(f => f.GetTimelineAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<long?>(), It.IsAny<long?>()))
            .ThrowsAsync(Status(HttpStatusCode.NotFound));

        var cut = ctx.RenderComponent<NoDelayTimeline>();

        cut.WaitForAssertion(() => Assert.IsTrue(cut.Markup.Contains(FailedText)), Wait);
        VerifyFetches(feeds, 2);
        users.Verify(u => u.GetFeedUserAsync(), Times.Once);
    }

    [TestMethod]
    public void TransientFailures_AreRetriedUntilTheLoadSucceeds()
    {
        var (ctx, feeds, users) = Setup();
        using var _ = ctx;
        feeds
            .SetupSequence(f => f.GetTimelineAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<long?>(), It.IsAny<long?>()))
            .ThrowsAsync(new HttpRequestException("network down"))
            .ThrowsAsync(Status(HttpStatusCode.ServiceUnavailable))
            .ReturnsAsync(new[] { Post("2"), Post("1") });

        var cut = ctx.RenderComponent<NoDelayTimeline>();

        cut.WaitForAssertion(() => Assert.IsTrue(cut.Markup.Contains("Post 2")), Wait);
        Assert.IsFalse(cut.Markup.Contains(FailedText));
        VerifyFetches(feeds, 3);
        users.Verify(u => u.GetFeedUserAsync(), Times.Never);
    }

    [TestMethod]
    public void PersistentServerError_GivesUpAfterTheBackoffAndOffersRetry()
    {
        var (ctx, feeds, _) = Setup();
        using var __ = ctx;
        feeds
            .Setup(f => f.GetTimelineAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<long?>(), It.IsAny<long?>()))
            .ThrowsAsync(Status(HttpStatusCode.ServiceUnavailable));

        var cut = ctx.RenderComponent<NoDelayTimeline>();

        cut.WaitForAssertion(() => Assert.IsTrue(cut.Markup.Contains(FailedText)), Wait);
        Assert.IsFalse(cut.Markup.Contains("No posts found"), "a failed load must not claim the list is empty");
        VerifyFetches(feeds, 4);

        // The Retry button runs the whole load again, backoff included.
        feeds
            .Setup(f => f.GetTimelineAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<long?>(), It.IsAny<long?>()))
            .ReturnsAsync(new[] { Post("2"), Post("1") });
        cut.Find(".load-failed-message button").Click();

        cut.WaitForAssertion(() => Assert.IsTrue(cut.Markup.Contains("Post 2")), Wait);
        Assert.IsFalse(cut.Markup.Contains(FailedText));
    }

    [TestMethod]
    public void NonTransientFailure_IsNotRetried()
    {
        var (ctx, feeds, users) = Setup();
        using var _ = ctx;
        feeds
            .Setup(f => f.GetTimelineAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<long?>(), It.IsAny<long?>()))
            .ThrowsAsync(Status(HttpStatusCode.Unauthorized));

        var cut = ctx.RenderComponent<NoDelayTimeline>();

        cut.WaitForAssertion(() => Assert.IsTrue(cut.Markup.Contains(FailedText)), Wait);
        VerifyFetches(feeds, 1);
        users.Verify(u => u.GetFeedUserAsync(), Times.Never);
    }

    [TestMethod]
    public void EmptyAccount_StillSaysNoPostsFound()
    {
        var (ctx, feeds, _) = Setup();
        using var __ = ctx;
        feeds
            .Setup(f => f.GetTimelineAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<long?>(), It.IsAny<long?>()))
            .ReturnsAsync(Array.Empty<NewsFeedItem>());

        var cut = ctx.RenderComponent<NoDelayTimeline>();

        cut.WaitForAssertion(() => Assert.IsTrue(cut.Markup.Contains("No posts found")), Wait);
        Assert.IsFalse(cut.Markup.Contains(FailedText));
    }
}
