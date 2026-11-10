namespace RssReaderTests;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RssApp.Contracts;
using RssApp.Data;

/// <summary>
/// A feed that renames a post (The Register fixing a typo in a headline, which
/// also changes the headline slug in its URL) must update the stored post, not
/// add a second one. The link is the primary key and the guid only a fallback,
/// so rows stored before guids were kept never duplicate.
/// </summary>
[TestClass]
public sealed class PostRetitleTests
{
    private const string FeedUrl = "https://feeds.example.org/register";
    private const long Published = 639_000_000_000_000_000;

    private static (SQLiteItemRepository repo, RssUser user, string db) Setup(string name)
    {
        var db = $"retitle_{name}.db";
        if (File.Exists(db)) File.Delete(db);

        var connections = new SqliteDbConnections(db);
        var users = new SQLiteUserRepository(connections, NullLogger<SQLiteUserRepository>.Instance);
        users.AddUser("reader", 0);
        var feeds = new SQLiteFeedRepository(connections, NullLogger<SQLiteFeedRepository>.Instance);
        feeds.AddFeed(new NewsFeed(1, FeedUrl, 0));
        var repo = new SQLiteItemRepository(connections, NullLogger<SQLiteItemRepository>.Instance, feeds, users);
        return (repo, new RssUser("reader", 0), db);
    }

    private static NewsFeedItem Item(string guid, string title, string href, long published = Published, string content = "<p>body</p>")
        => new(guid, 0, title, href, null, "2026-11-05", content, null)
        {
            FeedUrl = FeedUrl,
            PublishDateOrder = published,
        };

    private static long Scalar(string db, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={db}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? 0 : Convert.ToInt64(result);
    }

    private static async Task<List<NewsFeedItem>> Timeline(SQLiteItemRepository repo, RssUser user)
        => (await repo.GetItemsAsync(new NewsFeed("%", user.Id), false, false, null, 0, 50)).ToList();

    [TestMethod]
    public async Task RenamedPostWithNewSlug_UpdatesInPlace_AndKeepsReadState()
    {
        var (repo, user, db) = Setup("slug");
        await repo.UpsertItemsAsync([Item("https://www.theregister.com/a/1", "Safari's Autofill feature may be distoring time", "https://www.theregister.com/2026/11/05/safari-autofill-distoring-time/1")]);
        var stored = repo.GetItem(user, "https://www.theregister.com/2026/11/05/safari-autofill-distoring-time/1");
        repo.MarkAsRead(stored, true, user);

        var result = await repo.UpsertItemsAsync([Item("https://www.theregister.com/a/1", "Safari's Autofill feature may be distorting time", "https://www.theregister.com/2026/11/05/safari-autofill-distorting-time/1", content: "<p>fixed</p>")]);

        Assert.AreEqual(new ItemWriteResult(0, 1), result);
        var posts = await Timeline(repo, user);
        Assert.AreEqual(1, posts.Count);
        Assert.AreEqual(stored.Id, posts[0].Id, "the post keeps its row");
        Assert.AreEqual("Safari's Autofill feature may be distorting time", posts[0].Title);
        Assert.AreEqual("Safari's Autofill feature may be distoring time", posts[0].FormerTitle);
        Assert.AreEqual("https://www.theregister.com/2026/11/05/safari-autofill-distorting-time/1", posts[0].Href);
        Assert.IsTrue(posts[0].IsRead, "read state survives the rename");
        Assert.AreEqual("<p>fixed</p>", posts[0].Content);
    }

