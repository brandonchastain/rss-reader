using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RssApp.Contracts;
using RssApp.Data;
using RssApp.Serialization;

namespace RssReaderTests;

/// <summary>
/// A temp SQLite file with the three repositories wired like production
/// (without the caching decorators). Disposing deletes the file.
/// </summary>
internal sealed class TestDatabase : IDisposable
{
    public string Path { get; }
    public SqliteDbConnections Connections { get; }
    public SQLiteUserRepository Users { get; }
    public SQLiteFeedRepository Feeds { get; }
    public SQLiteItemRepository Items { get; }

    public TestDatabase(string prefix = "schema")
    {
        Path = $"{prefix}-{Guid.NewGuid():N}.db";
        Connections = new SqliteDbConnections(Path, isReadOnly: false);
        Users = new SQLiteUserRepository(Connections, NullLogger<SQLiteUserRepository>.Instance);
        Feeds = new SQLiteFeedRepository(Connections, NullLogger<SQLiteFeedRepository>.Instance);
        Items = new SQLiteItemRepository(Connections, NullLogger<SQLiteItemRepository>.Instance, Feeds, Users);
    }

    public RssUser AddUser(string name) => Users.AddUser(name);

    public NewsFeed Subscribe(RssUser user, string url, params string[] tags)
    {
        var feed = new NewsFeed(url, user.Id) { Tags = tags.ToList() };
        Feeds.AddFeed(feed);
        return feed;
    }

    public async Task<int> AddPosts(string feedUrl, int count, long firstOrder = 1000, string prefix = "post")
    {
        var items = Enumerable.Range(0, count).Select(i => new NewsFeedItem(
            "0", 0, $"{prefix} {i}", $"{feedUrl}/{prefix}-{i}", null, "2025-01-01", $"<p>{prefix} {i}</p>", null)
        {
            FeedUrl = feedUrl,
            PublishDateOrder = firstOrder + i,
        });
        return await Items.AddItemsAsync(items);
    }

    public Task<List<NewsFeedItem>> Timeline(RssUser user, bool unread = false, bool saved = false, string tag = null, bool hideHidden = true, int pageSize = 100)
        => Items.GetItemsAsync(new NewsFeed("%", user.Id), unread, saved, tag, 0, pageSize,
            excludeFeedUrls: hideHidden ? Feeds.GetHiddenFeedUrls(user) : null).ContinueWith(t => t.Result.ToList());

    public long Scalar(string sql)
    {
        using var c = Connections.OpenRead();
        return DatabaseSchema.Scalar(c, sql);
    }

    public void Execute(string sql)
    {
        using var c = Connections.OpenWrite();
        DatabaseSchema.Execute(c, sql);
    }

    public void Dispose()
    {
        Items.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(Path + suffix); } catch { }
        }
    }
}

/// <summary>Repository behaviour of the shared-posts schema (DB-REDESIGN-SPEC.md section 6).</summary>
[TestClass]
public sealed class PostsSchemaTests
{
    private static async Task<List<string>> SearchTitles(TestDatabase db, RssUser user, string query)
        => (await db.Items.SearchItemsAsync(query, user, 0, 50)).Select(i => i.Title).OrderBy(t => t).ToList();

    [TestMethod]
    public async Task Search_MatchesWholeWords_WordStarts_AndPhrases_InTitleAndBody()
    {
        using var db = new TestDatabase("search");
        var user = db.AddUser("search-user");
        const string feed = "https://search.example.com/f";
        db.Subscribe(user, feed);
        await db.Items.AddItemsAsync(new[]
        {
            Post(feed, "1", "Cloudflare outage report", "<p>The edge network went down.</p>"),
            Post(feed, "2", "Weekly roundup", "<p>A zeppelin landed near the quantum widgets factory.</p>"),
            Post(feed, "3", "Unrelated", "<p>Nothing to see.</p>"),
        });

        CollectionAssert.AreEqual(new[] { "Cloudflare outage report" }, await SearchTitles(db, user, "cloudflare"), "whole word in a title");
        CollectionAssert.AreEqual(new[] { "Cloudflare outage report" }, await SearchTitles(db, user, "CLOUDFL"), "word start, any case");
        CollectionAssert.AreEqual(new[] { "Weekly roundup" }, await SearchTitles(db, user, "zeppelin"), "word in the body");
        CollectionAssert.AreEqual(new[] { "Weekly roundup" }, await SearchTitles(db, user, "quantum wid"), "phrase with a cut-short last word");
        Assert.AreEqual(0, (await SearchTitles(db, user, "quantum factory")).Count, "words must be adjacent: it is a phrase");
    }

