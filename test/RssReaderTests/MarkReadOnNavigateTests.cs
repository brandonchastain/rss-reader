namespace RssReaderTests;

using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RssApp.Config;
using RssApp.Contracts;
using RssApp.RssClient;
using WasmApp.Pages.Detail;
using WasmApp.Services;

/// <summary>
/// Clicking a post's outbound link must mark it read in a way that survives the
/// page unloading for that link. The request is sent by app.js from the link's
/// data attribute, so these tests pin the contract between the component, the
/// URL builder and the outbox JSON that app.js writes.
/// </summary>
[TestClass]
public sealed class MarkReadOnNavigateTests
{
    private const string ApiBaseUrl = "https://api.example.test/";

    private static NewsFeedItem UnreadPost() => new()
    {
        Id = "42",
        Title = "A post",
        Href = "https://news.example.com/story",
        FeedUrl = "https://news.example.com/feed",
        UserId = 7,
        IsRead = false,
        FeedTags = new List<string>(),
    };

    [TestMethod]
    public void Url_IsAbsoluteOnTheApiOrigin()
    {
        // A frontend-origin path reaches the old proxy route without the API's
        // session cookie, which is how the write used to be lost.
        var url = MarkReadRequest.Url(ApiBaseUrl, "42", ReadState.Read);

        Assert.AreEqual("https://api.example.test/api/item/markAsRead?itemId=42&isRead=true", url);
        Assert.AreEqual(
            "https://api.example.test/api/item/markAsRead?itemId=42&isRead=false",
            MarkReadRequest.Url(ApiBaseUrl, "42", ReadState.Unread));
    }

    [TestMethod]
    public void OutboxEntryWrittenByAppJs_DeserializesToAPendingWrite()
    {
        // Exactly the shape rssApp.cache.enqueueOutbox writes: PascalCase, no
        // FeedUrl/Href/UserId. PostCache must read it back as a read write.
        var json = """[{"ItemId":"42","Kind":"read","Value":true,"Ts":1700000000000}]""";

        var writes = JsonSerializer.Deserialize<List<PendingWrite>>(json);

        Assert.IsNotNull(writes);
        Assert.AreEqual(1, writes.Count);
        Assert.AreEqual("42", writes[0].ItemId);
        Assert.AreEqual(PendingWrite.ReadKind, writes[0].Kind);
        Assert.IsTrue(writes[0].Value);
        Assert.AreEqual(1700000000000, writes[0].Ts);

        var item = UnreadPost();
        PendingWrites.Apply(writes, new[] { item });
        Assert.IsTrue(item.IsRead, "a replayed app.js outbox entry must overlay onto a fetched item");
    }

    [TestMethod]
    public void OutboundLink_CarriesTheApiMarkReadUrl()
    {
        using var ctx = new Bunit.TestContext();
        var feedClient = Register(ctx);
        var post = UnreadPost();

        var cut = ctx.RenderComponent<PostDetail>(p => p.Add(x => x.Post, post));

        var link = cut.Find("a[data-article-link][data-mark-read-url]");
        Assert.AreEqual(post.Href, link.GetAttribute("href"));
        Assert.AreEqual(post.Id, link.GetAttribute("data-post-id"));
        Assert.AreEqual(
            MarkReadRequest.Url(ApiBaseUrl, post.Id, ReadState.Read),
            link.GetAttribute("data-mark-read-url"));
        Assert.IsTrue(cut.Find(".row").ClassList.Contains("post-unread"));
    }

    [TestMethod]
    public void ClickingTheOutboundLink_MarksReadLocallyBeforeNavigation()
    {
        using var ctx = new Bunit.TestContext();
        var feedClient = Register(ctx);
        var post = UnreadPost();

        var cut = ctx.RenderComponent<PostDetail>(p => p.Add(x => x.Post, post));
        cut.Find("a[data-article-link][data-mark-read-url]").Click();

        // Local state flips synchronously so any snapshot of the page shows Read.
        Assert.IsTrue(post.IsRead);
        Assert.IsTrue(cut.Find(".row").ClassList.Contains("post-read"));

        // The request itself is owned by the capture-phase handler in app.js
        // (fetch keepalive), not by an awaited HttpClient call that the unload
        // would abort.
        feedClient.Verify(f => f.MarkAsReadAsync(It.IsAny<NewsFeedItem>(), It.IsAny<bool>()), Times.Never);
    }

    private static Mock<IFeedClient> Register(Bunit.TestContext ctx)
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var feedClient = new Mock<IFeedClient>();
        ctx.Services.AddLogging();
        ctx.Services.AddSingleton(new RssWasmConfig { ApiBaseUrl = ApiBaseUrl });
        ctx.Services.AddSingleton(feedClient.Object);
        ctx.Services.AddSingleton(new Mock<IUserClient>().Object);
        ctx.Services.AddSingleton(new Mock<IPostCache>().Object);
        return feedClient;
    }
}
