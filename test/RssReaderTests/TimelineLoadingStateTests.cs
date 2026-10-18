namespace RssReaderTests;

using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RssApp.Config;
using RssApp.Contracts;
using RssApp.RssClient;
using WasmApp.Pages;
using WasmApp.Pages.Detail;
using WasmApp.Services;

/// <summary>
/// The timeline cascades IsLoading to PostTable, whose "new posts" probe skips
/// every tick while it is true. The first load runs from OnAfterRenderAsync,
/// whose completion renders nothing by itself, so the page has to render once
/// more after the loading flags flip or the table never learns the load ended
/// and the probe stays silent until some unrelated event re-renders the page.
/// </summary>
[TestClass]
public sealed class TimelineLoadingStateTests
{
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

    [TestMethod]
    public async Task AfterTheFirstLoad_PostTableSeesIsLoadingFalseAndTheProbeFires()
    {
        using var ctx = new Bunit.TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        // Loose mode answers false to every bool call, which would read as a
        // hidden tab and skip the probe for an unrelated reason.
        ctx.JSInterop.Setup<bool>("isDocumentVisible").SetResult(true);

        // The fetch is held open so the probe's first tick (one second after the
        // table renders) is guaranteed to land while the load is in flight and
        // be skipped; only a render after the flags flip can unblock it.
        var fetch = new TaskCompletionSource<IEnumerable<NewsFeedItem>>();
        var probed = new TaskCompletionSource<bool>();

        var feedClient = new Mock<IFeedClient>();
        feedClient
            .Setup(f => f.GetTimelineAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<long?>(), It.IsAny<long?>()))
            .Returns(fetch.Task);
        feedClient
            .Setup(f => f.GetNewTimelineCountAsync(It.IsAny<long>(), It.IsAny<long>()))
            .Callback(() => probed.TrySetResult(true))
            .ReturnsAsync(0);
        feedClient.Setup(f => f.FlushPendingWritesAsync()).ReturnsAsync(0);
        feedClient.Setup(f => f.WarmTimelineCacheAsync()).ReturnsAsync(true);

        var cache = new Mock<IPostCache>();
        cache.Setup(c => c.GetPendingWritesAsync()).ReturnsAsync(new List<PendingWrite>());

        ctx.Services.AddLogging();
        ctx.Services.AddSingleton(new RssWasmConfig { ApiBaseUrl = "https://api.example.test/" });
        ctx.Services.AddSingleton(feedClient.Object);
        ctx.Services.AddSingleton(new Mock<IUserClient>().Object);
        ctx.Services.AddSingleton(cache.Object);

        var cut = ctx.RenderComponent<Timeline>();
        var table = cut.FindComponent<PostTable>();
        Assert.IsTrue(table.Instance.IsLoading, "the table starts out loading");

        await Task.Delay(1500);
        Assert.IsFalse(probed.Task.IsCompleted, "the probe must not fire while the first load is in flight");

        fetch.SetResult(new[] { Post("2"), Post("1") });

        // The probe polls on a one-second initial delay, so a few seconds is
        // ample once the table sees IsLoading = false; without that render it
        // never fires at all.
        var fired = await Task.WhenAny(probed.Task, Task.Delay(TimeSpan.FromSeconds(5))) == probed.Task;

        Assert.IsFalse(cut.Instance.IsLoading, "the page's IsLoading clears once the fetch returns");
        Assert.IsFalse(table.Instance.IsLoading, "PostTable must receive IsLoading = false after the first load");
        Assert.IsTrue(fired, "the new-posts probe must start once the first load completes");
    }
}