    [TestMethod]
    public async Task Search_NoLongerMatchesInTheMiddleOfAWord()
    {
        // The deliberate trade for dropping the title LIKE '%word%' scan: only word starts match.
        using var db = new TestDatabase("search-mid");
        var user = db.AddUser("mid-user");
        const string feed = "https://mid.example.com/f";
        db.Subscribe(user, feed);
        await db.Items.AddItemsAsync(new[] { Post(feed, "1", "Cloudflare outage", "<p>x</p>") });

        Assert.AreEqual(0, (await SearchTitles(db, user, "flare")).Count);
        Assert.AreEqual(1, (await SearchTitles(db, user, "cloud")).Count);
    }

    [DataTestMethod]
    [DataRow("!!!")]
    [DataRow("   ")]
    [DataRow("\"")]
    [DataRow("a \"quoted\" word")]
    [DataRow("c++")]
    [DataRow("it's")]
    [DataRow("*")]
    [DataRow("NEAR(a b)")]
    [DataRow("a AND OR b")]
    public async Task Search_OddInput_NeverThrows(string query)
    {
        using var db = new TestDatabase("search-odd");
        var user = db.AddUser("odd-user");
        const string feed = "https://odd.example.com/f";
        db.Subscribe(user, feed);
        await db.Items.AddItemsAsync(new[] { Post(feed, "1", "A quoted word", "<p>c plus plus</p>") });

        await db.Items.SearchItemsAsync(query, user, 0, 20);
    }

    [TestMethod]
    public async Task Search_AnswersFromTheFullTextIndex_WithoutScanningPosts()
    {
        // The plan, not a stopwatch, is what must not regress: a title scan next to the index lookup
        // is what made every search cost about 200 ms.
        using var db = new TestDatabase("search-plan");
        var user = db.AddUser("plan-user");
        db.Subscribe(user, "https://plan.example.com/f");
        await db.AddPosts("https://plan.example.com/f", 50);
        db.Execute("ANALYZE;");

        using var connection = db.Connections.OpenRead();
        using var command = SQLiteItemRepository.BuildSearchCommand(connection, "post", user, 0, 20, useFts: true);
        var plan = ExplainPlan(connection, command);
        StringAssert.Contains(plan, "Posts_fts", plan);
        Assert.IsFalse(plan.Contains("SCAN p", StringComparison.Ordinal), plan);
        Assert.IsFalse(command.CommandText.Contains("LIKE", StringComparison.OrdinalIgnoreCase), "no substring scan alongside the index");
    }

    private static NewsFeedItem Post(string feedUrl, string id, string title, string content) => new(
        "0", 0, title, $"{feedUrl}/{id}", null, "2025-01-01", content, null)
    {
        FeedUrl = feedUrl,
        PublishDateOrder = 1_000 + long.Parse(id),
    };

    [TestMethod]
    public void RemoveTag_DetachesOnlyThatTagFromThatFeed_AndDropsUnusedTags()
    {
        using var db = new TestDatabase("remove-tag");
        var user = db.AddUser("tagger");
        const string a = "https://a.example.com/feed";
        const string b = "https://b.example.com/feed";
        db.Subscribe(user, a);
        db.Subscribe(user, b);
        var feedA = db.Feeds.GetFeed(user, a);
        var feedB = db.Feeds.GetFeed(user, b);
        db.Feeds.AddTag(feedA, "tech");
        db.Feeds.AddTag(feedA, "news");
        db.Feeds.AddTag(feedB, "tech");

        db.Feeds.RemoveTag(feedA, "tech");

        CollectionAssert.AreEqual(new[] { "news" }, db.Feeds.GetFeed(user, a).Tags.ToArray());
        CollectionAssert.AreEqual(new[] { "tech" }, db.Feeds.GetFeed(user, b).Tags.ToArray());

        db.Feeds.RemoveTag(feedB, "tech");
        Assert.IsFalse(db.Feeds.GetTagSettings(user).Any(t => t.Tag == "tech"));
    }

