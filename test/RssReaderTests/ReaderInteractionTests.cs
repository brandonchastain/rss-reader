namespace RssReaderTests;

using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RssApp.Config;
using RssApp.Contracts;
using RssApp.RssClient;
using WasmApp.Pages.Detail;
using WasmApp.Services;

/// <summary>
/// The headline opens the in-app reader; the original is one click away on the
/// ↗ link and at the foot of the reader. Opening marks a post read, closing
/// does not, and a day's loaded posts can be marked read from its divider.
/// </summary>
[TestClass]
public sealed class ReaderInteractionTests
{
    private const string ApiBaseUrl = "https://api.example.test/";

    private static NewsFeedItem Post(string id, string publishDate = "2024-01-02T12:00:00") => new()
    {
        Id = id,
        Title = "Post " + id,
        Href = "https://news.example.com/" + id,
        FeedUrl = "https://news.example.com/feed",
        UserId = 7,
        PublishDate = publishDate,
        PublishDateOrder = long.Parse(id),
        IsRead = false,
        FeedTags = new List<string>(),
    };

    [TestMethod]
    public void PlainHeadlineClick_OpensTheReaderAndMarksRead()
    {
        using var ctx = new Bunit.TestContext();
        var feedClient = Register(ctx);
        var post = Post("42");

        var cut = ctx.RenderComponent<PostDetail>(p => p.Add(x => x.Post, post));
        cut.Find("a.post-headline").Click();

        Assert.IsTrue(post.IsBeingPreviewed, "a plain headline click opens the reader");
        Assert.IsTrue(post.IsRead);
        feedClient.Verify(f => f.MarkAsReadAsync(post, true), Times.Once);

        // The original stays reachable from inside the reader.
        var original = cut.Find(".reader-footer a[data-article-link]");
        Assert.AreEqual(post.Href, original.GetAttribute("href"));
        Assert.AreEqual(MarkReadRequest.Url(ApiBaseUrl, post.Id, ReadState.Read), original.GetAttribute("data-mark-read-url"));
    }

    [TestMethod]
    public void ModifiedHeadlineClick_LeavesTheReaderClosed()
    {
        using var ctx = new Bunit.TestContext();
        var feedClient = Register(ctx);
        var post = Post("42");

        var cut = ctx.RenderComponent<PostDetail>(p => p.Add(x => x.Post, post));
        cut.Find("a.post-headline").Click(new MouseEventArgs { CtrlKey = true });

        // The browser opens the original in a new tab; app.js sends the write.
        Assert.IsFalse(post.IsBeingPreviewed);
        Assert.IsTrue(post.IsRead);
        feedClient.Verify(f => f.MarkAsReadAsync(It.IsAny<NewsFeedItem>(), It.IsAny<bool>()), Times.Never);
    }

    [TestMethod]
    public void HeadlineAndExternalLink_BothPointAtTheOriginal()
    {
        using var ctx = new Bunit.TestContext();
        Register(ctx);
        var post = Post("42");

        var cut = ctx.RenderComponent<PostDetail>(p => p.Add(x => x.Post, post));

        Assert.AreEqual(post.Href, cut.Find("a.post-headline").GetAttribute("href"));
        var external = cut.Find("a.post-external");
        Assert.AreEqual(post.Href, external.GetAttribute("href"));
        Assert.IsTrue(external.HasAttribute("data-article-link"), "the ↗ link marks read before navigating");
    }

    [TestMethod]
    public void CollapsingAPostMarkedUnreadWhileOpen_LeavesItUnread()
    {
        using var ctx = new Bunit.TestContext();
        var feedClient = Register(ctx);
        var post = Post("42");

        var cut = ctx.RenderComponent<PostDetail>(p => p.Add(x => x.Post, post));
        cut.Find("a.post-headline").Click();
        cut.Find("[data-action=\"toggle-read\"]").Click();
        Assert.IsFalse(post.IsRead);

        cut.Find(".reader-footer button").Click();

        Assert.IsFalse(post.IsBeingPreviewed);
        Assert.IsFalse(post.IsRead, "collapsing must not mark the post read again");
        feedClient.Verify(f => f.MarkAsReadAsync(post, true), Times.Once);
    }

    [TestMethod]
    public void MarkAllRead_MarksOnlyThatDaysPosts()
    {
        using var ctx = new Bunit.TestContext();
        var feedClient = Register(ctx);
        var today = new[] { Post("4"), Post("3") };
        var yesterday = Post("2", "2024-01-01T12:00:00");
        var posts = new List<NewsFeedItem>(today) { yesterday };

        var cut = ctx.RenderComponent<CascadingValue<List<NewsFeedItem>>>(p => p
            .Add(x => x.Name, "Posts")
            .Add(x => x.Value, posts)
            .AddChildContent<PostTable>(t => t
                .Add(x => x.FeedClient, feedClient.Object)
                .Add(x => x.NewPostsPollEnabled, false)));

        var buttons = cut.FindAll(".day-mark-read");
        Assert.AreEqual(2, buttons.Count, "each day with unread posts offers the action");

        buttons[0].Click();

        Assert.IsTrue(today.All(p => p.IsRead));
        Assert.IsFalse(yesterday.IsRead);
        foreach (var post in today)
        {
            feedClient.Verify(f => f.MarkAsReadAsync(post, true), Times.Once);
        }
        feedClient.Verify(f => f.MarkAsReadAsync(yesterday, It.IsAny<bool>()), Times.Never);
        Assert.AreEqual(1, cut.FindAll(".day-mark-read").Count, "a day with nothing unread drops the action");
    }

    private static Mock<IFeedClient> Register(Bunit.TestContext ctx)
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var feedClient = new Mock<IFeedClient>();
        feedClient.Setup(f => f.GetItemContentAsync(It.IsAny<NewsFeedItem>())).ReturnsAsync("<p>body</p>");
        var cache = new Mock<IPostCache>();
        cache.Setup(c => c.GetPendingWritesAsync()).ReturnsAsync(new List<PendingWrite>());
        ctx.Services.AddLogging();
        ctx.Services.AddSingleton(new RssWasmConfig { ApiBaseUrl = ApiBaseUrl });
        ctx.Services.AddSingleton(feedClient.Object);
        ctx.Services.AddSingleton(new Mock<IUserClient>().Object);
        ctx.Services.AddSingleton(cache.Object);
        return feedClient;
    }
}
