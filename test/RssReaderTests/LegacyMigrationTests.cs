using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RssApp.Contracts;
using RssApp.Data;

namespace RssReaderTests;

/// <summary>
/// The one-shot migration (DB-REDESIGN-SPEC.md section 7) against a database
/// built with the pre-redesign schema and seeded the way production was:
/// posts duplicated per subscriber, tags as comma strings, validators and
/// schedule in side tables, identities in Users columns.
/// </summary>
[TestClass]
public sealed class LegacyMigrationTests
{
    // The old schema, verbatim from the previous repositories.
    private const string LegacySchema = """
        CREATE TABLE Users (Id INTEGER PRIMARY KEY AUTOINCREMENT, Username TEXT NOT NULL UNIQUE, AadUserId TEXT, ProviderUserId TEXT);
        CREATE UNIQUE INDEX IX_Users_ProviderUserId ON Users(ProviderUserId) WHERE ProviderUserId IS NOT NULL;
        CREATE UNIQUE INDEX IX_Users_AadUserId ON Users(AadUserId) WHERE AadUserId IS NOT NULL;
        CREATE TABLE Feeds (Id INTEGER PRIMARY KEY AUTOINCREMENT, Url TEXT NOT NULL, IsPaywalled BOOLEAN NOT NULL DEFAULT 0, UserId INTEGER NOT NULL, Tags TEXT DEFAULT NULL, UNIQUE (Url, UserId), FOREIGN KEY (UserId) REFERENCES Users(Id));
        CREATE TABLE UserTagSettings (UserId INTEGER NOT NULL, Tag TEXT NOT NULL, IsHidden BOOLEAN NOT NULL DEFAULT 0, PRIMARY KEY (UserId, Tag), FOREIGN KEY (UserId) REFERENCES Users(Id));
        CREATE TABLE Items (Id INTEGER PRIMARY KEY AUTOINCREMENT, FeedUrl TEXT NOT NULL, Href TEXT, CommentsHref TEXT, Title TEXT, PublishDateOrder INTEGER NOT NULL DEFAULT 0, PublishDate TEXT, Content TEXT, IsRead BOOLEAN DEFAULT 0, UserId INTEGER NOT NULL, ThumbnailUrl TEXT, IsSaved BOOLEAN DEFAULT 0, Tags TEXT DEFAULT '', FOREIGN KEY (UserId) REFERENCES Users(Id), UNIQUE(FeedUrl, UserId, Href));
        CREATE TABLE ItemContent (Id INTEGER PRIMARY KEY AUTOINCREMENT, FeedUrl TEXT NOT NULL, Href TEXT, Title TEXT, PublishDateOrder INTEGER NOT NULL DEFAULT 0, PublishDate TEXT, Content TEXT, UserId INTEGER NOT NULL, UNIQUE (FeedUrl, UserId, Href), FOREIGN KEY (UserId) REFERENCES Users(Id));
        CREATE VIRTUAL TABLE Items_fts USING fts5(Title, Content, Href, FeedUrl, UserId UNINDEXED, content='ItemContent', content_rowid='Id');
        CREATE TRIGGER Items_after_insert AFTER INSERT ON ItemContent BEGIN INSERT INTO Items_fts (rowid, Title, Content, Href, FeedUrl, UserId) VALUES (new.Id, new.Title, new.Content, new.Href, new.FeedUrl, new.UserId); END;
        CREATE TRIGGER Items_au AFTER UPDATE ON ItemContent BEGIN INSERT INTO Items_fts(Items_fts, rowid, Title, Content, Href, FeedUrl, UserId) VALUES('delete', old.Id, old.Title, old.Content, old.Href, old.FeedUrl, old.UserId); INSERT INTO Items_fts(rowid, Title, Content, Href, FeedUrl, UserId) VALUES (new.Id, new.Title, new.Content, new.Href, new.FeedUrl, new.UserId); END;
        CREATE TRIGGER Items_after_delete AFTER DELETE ON ItemContent BEGIN DELETE FROM Items_fts WHERE rowid = old.Id; END;
        CREATE INDEX idx_items_timeline ON Items(UserId, PublishDateOrder DESC, Id DESC);
        CREATE TABLE FeedValidators (Url TEXT PRIMARY KEY, ETag TEXT DEFAULT NULL, LastModified TEXT DEFAULT NULL, UpdatedUtc TEXT NOT NULL);
        CREATE TABLE FeedFetchState (Url TEXT PRIMARY KEY, LastFetchedUtc TEXT, NextEarliestFetchUtc TEXT, IntervalSeconds INTEGER);
        CREATE TABLE SystemStats (Id INTEGER PRIMARY KEY AUTOINCREMENT, Timestamp TEXT NOT NULL, UserCount INT NOT NULL DEFAULT 0, FeedCount INT NOT NULL DEFAULT 0, ItemCount INT NOT NULL DEFAULT 0, DbSizeBytes INT NOT NULL DEFAULT 0);
        """;