    [TestMethod]
    public void FeedTitle_IsStoredAndShownOnTheSubscription()
    {
        using var db = new TestDatabase("feed-title");
        var user = db.AddUser("titled");
        const string url = "https://titled.example.com/feed";
        db.Subscribe(user, url);
        Assert.IsNull(db.Feeds.GetFeed(user, url).Title);

        db.Feeds.SetFeedTitle(url, "The Titled Blog");

        Assert.AreEqual("The Titled Blog", db.Feeds.GetFeed(user, url).Title);
    }

    [TestMethod]
    public void TryGetFeedTitle_ReadsRssAndAtomTitles()
    {
        Assert.AreEqual("My &amp; Blog".Replace("&amp;", "&"),
            RssApp.Serialization.RssDeserializer.TryGetFeedTitle("<rss><channel><title>My &amp; Blog</title><item><title>x</title></item></channel></rss>"));
        Assert.AreEqual("Atom Blog",
            RssApp.Serialization.RssDeserializer.TryGetFeedTitle("<feed xmlns=\"http://www.w3.org/2005/Atom\"><title>Atom Blog</title><entry><title>x</title></entry></feed>"));
        Assert.IsNull(RssApp.Serialization.RssDeserializer.TryGetFeedTitle("<html>"));
    }

    [TestMethod]
    public void FeedHealth_RoundTripsThroughTheSubscriptionList_AndResetsOnSuccess()
    {
        using var db = new TestDatabase("health");
        var user = db.AddUser("health-user");
        const string url = "https://flaky.example.com/feed";
        db.Subscribe(user, url);

        var fresh = db.Feeds.GetFeeds(user).Single();
        Assert.AreEqual(0, fresh.ConsecutiveFailures);
        Assert.IsNull(fresh.LastError);
        Assert.IsNull(fresh.LastFetchedUtc);
        Assert.IsFalse(fresh.IsFailing);

        var when = DateTimeOffset.UtcNow;
        for (var i = 0; i < NewsFeed.FailingThreshold; i++)
        {
            db.Feeds.RecordFetchResult(url, when, when.AddHours(1), TimeSpan.FromHours(1), null, null, keepValidators: true, failed: true, error: "HTTP 403");
        }

        var failing = db.Feeds.GetFeed(user, url);
        Assert.AreEqual(NewsFeed.FailingThreshold, failing.ConsecutiveFailures);
        Assert.AreEqual("HTTP 403", failing.LastError);
        Assert.IsTrue(failing.IsFailing);
        Assert.IsNotNull(failing.LastFetchedUtc);
        Assert.AreEqual(when.ToUnixTimeSeconds(), failing.LastFetchedUtc!.Value.ToUnixTimeSeconds());

        db.Feeds.RecordFetchResult(url, when, when.AddHours(1), TimeSpan.FromHours(1), null, null, keepValidators: true, failed: false, error: null);
        var recovered = db.Feeds.GetFeeds(user).Single();
        Assert.AreEqual(0, recovered.ConsecutiveFailures);
        Assert.IsNull(recovered.LastError);
        Assert.IsFalse(recovered.IsFailing);
    }

    [TestMethod]
    public async Task Timeline_WalksTheNewestFirstIndex_WithoutSortingEveryPost()
    {
        // Without the index hint the planner starts from the subscriptions and sorts every
        // visible post per page, which grows with the table. The plan, not a stopwatch,
        // is what must not regress.
        using var db = new TestDatabase("plan");
        var user = db.AddUser("plan-user");
        var order = 1000L;
        foreach (var url in new[] { "https://a.example.com/f", "https://b.example.com/f", "https://c.example.com/f" })
        {
            db.Subscribe(user, url, "t");
            await db.AddPosts(url, 200, firstOrder: order, prefix: url.Substring(8, 1));
            order += 7;
        }
        db.Execute("ANALYZE;");

        using var connection = db.Connections.OpenRead();
        foreach (var (unread, cursor) in new[] { (false, false), (true, false), (false, true), (true, true) })
        {
            using var command = SQLiteItemRepository.BuildTimelineCommand(
                connection, new NewsFeed("%", user.Id), unread, false, null, 0, 20,
                cursor ? 10_000 : null, cursor ? 5_000 : null, Array.Empty<string>(), includeContent: false);
            var plan = ExplainPlan(connection, command);
            StringAssert.Contains(plan, "IX_Posts_Timeline", $"unread={unread} cursor={cursor}: {plan}");
            Assert.IsFalse(plan.Contains("TEMP B-TREE", StringComparison.OrdinalIgnoreCase), $"unread={unread} cursor={cursor}: {plan}");
        }

        // The result is unchanged: newest first, 20 to a page, and the cursor continues after it.
        var first = (await db.Items.GetItemsAsync(new NewsFeed("%", user.Id), false, false, null, 0, 20, excludeFeedUrls: Array.Empty<string>())).ToList();
        Assert.AreEqual(20, first.Count);
        var orders = first.Select(i => i.PublishDateOrder).ToList();
        CollectionAssert.AreEqual(orders.OrderByDescending(o => o).ToList(), orders);
        var last = first.Last();
        var next = (await db.Items.GetItemsAsync(new NewsFeed("%", user.Id), false, false, null, 0, 20,
            lastId: long.Parse(last.Id), lastPublishDateOrder: last.PublishDateOrder, excludeFeedUrls: Array.Empty<string>())).ToList();
        Assert.AreEqual(20, next.Count);
        Assert.IsTrue(next.All(i => i.PublishDateOrder <= last.PublishDateOrder));
        Assert.AreEqual(0, next.Select(i => i.Id).Intersect(first.Select(i => i.Id)).Count());
    }

