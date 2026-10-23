using Microsoft.Extensions.Logging.Abstractions;
using RssApp.Config;
using RssReader.Server.Services;

namespace RssReaderTests;

/// <summary>Nightly retention (DB-REDESIGN-SPEC.md section 4): saved posts survive, orphan feeds go after the grace period.</summary>
[TestClass]
public sealed class PostRetentionTests
{
    private static PostRetentionService Create(TestDatabase db, int days = 180, int grace = 7)
        => new(db.Connections, new RssAppConfig { PostRetentionDays = days, FeedOrphanGraceDays = grace }, NullLogger<PostRetentionService>.Instance);

    [TestMethod]
    public async Task OldUnsavedPosts_AreDeleted_SavedOnesSurvive()
    {
        using var db = new TestDatabase("retention");
        var alice = db.AddUser("alice");
        const string url = "https://example.com/feed";
        db.Subscribe(alice, url);
        await db.AddPosts(url, 5);
        var saved = (await db.Timeline(alice))[2];
        db.Items.SavePost(saved, alice);

        // Nothing is old yet.
        var none = Create(db).RunOnce(DateTimeOffset.UtcNow);
        Assert.AreEqual(0, none.PostsDeleted);

        // Everything is older than the window.
        var result = Create(db).RunOnce(DateTimeOffset.UtcNow.AddDays(181));

        Assert.AreEqual(4, result.PostsDeleted);
        var left = await db.Timeline(alice);
        Assert.AreEqual(1, left.Count);
        Assert.AreEqual(saved.Id, left[0].Id);
        Assert.IsTrue(left[0].IsSaved);
        Assert.AreEqual(1, db.Scalar("SELECT count(*) FROM PostContent"), "content cascades");
        Assert.AreEqual(1, db.Scalar("SELECT count(*) FROM Posts_fts"), "the search index follows");
    }

    [TestMethod]
    public async Task RetentionDisabled_DeletesNoPosts()
    {
        using var db = new TestDatabase("retention");
        var alice = db.AddUser("alice");
        const string url = "https://example.com/feed";
        db.Subscribe(alice, url);
        await db.AddPosts(url, 3);

        var result = Create(db, days: 0).RunOnce(DateTimeOffset.UtcNow.AddYears(5));
        Assert.AreEqual(0, result.PostsDeleted);
    }

    [TestMethod]
    public async Task OrphanFeeds_AreRemovedAfterGrace_UnlessAPostIsSaved()
    {
        using var db = new TestDatabase("retention");
        var alice = db.AddUser("alice");
        const string dropped = "https://example.com/dropped";
        const string kept = "https://example.com/kept";
        const string active = "https://example.com/active";
        db.Subscribe(alice, dropped);
        db.Subscribe(alice, kept);
        db.Subscribe(alice, active);
        await db.AddPosts(dropped, 2, 1000, "d");
        await db.AddPosts(kept, 2, 2000, "k");
        await db.AddPosts(active, 2, 3000, "a");
        db.Items.SavePost((await db.Timeline(alice)).First(i => i.FeedUrl == kept), alice);

        db.Feeds.DeleteFeed(alice, dropped);
        db.Feeds.DeleteFeed(alice, kept);

        // Inside the grace period nothing happens.
        Assert.AreEqual(0, Create(db).RunOnce(DateTimeOffset.UtcNow.AddDays(6)).FeedsDeleted);

        var result = Create(db).RunOnce(DateTimeOffset.UtcNow.AddDays(8));

        Assert.AreEqual(1, result.FeedsDeleted);
        Assert.AreEqual(0, db.Scalar($"SELECT count(*) FROM Feeds WHERE Url = '{dropped}'"));
        Assert.AreEqual(1, db.Scalar($"SELECT count(*) FROM Feeds WHERE Url = '{kept}'"), "a feed with a saved post is kept");
        Assert.AreEqual(1, db.Scalar($"SELECT count(*) FROM Feeds WHERE Url = '{active}'"), "a subscribed feed is never an orphan");
        Assert.AreEqual(4, db.Scalar("SELECT count(*) FROM Posts"), "the dropped feed's posts cascaded");
        Assert.AreEqual(0, db.Feeds.GetFeedFetchStates().Count(s => s.Url != active), "unsubscribed feeds are not scheduled");
    }
}