    /// <summary>Drop order for the hand clean-up: children before parents.</summary>
    public static readonly string[] LegacyDropOrder =
        { "Items_fts", "ItemContent", "Items", "UserTagSettings", "FeedValidators", "FeedFetchState", "Feeds", "Users" };

    private const string Shared = "https://shared.example.com/rss";
    private const string AliceOnly = "https://alice.example.com/rss";
    private const string Orphan = "https://orphan.example.com/rss";

    private string path;

    [TestInitialize]
    public void Setup() => path = $"legacy-{Guid.NewGuid():N}.db";

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(path + suffix); } catch { }
        }
    }

    private SqliteConnection OpenRaw()
    {
        var c = new SqliteConnection($"Data Source={path}");
        c.Open();
        return c;
    }

    private static void Run(SqliteConnection c, string sql, params (string, object)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static long Count(SqliteConnection c, string sql) => DatabaseSchema.Scalar(c, sql);

    /// <summary>
    /// alice (id 1): legacy SWA id + linked microsoft:obj; subscribes shared (tags
    /// "tech,news", news hidden) and alice-only; read 3 of shared, saved 1.
    /// bob (id 2): github id in AadUserId and an email-hash link; subscribes shared
    /// (tag "tech"); saved a post alice also saved, read a different one.
    /// An orphan feed URL has items for alice but no Feeds row (deleted feed).
    /// One shared item exists only for bob (added after alice's fetch), and one
    /// alice item lacks an ItemContent row (content only on Items).
    /// </summary>
    private void SeedLegacy()
    {
        using var c = OpenRaw();
        Run(c, LegacySchema);
        Run(c, "INSERT INTO Users (Id, Username, AadUserId, ProviderUserId) VALUES (1, 'alice', '024d97a14bc74c58b9a83e88b919a92c', 'microsoft:obj-1')");
        Run(c, "INSERT INTO Users (Id, Username, AadUserId, ProviderUserId) VALUES (2, 'bob', 'github:4242', 'microsoft-email:abc123')");
        Run(c, "INSERT INTO Users (Id, Username) VALUES (3, 'carol')"); // no identities, no feeds
        Run(c, "INSERT INTO Feeds (Url, UserId, Tags) VALUES (@u, 1, 'tech,news')", ("@u", Shared));
        Run(c, "INSERT INTO Feeds (Url, UserId, Tags) VALUES (@u, 1, NULL)", ("@u", AliceOnly));
        Run(c, "INSERT INTO Feeds (Url, UserId, Tags) VALUES (@u, 2, 'tech')", ("@u", Shared));
        Run(c, "INSERT INTO UserTagSettings (UserId, Tag, IsHidden) VALUES (1, 'news', 1)");
        Run(c, "INSERT INTO FeedValidators (Url, ETag, LastModified, UpdatedUtc) VALUES (@u, '\"etag-1\"', '2026-01-01T00:00:00.0000000+00:00', 'x')", ("@u", Shared));
        Run(c, "INSERT INTO FeedFetchState (Url, LastFetchedUtc, NextEarliestFetchUtc, IntervalSeconds) VALUES (@u, '2026-10-20T10:00:00.0000000+00:00', '2026-10-20T11:00:00.0000000+00:00', 3600)", ("@u", Shared));

        void Item(string feed, int user, int n, long order, bool read, bool saved, bool content = true)
        {
            var href = $"{feed}/p{n}";
            Run(c, """
                INSERT INTO Items (FeedUrl, Href, Title, PublishDateOrder, PublishDate, Content, IsRead, UserId, ThumbnailUrl, IsSaved, Tags)
                VALUES (@f, @h, @t, @o, '2025-01-01', @c, @r, @u, 'https://img/' || @t, @s, '')
                """, ("@f", feed), ("@h", href), ("@t", $"{feed} {n}"), ("@o", order), ("@c", $"<p>items {n}</p>"), ("@r", read ? 1 : 0), ("@u", user), ("@s", saved ? 1 : 0));
            if (content)
            {
                Run(c, "INSERT INTO ItemContent (FeedUrl, Href, Title, PublishDateOrder, PublishDate, Content, UserId) VALUES (@f, @h, @t, @o, '2025-01-01', @c, @u)",
                    ("@f", feed), ("@h", href), ("@t", $"{feed} {n}"), ("@o", order), ("@c", $"<p>content {n}</p>"), ("@u", user));
            }
        }

        // Shared feed: 10 posts for alice (p0..p9), the same 10 plus p10 for bob.
        for (var n = 0; n < 10; n++) Item(Shared, 1, n, 1000 + n, read: n < 3, saved: n == 5, content: n != 7);
        for (var n = 0; n <= 10; n++) Item(Shared, 2, n, 1000 + n, read: n == 9, saved: n == 5);
        // Alice-only feed: 4 posts.
        for (var n = 0; n < 4; n++) Item(AliceOnly, 1, n, 2000 + n, read: false, saved: false);
        // Orphan feed (no Feeds row): 2 posts for alice, one saved.
        for (var n = 0; n < 2; n++) Item(Orphan, 1, n, 3000 + n, read: true, saved: n == 1);
    }

    /// <summary>Old timeline for a user: Items by (PublishDateOrder DESC, Id DESC), DistinctBy(Href), excluding feeds with a hidden tag.</summary>
    private List<string> LegacyTimelineHrefs(int userId, IEnumerable<string> excludeFeedUrls, int take)
    {
        using var c = OpenRaw();
        var hrefs = new List<string>();
        using var cmd = c.CreateCommand();
        var excluded = excludeFeedUrls.Select(u => $"'{u}'").DefaultIfEmpty("''");
        cmd.CommandText = $"SELECT Href FROM Legacy_Items WHERE UserId = {userId} AND FeedUrl NOT IN ({string.Join(",", excluded)}) ORDER BY PublishDateOrder DESC, Id DESC";
        using var r = cmd.ExecuteReader();
        while (r.Read()) hrefs.Add(r.GetString(0));
        return hrefs.Distinct().Take(take).ToList();
    }

    [TestMethod]
    public async Task Migration_MovesEverything_Deterministically_AndRecordsItself()
    {
        SeedLegacy();
        var connections = new SqliteDbConnections(path);

        var state = LegacyMigration.RunIfNeeded(connections, NullLogger.Instance);

        Assert.AreEqual("migrated", state.Status);
        Assert.IsNotNull(state.DurationMs);
        StringAssert.Contains(state.Notes, "posts=17");

        using (var c = OpenRaw())
        {
            // Superseded tables renamed, nothing dropped.
            foreach (var t in new[] { "Items", "ItemContent", "Items_fts", "FeedValidators", "FeedFetchState", "UserTagSettings", "Users", "Feeds" })
            {
                Assert.IsTrue(DatabaseSchema.TableExists(c, "Legacy_" + t), $"Legacy_{t} should exist");
            }
            Assert.AreEqual(27, Count(c, "SELECT count(*) FROM Legacy_Items"), "legacy rows untouched");
            Assert.IsTrue(DatabaseSchema.TableExists(c, "SystemStats"));

            // Users and identities.
            Assert.AreEqual(3, Count(c, "SELECT count(*) FROM Users"));
            Assert.AreEqual(1, Count(c, "SELECT UserId FROM UserIdentities WHERE Provider='microsoft' AND Subject='024d97a14bc74c58b9a83e88b919a92c'"));
            Assert.AreEqual(1, Count(c, "SELECT UserId FROM UserIdentities WHERE Provider='microsoft' AND Subject='obj-1'"));
            Assert.AreEqual(2, Count(c, "SELECT UserId FROM UserIdentities WHERE Provider='github' AND Subject='4242'"));
            Assert.AreEqual(2, Count(c, "SELECT UserId FROM UserIdentities WHERE Provider='email-link' AND Subject='abc123'"));
            Assert.AreEqual(4, Count(c, "SELECT count(*) FROM UserIdentities"));

            // Feeds: shared, alice-only and the orphan URL that only had items.
            Assert.AreEqual(3, Count(c, "SELECT count(*) FROM Feeds"));
            Assert.AreEqual(1, Count(c, $"SELECT count(*) FROM Feeds WHERE Url='{Shared}' AND ETag='\"etag-1\"' AND LastFetchedUtc LIKE '2026-10-20T10%' AND NextFetchUtc LIKE '2026-10-20T11%' AND IntervalSeconds=3600"));
            Assert.AreEqual(1, Count(c, $"SELECT count(*) FROM Feeds WHERE Url='{AliceOnly}' AND ETag IS NULL AND NextFetchUtc IS NULL"));
            Assert.AreEqual(3, Count(c, "SELECT count(*) FROM Subscriptions"));
            Assert.AreEqual(0, Count(c, $"SELECT count(*) FROM Subscriptions s JOIN Feeds f ON f.Id=s.FeedId WHERE f.Url='{Orphan}'"), "nobody subscribes to the orphan");

            // Tags: alice tech+news (news hidden), bob tech.
            Assert.AreEqual(3, Count(c, "SELECT count(*) FROM Tags"));
            Assert.AreEqual(1, Count(c, "SELECT IsHidden FROM Tags WHERE UserId=1 AND Name='news'"));
            Assert.AreEqual(0, Count(c, "SELECT IsHidden FROM Tags WHERE UserId=1 AND Name='tech'"));
            Assert.AreEqual(3, Count(c, "SELECT count(*) FROM SubscriptionTags"));

            // Posts: 11 shared + 4 alice-only + 2 orphan, stored once.
            Assert.AreEqual(17, Count(c, "SELECT count(*) FROM Posts"));
            Assert.AreEqual(17, Count(c, "SELECT count(*) FROM PostContent"));
            Assert.AreEqual(17, Count(c, "SELECT count(*) FROM Posts_fts"));
            Assert.AreEqual(1, Count(c, $"SELECT count(*) FROM Posts p JOIN Feeds f ON f.Id=p.FeedId WHERE f.Url='{Shared}' AND p.Href='{Shared}/p10'"), "bob's extra post is there once");
            // Content: representative row's ItemContent; p7 falls back to Items.Content from alice, then bob's ItemContent wins first.
            Assert.AreEqual(1, Count(c, $"SELECT count(*) FROM PostContent pc JOIN Posts p ON p.Id=pc.PostId WHERE p.Href='{Shared}/p1' AND pc.Content='<p>content 1</p>'"));
            Assert.AreEqual(1, Count(c, $"SELECT count(*) FROM PostContent pc JOIN Posts p ON p.Id=pc.PostId WHERE p.Href='{Shared}/p7' AND pc.Content='<p>content 7</p>'"), "another subscriber's ItemContent fills the gap");
            Assert.AreEqual(1, Count(c, $"SELECT count(*) FROM PostContent pc JOIN Posts p ON p.Id=pc.PostId WHERE p.Href='{AliceOnly}/p0' AND pc.Content='<p>content 0</p>'"));

            // State: alice read p0..p2 + 2 orphan, saved p5 + orphan p1; bob read p9, saved p5.
            Assert.AreEqual(6, Count(c, "SELECT count(*) FROM UserPostState WHERE IsRead=1"));
            Assert.AreEqual(3, Count(c, "SELECT count(*) FROM UserPostState WHERE IsSaved=1"));
            Assert.AreEqual(2, Count(c, $"SELECT count(*) FROM UserPostState s JOIN Posts p ON p.Id=s.PostId WHERE p.Href='{Shared}/p5' AND s.IsSaved=1"), "both saved the same shared post");
            Assert.AreEqual(0, Count(c, "SELECT count(*) FROM UserPostState WHERE IsRead=0 AND IsSaved=0"), "sparse");

            Assert.AreEqual(1, Count(c, $"SELECT count(*) FROM SchemaMigrations WHERE Id='{DatabaseSchema.MigrationId}' AND DurationMs > 0"));
        }

        // The repositories read the migrated data.
        var users = new SQLiteUserRepository(connections, NullLogger<SQLiteUserRepository>.Instance);
        var feeds = new SQLiteFeedRepository(connections, NullLogger<SQLiteFeedRepository>.Instance);
        using var items = new SQLiteItemRepository(connections, NullLogger<SQLiteItemRepository>.Instance, feeds, users);
        var alice = users.FindByIdentity("microsoft", "obj-1");
        Assert.AreEqual(1, alice.Id);
        Assert.AreEqual("024d97a14bc74c58b9a83e88b919a92c", alice.AadUserId);
        var bob = users.FindByIdentity("github", "4242");
        Assert.AreEqual(2, bob.Id);

        var aliceFeeds = feeds.GetFeeds(alice).ToList();
        Assert.AreEqual(2, aliceFeeds.Count);
        CollectionAssert.AreEquivalent(new[] { "tech", "news" }, aliceFeeds.First(f => f.Href == Shared).Tags.ToList());
        CollectionAssert.AreEqual(new[] { Shared }, feeds.GetHiddenFeedUrls(alice).ToList());

        // Per-user first page matches the old timeline by Href order (spec 7 verification).
        foreach (var (user, excluded) in new[] { (alice, feeds.GetHiddenFeedUrls(alice)), (bob, feeds.GetHiddenFeedUrls(bob)) })
        {
            var page = (await items.GetItemsAsync(new NewsFeed("%", user.Id), false, false, null, 0, 20, excludeFeedUrls: excluded, includeContent: false))
                .DistinctBy(i => i.Href).Select(i => i.Href).ToList();
            var legacy = LegacyTimelineHrefs(user.Id, excluded, 20)
                .Where(h => !h.StartsWith(Orphan)) // old timelines still showed items of a deleted feed; the new one does not
                .ToList();
            CollectionAssert.AreEqual(legacy, page, $"first page for user {user.Id}");
        }

        // Read/saved flags survive on the view, and the shared post id is one id for both.
        var bobTimeline = (await items.GetItemsAsync(new NewsFeed("%", bob.Id), false, false, null, 0, 20, includeContent: false)).ToList();
        Assert.AreEqual(11, bobTimeline.Count);
        Assert.IsTrue(bobTimeline.Single(i => i.Href == $"{Shared}/p9").IsRead);
        Assert.IsTrue(bobTimeline.Single(i => i.Href == $"{Shared}/p5").IsSaved);
        Assert.IsFalse(bobTimeline.Single(i => i.Href == $"{Shared}/p0").IsRead, "alice's read state is not bob's");
        var aliceSaved = (await items.GetItemsAsync(new NewsFeed("%", alice.Id), false, true, null, 0, 20, includeContent: false)).ToList();
        Assert.AreEqual(2, aliceSaved.Count, "saved view: the shared post and the orphan feed's saved post (a deleted feed's saved item stayed visible before too)");
        Assert.IsTrue(aliceSaved.Any(i => i.Id == bobTimeline.Single(b => b.Href == $"{Shared}/p5").Id));
        Assert.IsTrue(aliceSaved.Any(i => i.Href == $"{Orphan}/p1"));

        // Running again is a no-op reporting the recorded state.
        var again = LegacyMigration.RunIfNeeded(connections, NullLogger.Instance);
        Assert.AreEqual("already-applied", again.Status);
        Assert.AreEqual(state.AppliedUtc, again.AppliedUtc);

        // The legacy tables can be dropped by hand afterwards, FTS included. The
        // renamed tables still carry their foreign keys, so children go first
        // and Legacy_Users last.
        using (var c = OpenRaw())
        {
            foreach (var t in LegacyDropOrder)
            {
                Run(c, $"DROP TABLE Legacy_{t};");
            }
            Run(c, "VACUUM;");
            Assert.AreEqual(17, Count(c, "SELECT count(*) FROM Posts"));
            using var check = c.CreateCommand();
            check.CommandText = "PRAGMA integrity_check";
            Assert.AreEqual("ok", check.ExecuteScalar() as string);
        }
    }

    [TestMethod]
    public void Migration_FreshDatabase_IsNotNeeded()
    {
        var connections = new SqliteDbConnections(path);
        var state = LegacyMigration.RunIfNeeded(connections, NullLogger.Instance);
        Assert.AreEqual("not-needed", state.Status);

        // And the repositories create the new schema on an empty file.
        var users = new SQLiteUserRepository(connections, NullLogger<SQLiteUserRepository>.Instance);
        Assert.IsNotNull(users.AddUser("alice"));
        Assert.AreEqual("not-needed", LegacyMigration.RunIfNeeded(connections, NullLogger.Instance).Status, "a new-schema database has no Items table");
    }

    [TestMethod]
    public void Migration_RefusesToStart_WhenLegacyTablesExistWithoutRecord()
    {
        SeedLegacy();
        using (var c = OpenRaw())
        {
            Run(c, "ALTER TABLE FeedValidators RENAME TO Legacy_FeedValidators");
        }
        var connections = new SqliteDbConnections(path);
        Assert.ThrowsException<InvalidOperationException>(() => LegacyMigration.RunIfNeeded(connections, NullLogger.Instance));
    }

    [TestMethod]
    public void Migration_RollsBackEverything_WhenAStepFails()
    {
        SeedLegacy();
        using (var c = OpenRaw())
        {
            // A stray table with the new name but the wrong shape makes the tag
            // load fail half-way through; the whole transaction must roll back.
            Run(c, "CREATE TABLE Tags (Bogus TEXT)");
        }

        var connections = new SqliteDbConnections(path);
        Assert.ThrowsException<SqliteException>(() => LegacyMigration.RunIfNeeded(connections, NullLogger.Instance));

        using (var c = OpenRaw())
        {
            Assert.IsTrue(DatabaseSchema.TableExists(c, "Items"), "rolled back: legacy tables keep their names");
            Assert.IsTrue(DatabaseSchema.TableExists(c, "Users"));
            Assert.IsFalse(DatabaseSchema.TableExists(c, "Legacy_Items"));
            Assert.IsFalse(DatabaseSchema.TableExists(c, "Posts"));
            Assert.IsFalse(DatabaseSchema.TableExists(c, "SchemaMigrations"));
            Assert.AreEqual(27, Count(c, "SELECT count(*) FROM Items"));
        }
    }
}