    [TestMethod]
    public async Task RowsStoredWithoutGuid_MatchByLink_NoDuplicatesAndNoBackfill()
    {
        var (repo, user, db) = Setup("legacy");
        var items = Enumerable.Range(0, 20)
            .Select(i => Item($"https://www.theregister.com/a/{i}", $"Story {i}", $"https://www.theregister.com/2026/11/05/story-{i}/{i}", Published + i))
            .ToList();
        await repo.UpsertItemsAsync(items);

        // As if stored before this change: no guids at all.
        using (var connection = new SqliteConnection($"Data Source={db}"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE Posts SET Guid = NULL";
            cmd.ExecuteNonQuery();
        }

        var result = await repo.UpsertItemsAsync(items);

        Assert.AreEqual(new ItemWriteResult(0, 0), result);
        Assert.AreEqual(20, Scalar(db, "SELECT count(*) FROM Posts"));
        Assert.AreEqual(0, Scalar(db, "SELECT count(*) FROM Posts WHERE Guid IS NOT NULL"), "a refresh writes nothing to rows that did not change");
    }

    [TestMethod]
    public async Task TitleChangeOnSameLink_IsRecorded_FirstTitleKept_RevertClears()
    {
        var (repo, user, _) = Setup("samelink");
        const string href = "https://example.com/hospital";
        await repo.UpsertItemsAsync([Item("g1", "Hospital gave very sick doctor a special PC for his very personal use", href)]);

        Assert.AreEqual(new ItemWriteResult(0, 1), await repo.UpsertItemsAsync([Item("g1", "Hospital gave very sick doctor a special PC for his personal, erm, pleasure", href)]));
        Assert.AreEqual(new ItemWriteResult(0, 1), await repo.UpsertItemsAsync([Item("g1", "Hospital gave sick doctor a PC", href)]));
        var post = (await Timeline(repo, user)).Single();
        Assert.AreEqual("Hospital gave sick doctor a PC", post.Title);
        Assert.AreEqual("Hospital gave very sick doctor a special PC for his very personal use", post.FormerTitle, "FormerTitle is the title readers first saw");

        await repo.UpsertItemsAsync([Item("g1", "Hospital gave very sick doctor a special PC for his very personal use", href)]);
        post = (await Timeline(repo, user)).Single();
        Assert.IsNull(post.FormerTitle, "renamed back to the first title: nothing to show");

        Assert.AreEqual(new ItemWriteResult(0, 0), await repo.UpsertItemsAsync([Item("g1", post.Title, href)]), "an unchanged post is not an update");
    }

    [TestMethod]
    public async Task ReusedGuidWithDifferentDate_IsANewPost()
    {
        // Feeds that number items by position hand the same guid to a different
        // article later; folding it into the old post would lose that post.
        var (repo, user, _) = Setup("reused");
        await repo.UpsertItemsAsync([Item("1", "Monday's top story", "https://example.com/monday", Published)]);
        var result = await repo.UpsertItemsAsync([Item("1", "Tuesday's top story", "https://example.com/tuesday", Published + TimeSpan.TicksPerDay)]);

        Assert.AreEqual(new ItemWriteResult(1, 0), result);
        Assert.AreEqual(2, (await Timeline(repo, user)).Count);
    }

    [TestMethod]
    public async Task GuidRepeatedWithinAFetch_IsIgnored()
    {
        var (repo, user, _) = Setup("repeated");
        await repo.UpsertItemsAsync([Item("same", "A", "https://example.com/a"), Item("same", "B", "https://example.com/b")]);
        var result = await repo.UpsertItemsAsync([Item("same", "A2", "https://example.com/a2"), Item("same", "B2", "https://example.com/b2")]);

        Assert.AreEqual(new ItemWriteResult(2, 0), result);
        Assert.AreEqual(4, (await Timeline(repo, user)).Count);
    }

    [TestMethod]
    public async Task GuidEqualToFeedUrl_IsIgnored()
    {
        var (repo, user, _) = Setup("feedurl");
        await repo.UpsertItemsAsync([Item(FeedUrl, "A", "https://example.com/a")]);
        var result = await repo.UpsertItemsAsync([Item(FeedUrl, "B", "https://example.com/b")]);

        Assert.AreEqual(new ItemWriteResult(1, 0), result);
    }

    [TestMethod]
    public async Task Search_FindsTheNewTitle()
    {
        var (repo, user, _) = Setup("search");
        await repo.UpsertItemsAsync([Item("g", "Distoring time", "https://example.com/distoring", content: "<p>x</p>")]);
        await repo.UpsertItemsAsync([Item("g", "Distorting time", "https://example.com/distorting", content: "<p>x</p>")]);

        var hits = (await repo.SearchItemsAsync("Distorting", user, 0, 20)).ToList();
        Assert.AreEqual(1, hits.Count);
        Assert.AreEqual("Distorting time", hits[0].Title);
    }

    [TestMethod]
    public void ExistingDatabase_GainsTheNewColumns()
    {
        var (_, _, db) = Setup("upgrade");
        using (var connection = new SqliteConnection($"Data Source={db}"))
        {
            connection.Open();
            DatabaseSchema.Execute(connection, "DROP INDEX IX_Posts_FeedGuid; ALTER TABLE Posts DROP COLUMN Guid; ALTER TABLE Posts DROP COLUMN FormerTitle;");
            Assert.IsFalse(DatabaseSchema.ColumnExists(connection, "Posts", "Guid"));

            DatabaseSchema.EnsureCreated(connection);

            Assert.IsTrue(DatabaseSchema.ColumnExists(connection, "Posts", "Guid"));
            Assert.IsTrue(DatabaseSchema.ColumnExists(connection, "Posts", "FormerTitle"));
        }

        Assert.AreEqual(1, Scalar(db, "SELECT count(*) FROM sqlite_master WHERE type='index' AND name='IX_Posts_FeedGuid'"));
    }
}
