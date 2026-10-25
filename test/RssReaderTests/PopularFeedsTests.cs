namespace RssReaderTests;
using System.Security.Claims;
using Microsoft.Extensions.Logging.Abstractions;
using RssApp.ComponentServices;
using RssApp.Contracts;
using RssApp.Data;

[TestClass]
public sealed class PopularFeedsTests
{
    private string testDbFile;
    private SQLiteUserRepository userRepo;
    private SQLiteFeedRepository feedRepo;

    [TestInitialize]
    public void Setup()
    {
        testDbFile = $"popularfeeds-{Guid.NewGuid():N}.db";
        var connections = new SqliteDbConnections(testDbFile, isReadOnly: false);
        userRepo = new SQLiteUserRepository(connections, new NullLogger<SQLiteUserRepository>());
        feedRepo = new SQLiteFeedRepository(connections, new NullLogger<SQLiteFeedRepository>());
    }

    [TestCleanup]
    public void Cleanup()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(testDbFile)) File.Delete(testDbFile); } catch { }
    }

    private RssUser[] AddUsers(int count)
    {
        var users = new RssUser[count];
        for (var i = 0; i < count; i++)
        {
            users[i] = userRepo.AddUser($"user{i}", i + 1);
        }
        return users;
    }

    private void Subscribe(RssUser user, params string[] urls)
        => feedRepo.AddFeeds(user, urls.Select(u => new NewsFeed(u, user.Id)));

    [TestMethod]
    public void Popular_RanksBySubscribers_AndHidesOwnAndRareFeeds()
    {
        var u = AddUsers(5);
        Subscribe(u[0], "https://a.example/feed", "https://b.example/feed", "https://rare.example/feed");
        Subscribe(u[1], "https://a.example/feed", "https://b.example/feed");
        Subscribe(u[2], "https://a.example/feed", "https://b.example/feed");
        Subscribe(u[3], "https://a.example/feed");

        var popular = feedRepo.GetPopularFeeds(u[4], limit: 10, minSubscribers: 3);

        CollectionAssert.AreEqual(
            new[] { "https://a.example/feed", "https://b.example/feed" },
            popular.Select(p => p.Href).ToList());
        Assert.AreEqual(4, popular[0].SubscriberCount);

        // Already-subscribed feeds are not offered again.
        Subscribe(u[4], "https://a.example/feed");
        CollectionAssert.AreEqual(
            new[] { "https://b.example/feed" },
            feedRepo.GetPopularFeeds(u[4], 10, 3).Select(p => p.Href).ToList());
    }

    [TestMethod]
    public void Popular_NeverRevealsUrlsWithSecrets()
    {
        var u = AddUsers(4);
        foreach (var user in u.Take(3))
        {
            Subscribe(user, "https://private.example/feed?token=abc", "http://insecure.example/feed", "https://me:pw@cred.example/feed");
        }

        Assert.AreEqual(0, feedRepo.GetPopularFeeds(u[3], 10, 3).Count);
    }

    [TestMethod]
    public void Popular_RespectsLimit()
    {
        var u = AddUsers(4);
        foreach (var user in u.Take(3))
        {
            Subscribe(user, "https://a.example/f", "https://b.example/f", "https://c.example/f");
        }

        Assert.AreEqual(2, feedRepo.GetPopularFeeds(u[3], 2, 3).Count);
    }

    [TestMethod]
    public void NewUser_IsSubscribedToStarterFeeds_AndTheListIsShort()
    {
        Assert.IsTrue(StarterFeeds.Urls.Count is >= 1 and <= 8, "starter set should stay small");
        Assert.IsTrue(StarterFeeds.Urls.All(u => u.StartsWith("https://")));

        var resolver = new UserResolver(userRepo, NullLogger<UserResolver>.Instance, feedRepo);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, "github:777") }, "test"));

        var user = resolver.ResolveOrCreateUser(principal);

        CollectionAssert.AreEquivalent(
            StarterFeeds.Urls.ToList(),
            feedRepo.GetFeeds(user).Select(f => f.Href).ToList());

        // Signing in again does not re-add feeds the user deleted.
        feedRepo.DeleteFeed(user, StarterFeeds.Urls[0]);
        resolver.ResolveOrCreateUser(principal);
        Assert.AreEqual(StarterFeeds.Urls.Count - 1, feedRepo.GetFeeds(user).Count());
    }
}
