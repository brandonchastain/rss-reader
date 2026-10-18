namespace RssReaderTests;

using Microsoft.AspNetCore.Mvc;
using System.Reflection;
using Server.Controllers;

[TestClass]
public sealed class ResponseOptimizationTests
{
    [TestMethod]
    public void GetItemContent_Should_Have_ResponseCache_Attribute()
    {
        var method = typeof(ItemController).GetMethod("GetItemContent");
        Assert.IsNotNull(method, "GetItemContent method should exist on ItemController");

        var attr = method.GetCustomAttribute<ResponseCacheAttribute>();
        Assert.IsNotNull(attr, "GetItemContent should have [ResponseCache] attribute");
        Assert.AreEqual(3600, attr.Duration, "Cache duration should be 1 hour (3600s)");
        Assert.AreEqual(ResponseCacheLocation.Client, attr.Location, "Cache location should be Client (private)");
    }

    [TestMethod]
    public void Timeline_Should_Send_NoStore()
    {
        // Carries per-user read/saved flags: a back-navigation must never be
        // answered from the browser's HTTP cache with a pre-click copy.
        var method = typeof(ItemController).GetMethod("TimelineAsync");
        Assert.IsNotNull(method, "TimelineAsync method should exist on ItemController");

        var attr = method.GetCustomAttribute<ResponseCacheAttribute>();
        Assert.IsNotNull(attr, "TimelineAsync should have [ResponseCache(NoStore = true)] — affected by read/save state");
        Assert.IsTrue(attr.NoStore, "TimelineAsync must send Cache-Control: no-store");
        Assert.AreEqual(ResponseCacheLocation.None, attr.Location);
    }

    [TestMethod]
    public void FeedAsync_Should_Send_NoStore()
    {
        // Carries per-user read/saved flags: a back-navigation must never be
        // answered from the browser's HTTP cache with a pre-click copy.
        var method = typeof(ItemController).GetMethod("FeedAsync");
        Assert.IsNotNull(method, "FeedAsync method should exist on ItemController");

        var attr = method.GetCustomAttribute<ResponseCacheAttribute>();
        Assert.IsNotNull(attr, "FeedAsync should have [ResponseCache(NoStore = true)] — affected by read/save state");
        Assert.IsTrue(attr.NoStore, "FeedAsync must send Cache-Control: no-store");
        Assert.AreEqual(ResponseCacheLocation.None, attr.Location);
    }

    [TestMethod]
    public void SearchAsync_Should_Send_NoStore()
    {
        // Carries per-user read/saved flags: a back-navigation must never be
        // answered from the browser's HTTP cache with a pre-click copy.
        var method = typeof(ItemController).GetMethod("SearchAsync");
        Assert.IsNotNull(method, "SearchAsync method should exist on ItemController");

        var attr = method.GetCustomAttribute<ResponseCacheAttribute>();
        Assert.IsNotNull(attr, "SearchAsync should have [ResponseCache(NoStore = true)] — affected by read/save state");
        Assert.IsTrue(attr.NoStore, "SearchAsync must send Cache-Control: no-store");
        Assert.AreEqual(ResponseCacheLocation.None, attr.Location);
    }

    [TestMethod]
    public void GetFeeds_Should_NOT_Have_ResponseCache_Attribute()
    {
        var method = typeof(FeedController).GetMethod("GetFeeds");
        Assert.IsNotNull(method, "GetFeeds method should exist on FeedController");

        var attr = method.GetCustomAttribute<ResponseCacheAttribute>();
        Assert.IsNull(attr, "GetFeeds should NOT have [ResponseCache] — re-called after mutations");
    }

    [TestMethod]
    public void GetUserTagsAsync_Should_NOT_Have_ResponseCache_Attribute()
    {
        var method = typeof(FeedController).GetMethod("GetUserTagsAsync");
        Assert.IsNotNull(method, "GetUserTagsAsync method should exist on FeedController");

        var attr = method.GetCustomAttribute<ResponseCacheAttribute>();
        Assert.IsNull(attr, "GetUserTagsAsync should NOT have [ResponseCache] — re-called after mutations");
    }
}
