using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RssApp.ComponentServices;
using RssApp.Config;
using RssApp.Contracts;
using RssApp.Data;
using Server.Controllers;

namespace SerializerTests;

/// <summary>
/// Covers the batch content endpoint that backs the client's cold-start
/// prefetch: one request for a page of post bodies instead of one per post.
/// </summary>
[TestClass]
public class ContentBatchTests
{
    private static readonly RssUser TestUser = new("testuser", 7);

    private Mock<IItemRepository> itemRepo;
    private Mock<IUserResolver> userResolver;

    private ItemController CreateController(RssUser user = null)
    {
        this.itemRepo = new Mock<IItemRepository>();
        this.userResolver = new Mock<IUserResolver>();
        this.userResolver
            .Setup(r => r.ResolveUser(It.IsAny<ClaimsPrincipal>()))
            .Returns(user ?? TestUser);

        return new ItemController(
            this.itemRepo.Object,
            new Mock<IFeedRepository>().Object,
            new Mock<IUserRepository>().Object,
            this.userResolver.Object,
            new Mock<IFeedRefresher>().Object,
            new RssAppConfig(),
            NullLogger<ItemController>.Instance);
    }

    private void SetupItem(int id, string content)
    {
        var item = new NewsFeedItem { Id = id.ToString(), UserId = TestUser.Id, FeedUrl = "f", Href = "h" + id };
        this.itemRepo.Setup(r => r.GetItem(It.IsAny<RssUser>(), id)).Returns(item);
        this.itemRepo.Setup(r => r.GetItemContent(item)).Returns(content);
    }

    private static Dictionary<string, string> ResultMap(IActionResult result)
    {
        var ok = result as OkObjectResult;
        Assert.IsNotNull(ok, "Expected an OK result.");
        return (Dictionary<string, string>)ok.Value;
    }

    private static string Decode(string base64)
        => Encoding.UTF8.GetString(Convert.FromBase64String(base64));

    [TestMethod]
    public void ContentBatch_ReturnsBase64BodiesKeyedById()
    {
        var controller = CreateController();
        SetupItem(1, "<p>one</p>");
        SetupItem(2, "<p>two</p>");

        var map = ResultMap(controller.GetItemContentBatch("1,2"));

        Assert.AreEqual(2, map.Count);
        Assert.AreEqual("<p>one</p>", Decode(map["1"]));
        Assert.AreEqual("<p>two</p>", Decode(map["2"]));
    }

    [TestMethod]
    public void ContentBatch_OmitsItemsWithoutContentRatherThanFailing()
    {
        var controller = CreateController();
        SetupItem(1, "<p>one</p>");
        SetupItem(2, "");
        SetupItem(3, null);

        var map = ResultMap(controller.GetItemContentBatch("1,2,3"));

        // A single empty post must not sink the whole prefetch.
        Assert.AreEqual(1, map.Count);
        Assert.IsTrue(map.ContainsKey("1"));
    }

    [TestMethod]
    public void ContentBatch_SkipsItemsNotOwnedByTheUser()
    {
        var controller = CreateController();
        SetupItem(1, "<p>mine</p>");
        // GetItem is user-scoped: another user's id resolves to null.
        this.itemRepo.Setup(r => r.GetItem(It.IsAny<RssUser>(), 99)).Returns((NewsFeedItem)null);

        var map = ResultMap(controller.GetItemContentBatch("1,99"));

        Assert.AreEqual(1, map.Count);
        Assert.IsFalse(map.ContainsKey("99"), "Another user's content must not be returned.");
    }

    [TestMethod]
    public void ContentBatch_CapsTheNumberOfItemsFetched()
    {
        var controller = CreateController();
        for (int i = 1; i <= 80; i++)
        {
            SetupItem(i, "<p>" + i + "</p>");
        }

        var ids = string.Join(",", Enumerable.Range(1, 80));
        var map = ResultMap(controller.GetItemContentBatch(ids));

        Assert.AreEqual(50, map.Count, "Batch size should be bounded.");
        this.itemRepo.Verify(r => r.GetItem(It.IsAny<RssUser>(), It.IsAny<int>()), Times.Exactly(50));
    }

    [TestMethod]
    public void ContentBatch_IgnoresGarbageAndDuplicateIds()
    {
        var controller = CreateController();
        SetupItem(1, "<p>one</p>");

        var map = ResultMap(controller.GetItemContentBatch("1, 1 ,abc,,-5,0"));

        Assert.AreEqual(1, map.Count);
        this.itemRepo.Verify(r => r.GetItem(It.IsAny<RssUser>(), It.IsAny<int>()), Times.Once);
    }

    [TestMethod]
    public void ContentBatch_EmptyInputReturnsEmptyMap()
    {
        var controller = CreateController();

        Assert.AreEqual(0, ResultMap(controller.GetItemContentBatch("")).Count);
        Assert.AreEqual(0, ResultMap(controller.GetItemContentBatch(null)).Count);
        this.itemRepo.Verify(r => r.GetItem(It.IsAny<RssUser>(), It.IsAny<int>()), Times.Never);
    }

    [TestMethod]
    public void ContentBatch_UnknownUserIsNotFound()
    {
        var controller = CreateController(user: null);
        this.userResolver
            .Setup(r => r.ResolveUser(It.IsAny<ClaimsPrincipal>()))
            .Returns((RssUser)null);

        Assert.IsInstanceOfType(controller.GetItemContentBatch("1"), typeof(NotFoundObjectResult));
    }
}
