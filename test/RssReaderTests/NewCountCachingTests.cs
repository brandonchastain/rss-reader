using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RssApp.ComponentServices;
using RssApp.Config;
using RssApp.Contracts;
using RssApp.Data;
using RssReader.Server.Services;
using Server.Controllers;

namespace RssReaderTests;

/// <summary>
/// The timeline's "new posts?" probe is polled by every open tab. The endpoint
/// lets the browser cache the answer briefly and answers revalidations from the
/// in-memory change stamp, so an idle replica serves 304s without touching SQL.
/// </summary>
[TestClass]
public class NewCountCachingTests
{
    private static readonly RssUser TestUser = new("testuser", 7);

    private Mock<IItemRepository> itemRepo = null!;
    private Mock<IFeedRepository> feedRepo = null!;
    private ItemsChangeStamp stamp = null!;

    private ItemController CreateController(string ifNoneMatch = null)
    {
        this.itemRepo = new Mock<IItemRepository>();
        this.feedRepo = new Mock<IFeedRepository>();
        this.stamp = new ItemsChangeStamp();
        var resolver = new Mock<IUserResolver>();
        resolver.Setup(r => r.ResolveUser(It.IsAny<ClaimsPrincipal>())).Returns(TestUser);
        this.itemRepo
            .Setup(r => r.GetNewItemCountAsync(It.IsAny<NewsFeed>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(3);

        var controller = new ItemController(
            this.itemRepo.Object,
            this.feedRepo.Object,
            new Mock<IUserRepository>().Object,
            resolver.Object,
            new Mock<IFeedRefresher>().Object,
            new RssAppConfig(),
            NullLogger<ItemController>.Instance,
            this.stamp);

        var http = new DefaultHttpContext();
        if (ifNoneMatch != null)
        {
            http.Request.Headers.IfNoneMatch = ifNoneMatch;
        }
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    private static string CurrentEtag(ItemsChangeStamp stamp) => $"\"{stamp.Current:x}\"";

    [TestMethod]
    public async Task NewCount_SetsPrivateMaxAgeAndEtag()
    {
        var controller = CreateController();

        var result = await controller.NewCountAsync(1, 1);

        Assert.IsInstanceOfType(result, typeof(OkObjectResult));
        Assert.AreEqual(3, ((OkObjectResult)result).Value);
        StringAssert.Contains(controller.Response.Headers.CacheControl.ToString(), "private");
        StringAssert.Contains(controller.Response.Headers.CacheControl.ToString(), "max-age=");
        Assert.AreEqual(CurrentEtag(this.stamp), controller.Response.Headers.ETag.ToString());
    }

    [TestMethod]
    public async Task NewCount_ReturnsNotModifiedWithoutSqlWhenNothingChanged()
    {
        var probe = new ItemsChangeStamp();
        var controller = CreateController(ifNoneMatch: null);
        // Use the controller's own stamp for the matching tag.
        var etag = CurrentEtag(this.stamp);
        controller.Request.Headers.IfNoneMatch = etag;

        var result = await controller.NewCountAsync(1, 1);

        Assert.IsInstanceOfType(result, typeof(StatusCodeResult));
        Assert.AreEqual(StatusCodes.Status304NotModified, ((StatusCodeResult)result).StatusCode);
        this.itemRepo.Verify(
            r => r.GetNewItemCountAsync(It.IsAny<NewsFeed>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<IEnumerable<string>>()),
            Times.Never, "A matching ETag must be answered without a count query");
        this.feedRepo.Verify(r => r.GetHiddenFeedUrls(It.IsAny<RssUser>()), Times.Never);
        Assert.AreNotEqual(probe.Current, 0);
    }

    [TestMethod]
    public async Task NewCount_RecomputesAfterItemsChange()
    {
        var controller = CreateController();
        var stale = CurrentEtag(this.stamp);
        this.stamp.Bump();
        controller.Request.Headers.IfNoneMatch = stale;

        var result = await controller.NewCountAsync(1, 1);

        Assert.IsInstanceOfType(result, typeof(OkObjectResult));
        Assert.AreNotEqual(stale, controller.Response.Headers.ETag.ToString());
    }

    [TestMethod]
    public void ChangeStamp_MovesOnEveryItemMutation()
    {
        var inner = new Mock<IItemRepository>();
        inner.Setup(r => r.AddItemsAsync(It.IsAny<IEnumerable<NewsFeedItem>>())).ReturnsAsync(1);
        var s = new ItemsChangeStamp();
        var repo = new CachingItemRepository(inner.Object, new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()), s);
        var item = new NewsFeedItem { Id = "1", UserId = TestUser.Id, Href = "h", FeedUrl = "f" };

        var before = s.Current;
        repo.MarkAsRead(item, true, TestUser);
        Assert.AreNotEqual(before, s.Current);

        before = s.Current;
        repo.AddItemsAsync(new[] { item }).GetAwaiter().GetResult();
        Assert.AreNotEqual(before, s.Current);
    }
}