    private static string ExplainPlan(SqliteConnection connection, SqliteCommand command)
    {
        using var explain = connection.CreateCommand();
        explain.CommandText = "EXPLAIN QUERY PLAN " + command.CommandText;
        foreach (SqliteParameter p in command.Parameters)
        {
            explain.Parameters.AddWithValue(p.ParameterName, p.Value);
        }
        using var reader = explain.ExecuteReader();
        var lines = new List<string>();
        while (reader.Read())
        {
            lines.Add(reader.GetString(3));
        }
        return string.Join(" | ", lines);
    }

    [TestMethod]
    public void FeedHealth_IsSharedByEverySubscriberOfTheFeed()
    {
        using var db = new TestDatabase("health-shared");
        var a = db.AddUser("a");
        var b = db.AddUser("b");
        const string url = "https://shared-dead.example.com/feed";
        db.Subscribe(a, url);
        db.Subscribe(b, url);
        db.Feeds.RecordFetchResult(url, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), TimeSpan.FromHours(1), null, null, true, true, "HTTP 404");
        Assert.AreEqual("HTTP 404", db.Feeds.GetFeeds(a).Single().LastError);
        Assert.AreEqual("HTTP 404", db.Feeds.GetFeeds(b).Single().LastError);
    }

    [TestMethod]
    public async Task Subscribe_ShowsExistingPosts_AllUnread()
    {
        using var db = new TestDatabase();
        var alice = db.AddUser("alice");
        var bob = db.AddUser("bob");
        const string url = "https://example.com/feed";
        db.Subscribe(alice, url);
        Assert.AreEqual(3, await db.AddPosts(url, 3));

        var aliceTimeline = await db.Timeline(alice);
        Assert.AreEqual(3, aliceTimeline.Count);
        Assert.IsTrue(aliceTimeline.All(i => i.UserId == alice.Id && i.FeedUrl == url && !i.IsRead));

        // Bob subscribes later: no fetch happened, the stored posts are visible at once.
        db.Subscribe(bob, url);
        var bobTimeline = await db.Timeline(bob);
        Assert.AreEqual(3, bobTimeline.Count);
        Assert.IsTrue(bobTimeline.All(i => i.UserId == bob.Id && !i.IsRead && !i.IsSaved));
        CollectionAssert.AreEqual(aliceTimeline.Select(i => i.Id).ToList(), bobTimeline.Select(i => i.Id).ToList(), "the same shared post ids");

        Assert.AreEqual(1, db.Scalar("SELECT count(*) FROM Feeds"), "one feed row per URL");
        Assert.AreEqual(3, db.Scalar("SELECT count(*) FROM Posts"), "posts stored once");
    }

    [TestMethod]
    public async Task Unsubscribe_LeavesPosts_AndOtherSubscribers()
    {
        using var db = new TestDatabase();
        var alice = db.AddUser("alice");
        var bob = db.AddUser("bob");
        const string url = "https://example.com/feed";
        db.Subscribe(alice, url);
        db.Subscribe(bob, url);
        await db.AddPosts(url, 4);

        db.Feeds.DeleteFeed(alice, url);

        Assert.AreEqual(0, (await db.Timeline(alice)).Count, "unsubscribed user no longer sees the feed");
        Assert.IsNull(db.Feeds.GetFeed(alice, url));
        Assert.AreEqual(4, (await db.Timeline(bob)).Count, "the other subscriber is unaffected");
        Assert.AreEqual(4, db.Scalar("SELECT count(*) FROM Posts"), "posts stay");
        Assert.AreEqual(1, db.Scalar("SELECT count(*) FROM Feeds"), "the feed row stays");
        Assert.AreEqual(0, db.Items.GetItemCountForFeed(alice, url));
    }

    [TestMethod]
    public async Task SavedView_KeepsPosts_AfterUnsubscribeAndClear()
    {
        using var db = new TestDatabase();
        var alice = db.AddUser("alice");
        const string url = "https://example.com/feed";
        db.Subscribe(alice, url);
        await db.AddPosts(url, 3);
        var saved = (await db.Timeline(alice))[1];
        db.Items.SavePost(saved, alice);

        await db.Items.DeleteAllItemsAsync(alice);
        Assert.AreEqual(0, (await db.Timeline(alice)).Count, "cleared");
        var afterClear = await db.Timeline(alice, saved: true, hideHidden: false);
        Assert.AreEqual(1, afterClear.Count, "the saved post outlives clear posts");

        db.Feeds.DeleteFeed(alice, url);
        var afterUnsubscribe = await db.Timeline(alice, saved: true, hideHidden: false);
        Assert.AreEqual(1, afterUnsubscribe.Count, "and the unsubscribe");
        Assert.AreEqual(saved.Id, afterUnsubscribe[0].Id);
        Assert.IsTrue(afterUnsubscribe[0].IsSaved);
        Assert.AreEqual(1, await db.Items.GetNewItemCountAsync(new NewsFeed("%", alice.Id), false, true, null, 0, 0));
        Assert.AreEqual("<p>post 1</p>", db.Items.GetItemContent(afterUnsubscribe[0]), "content stays reachable");

        db.Items.UnsavePost(afterUnsubscribe[0], alice);
        Assert.AreEqual(0, (await db.Timeline(alice, saved: true, hideHidden: false)).Count);
    }

    [TestMethod]
    public async Task ReadAndSaved_AreIsolatedBetweenUsers()
    {
        using var db = new TestDatabase();
        var alice = db.AddUser("alice");
        var bob = db.AddUser("bob");
        const string url = "https://example.com/feed";
        db.Subscribe(alice, url);
        db.Subscribe(bob, url);
        await db.AddPosts(url, 3);

        var post = (await db.Timeline(alice)).First();
        db.Items.MarkAsRead(post, true, alice);
        db.Items.SavePost(post, alice);

        var aliceView = (await db.Timeline(alice)).First(i => i.Id == post.Id);
        var bobView = (await db.Timeline(bob)).First(i => i.Id == post.Id);
        Assert.IsTrue(aliceView.IsRead && aliceView.IsSaved);
        Assert.IsFalse(bobView.IsRead || bobView.IsSaved);

        Assert.AreEqual(2, (await db.Timeline(alice, unread: true)).Count);
        Assert.AreEqual(3, (await db.Timeline(bob, unread: true)).Count);
        Assert.AreEqual(1, (await db.Timeline(alice, saved: true)).Count);
        Assert.AreEqual(0, (await db.Timeline(bob, saved: true)).Count);

        // Sparse state: clearing both flags removes the row.
        db.Items.MarkAsRead(post, false, alice);
        Assert.AreEqual(1, db.Scalar("SELECT count(*) FROM UserPostState"), "still saved");
        db.Items.UnsavePost(post, alice);
        Assert.AreEqual(0, db.Scalar("SELECT count(*) FROM UserPostState"), "unread + unsaved = no row");

        // Idempotent: a replayed mark-read leaves the row as it is.
        db.Items.MarkAsRead(post, true, alice);
        db.Items.MarkAsRead(post, true, alice);
        Assert.AreEqual(1, db.Scalar("SELECT count(*) FROM UserPostState WHERE IsRead = 1"));
        Assert.IsTrue(db.Items.GetItem(alice, int.Parse(post.Id)).IsRead);
        Assert.IsFalse(db.Items.GetItem(bob, int.Parse(post.Id)).IsRead);
    }

    [TestMethod]
    public async Task HiddenTag_ExcludesFeed_ForOneUserOnly()
    {
        using var db = new TestDatabase();
        var alice = db.AddUser("alice");
        var bob = db.AddUser("bob");
        const string noisy = "https://example.com/noisy";
        const string quiet = "https://example.com/quiet";
        db.Subscribe(alice, noisy, "memes");
        db.Subscribe(alice, quiet);
        db.Subscribe(bob, noisy, "memes");
        await db.AddPosts(noisy, 2, 1000, "noisy");
        await db.AddPosts(quiet, 2, 2000, "quiet");

        db.Feeds.SetTagHidden(alice, "memes", true);

        var aliceTimeline = await db.Timeline(alice);
        Assert.IsTrue(aliceTimeline.All(i => i.FeedUrl == quiet), "alice's hidden tag removes the noisy feed");
        Assert.AreEqual(2, (await db.Timeline(bob)).Count, "bob's identical tag is not hidden");
        CollectionAssert.AreEqual(new[] { noisy }, db.Feeds.GetHiddenFeedUrls(alice).ToList());
        Assert.AreEqual(0, db.Feeds.GetHiddenFeedUrls(bob).Count());

        // Explicit tag filter and the saved view still show the hidden feed.
        Assert.AreEqual(2, (await db.Timeline(alice, tag: "memes", hideHidden: false)).Count);
        var settings = db.Feeds.GetTagSettings(alice).ToList();
        Assert.AreEqual(1, settings.Count);
        Assert.IsTrue(settings[0].IsHidden);
        Assert.IsFalse(db.Feeds.GetTagSettings(bob).Single().IsHidden);

        // Tags ride on the item view.
        Assert.IsTrue((await db.Timeline(bob)).All(i => i.FeedTags.Contains("memes")));

        // The new-count probe applies the same exclusion.
        Assert.AreEqual(2, await db.Items.GetNewItemCountAsync(new NewsFeed("%", alice.Id), false, false, null, 0, 0, db.Feeds.GetHiddenFeedUrls(alice)));
        Assert.AreEqual(2, await db.Items.GetNewItemCountAsync(new NewsFeed("%", bob.Id), false, false, null, 0, 0, db.Feeds.GetHiddenFeedUrls(bob)));
    }

    [TestMethod]
    public async Task Watermark_HidesPosts_AndIsReversible()
    {
        using var db = new TestDatabase();
        var alice = db.AddUser("alice");
        var bob = db.AddUser("bob");
        const string url = "https://example.com/feed";
        db.Subscribe(alice, url);
        db.Subscribe(bob, url);
        await db.AddPosts(url, 3, 1000);

        await db.Items.DeleteAllItemsAsync(alice);

        Assert.AreEqual(0, (await db.Timeline(alice)).Count, "cleared");
        Assert.AreEqual(0, db.Items.GetItemCountForFeed(alice, url));
        Assert.AreEqual(3, (await db.Timeline(bob)).Count, "bob is unaffected");
        Assert.AreEqual(3, db.Scalar("SELECT count(*) FROM Posts"), "nothing was deleted");

        // Newer posts show up again, older ones stay hidden.
        await db.AddPosts(url, 1, 5000, "later");
        var after = await db.Timeline(alice);
        Assert.AreEqual(1, after.Count);
        Assert.AreEqual("later 0", after[0].Title);

        // Reversible: clear the watermark and everything is back.
        db.Execute("UPDATE Subscriptions SET HideBeforeOrder = NULL");
        Assert.AreEqual(4, (await db.Timeline(alice)).Count);
    }

    [TestMethod]
    public async Task Timeline_CursorPages_MatchReferenceOrdering_OnSeededCorpus()
    {
        using var db = new TestDatabase();
        var alice = db.AddUser("alice");
        var urls = new[] { "https://a.example.com/f", "https://b.example.com/f", "https://c.example.com/f" };
        foreach (var url in urls) db.Subscribe(alice, url);

        // A corpus with ties across feeds and interleaved orders.
        var rng = new Random(42);
        foreach (var url in urls)
        {
            var items = Enumerable.Range(0, 40).Select(i => new NewsFeedItem(
                "0", 0, $"{url} {i}", $"{url}/p{i}", null, "2025-01-01", "<p/>", null)
            { FeedUrl = url, PublishDateOrder = 1000 + rng.Next(0, 30) });
            await db.Items.AddItemsAsync(items);
        }

        // Reference: the old implementation's ordering, PublishDateOrder DESC then Id DESC.
        var reference = new List<(long Order, long Id)>();
        using (var c = db.Connections.OpenRead())
        {
            var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT PublishDateOrder, Id FROM Posts";
            using var r = cmd.ExecuteReader();
            while (r.Read()) reference.Add((r.GetInt64(0), r.GetInt64(1)));
        }
        var expected = reference.OrderByDescending(p => p.Order).ThenByDescending(p => p.Id).Select(p => p.Id.ToString()).ToList();

        var actual = new List<string>();
        long? cursorOrder = null, cursorId = null;
        while (true)
        {
            var page = (await db.Items.GetItemsAsync(new NewsFeed("%", alice.Id), false, false, null, 0, 7,
                lastId: cursorId, lastPublishDateOrder: cursorOrder, excludeFeedUrls: Array.Empty<string>())).ToList();
            if (page.Count == 0) break;
            actual.AddRange(page.Select(i => i.Id));
            cursorOrder = page.Last().PublishDateOrder;
            cursorId = long.Parse(page.Last().Id);
        }

        Assert.AreEqual(120, actual.Count, "every post appears exactly once across the cursor pages");
        CollectionAssert.AreEqual(expected, actual, "cursor pagination follows (PublishDateOrder DESC, Id DESC) with no gaps or overlaps");

        // The new-count probe agrees with the page boundary.
        var mid = actual[50];
        var midItem = db.Items.GetItem(alice, int.Parse(mid));
        var newer = await db.Items.GetNewItemCountAsync(new NewsFeed("%", alice.Id), false, false, null, midItem.PublishDateOrder, long.Parse(mid), Array.Empty<string>());
        Assert.AreEqual(50, newer);
    }

    [TestMethod]
    public async Task Search_RespectsSubscriptions()
    {
        using var db = new TestDatabase();
        var alice = db.AddUser("alice");
        var bob = db.AddUser("bob");
        const string aliceFeed = "https://a.example.com/f";
        const string bobFeed = "https://b.example.com/f";
        db.Subscribe(alice, aliceFeed);
        db.Subscribe(bob, bobFeed);
        await db.Items.AddItemsAsync(new[]
        {
            new NewsFeedItem("0", 0, "Wildlife in Alaska", $"{aliceFeed}/1", null, "2025-01-01", "<p>Bears and wildlife</p>", null) { FeedUrl = aliceFeed, PublishDateOrder = 1 },
            new NewsFeedItem("0", 0, "Wildlife in Kenya", $"{bobFeed}/1", null, "2025-01-01", "<p>Lions and wildlife</p>", null) { FeedUrl = bobFeed, PublishDateOrder = 2 },
        });

        var aliceHits = (await db.Items.SearchItemsAsync("wildlife", alice, 0, 20)).ToList();
        var bobHits = (await db.Items.SearchItemsAsync("wildlife", bob, 0, 20)).ToList();

        Assert.AreEqual(1, aliceHits.Count);
        Assert.AreEqual("Wildlife in Alaska", aliceHits[0].Title);
        Assert.AreEqual("<p>Bears and wildlife</p>", aliceHits[0].Content, "search results carry content");
        Assert.AreEqual(1, bobHits.Count);
        Assert.AreEqual("Wildlife in Kenya", bobHits[0].Title);

        // Partial words fall back to the Title LIKE path.
        Assert.AreEqual(1, (await db.Items.SearchItemsAsync("Alask", alice, 0, 20)).Count());
    }

    [TestMethod]
    public async Task DeleteUser_CascadesEverythingPerUser_AndLeavesSharedPosts()
    {
        using var db = new TestDatabase();
        var alice = db.AddUser("alice");
        var bob = db.AddUser("bob");
        const string url = "https://example.com/feed";
        db.Subscribe(alice, url, "tech");
        db.Subscribe(bob, url);
        await db.AddPosts(url, 2);
        db.Users.LinkIdentity(alice.Id, "github", "1");
        db.Items.SavePost((await db.Timeline(alice)).First(), alice);

        db.Users.DeleteUser(alice.Id);

        Assert.IsNull(db.Users.GetUserById(alice.Id));
        Assert.AreEqual(0, db.Scalar($"SELECT count(*) FROM Subscriptions WHERE UserId = {alice.Id}"));
        Assert.AreEqual(0, db.Scalar($"SELECT count(*) FROM Tags WHERE UserId = {alice.Id}"));
        Assert.AreEqual(0, db.Scalar("SELECT count(*) FROM SubscriptionTags"));
        Assert.AreEqual(0, db.Scalar("SELECT count(*) FROM UserPostState"));
        Assert.AreEqual(0, db.Scalar("SELECT count(*) FROM UserIdentities"));
        Assert.AreEqual(2, db.Scalar("SELECT count(*) FROM Posts"), "shared posts untouched");
        Assert.AreEqual(2, (await db.Timeline(bob)).Count);
    }

    [TestMethod]
    public void Identities_RoundTrip_AndNeverMove()
    {
        using var db = new TestDatabase();
        var alice = db.AddUser("alice");
        var bob = db.AddUser("bob");
        db.Users.LinkIdentity(alice.Id, "microsoft", "obj-1");
        db.Users.LinkIdentity(alice.Id, "email-link", "abc");
        db.Users.LinkIdentity(bob.Id, "microsoft", "obj-1"); // already alice's: ignored

        Assert.AreEqual(alice.Id, db.Users.FindByIdentity("microsoft", "obj-1").Id);
        Assert.IsNull(db.Users.FindByIdentity("microsoft", "nobody"));
        var reloaded = db.Users.GetUserById(alice.Id);
        CollectionAssert.AreEqual(new[] { "microsoft:obj-1", "microsoft-email:abc" }, reloaded.Identities.ToList());
        Assert.AreEqual("obj-1", reloaded.AadUserId);
        Assert.AreEqual("microsoft:obj-1", reloaded.ProviderUserId);
        Assert.AreEqual(0, db.Users.GetUserById(bob.Id).Identities.Count);
    }

    [TestMethod]
    public void Opml_RoundTrips_SubscriptionsWithTags()
    {
        using var db = new TestDatabase();
        var alice = db.AddUser("alice");
        db.Subscribe(alice, "https://a.example.com/feed", "tech", "news");
        db.Subscribe(alice, "https://b.example.com/feed");
        db.Subscribe(alice, "https://c.example.com/feed", "sports");

        var opml = OpmlSerializer.GenerateOpmlContent(db.Feeds.GetFeeds(alice));

        var bob = db.AddUser("bob");
        db.Feeds.AddFeeds(bob, OpmlSerializer.ParseOpmlContent(opml, bob.Id));

        var imported = db.Feeds.GetFeeds(bob).OrderBy(f => f.Href).ToList();
        Assert.AreEqual(3, imported.Count);
        CollectionAssert.AreEquivalent(new[] { "tech", "news" }, imported[0].Tags.ToList());
        Assert.AreEqual(0, imported[1].Tags.Count);
        CollectionAssert.AreEquivalent(new[] { "sports" }, imported[2].Tags.ToList());
        Assert.AreEqual(3, db.Scalar("SELECT count(*) FROM Feeds"), "import reuses the shared feed rows");

        // Importing again is a no-op.
        db.Feeds.AddFeeds(bob, OpmlSerializer.ParseOpmlContent(opml, bob.Id));
        Assert.AreEqual(3, db.Feeds.GetFeeds(bob).Count());
        Assert.AreEqual(3, db.Scalar($"SELECT count(*) FROM SubscriptionTags WHERE UserId = {bob.Id}"));
    }

    [TestMethod]
    public async Task AddItems_IgnoresDuplicates_AndUnknownFeeds()
    {
        using var db = new TestDatabase();
        var alice = db.AddUser("alice");
        const string url = "https://example.com/feed";
        db.Subscribe(alice, url);

        Assert.AreEqual(2, await db.AddPosts(url, 2));
        Assert.AreEqual(0, await db.AddPosts(url, 2), "same hrefs again: nothing inserted");
        Assert.AreEqual(0, await db.AddPosts("https://nobody.example.com/feed", 2), "no feed row, nothing to attach to");
        Assert.AreEqual(2, db.Scalar("SELECT count(*) FROM Posts"));
        Assert.AreEqual(2, db.Scalar("SELECT count(*) FROM PostContent"));
        Assert.AreEqual(2, db.Scalar("SELECT count(*) FROM Posts_fts"));

        var item = db.Items.GetItem(alice, $"{url}/post-0");
        Assert.AreEqual("<p>post 0</p>", db.Items.GetItemContent(item));
    }
}
