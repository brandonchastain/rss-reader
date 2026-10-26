using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RssApp.Contracts;
using RssApp.Data;

namespace RssReaderTests;

/// <summary>
/// Writes the SQLite database the browser (Playwright) suite in test/e2e runs against.
/// Feed URLs cannot be fetched in a test (the API refuses loopback and the suite is
/// offline), so the posts are inserted through the real repositories instead, which
/// keeps the fixture in step with the schema. It does nothing unless
/// E2E_FIXTURE_PATH is set, so an ordinary test run just reports it as skipped.
///
/// The data, relied on by name in test/e2e/tests:
///   Tech 01..30   feed tech.example.com   tag "tech"          Tech 01-05 read, Tech 07 saved, Tech 12 title has "quantum"
///   World 01..30  feed world.example.com  tag "news"          World 07 saved, World 15 body has "zeppelin"
///   Disposable 1-3 feed disposable.example.com  no tags       (the delete test removes this feed)
///   Muted 01..06  feed muted.example.com  tag "muted", hidden (never on the timeline)
/// Tech and World alternate in time, World 30 newest. The signed-in identity is the
/// API's test user ("test-user-id"), which is also the administrator.
/// </summary>
[TestClass]
public sealed class E2eFixtureBuilder
{
    public const string TestLoginId = "test-user-id";

    [TestMethod]
    public async Task Build()
    {
        var path = Environment.GetEnvironmentVariable("E2E_FIXTURE_PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            Assert.Inconclusive("Set E2E_FIXTURE_PATH to write the browser-test database.");
            return;
        }

        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            File.Delete(path + suffix);
        }

        var connections = new SqliteDbConnections(path);
        var users = new SQLiteUserRepository(connections, NullLogger<SQLiteUserRepository>.Instance);
        var feeds = new SQLiteFeedRepository(connections, NullLogger<SQLiteFeedRepository>.Instance);
        var items = new SQLiteItemRepository(connections, NullLogger<SQLiteItemRepository>.Instance, feeds, users);
        var stats = new SQLiteSystemStatsRepository(connections);

        var user = users.AddUser(TestLoginId);
        users.LinkIdentity(user.Id, "microsoft", TestLoginId);

        const string tech = "https://tech.example.com/feed.xml";
        const string world = "https://world.example.com/feed.xml";
        const string disposable = "https://disposable.example.com/feed.xml";
        const string muted = "https://muted.example.com/feed.xml";

        feeds.AddFeed(new NewsFeed(tech, user.Id) { Tags = new List<string> { "tech" } });
        feeds.AddFeed(new NewsFeed(world, user.Id) { Tags = new List<string> { "news" } });
        feeds.AddFeed(new NewsFeed(disposable, user.Id));
        feeds.AddFeed(new NewsFeed(muted, user.Id) { Tags = new List<string> { "muted" } });
        feeds.SetTagHidden(user, "muted", true);

        var start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        await items.AddItemsAsync(Posts(tech, "Tech", 30, 1_000_000, start, 0, i => i == 12 ? " quantum widgets" : "", _ => "A tech post."));
        await items.AddItemsAsync(Posts(world, "World", 30, 1_000_030, start, 30, _ => "", i => i == 15 ? "The zeppelin lands at noon." : "A world post."));
        await items.AddItemsAsync(Posts(disposable, "Disposable", 3, 900_000, start, 0, _ => "", _ => "A throwaway post.", pad: false));
        await items.AddItemsAsync(Posts(muted, "Muted", 6, 800_000, start, 0, _ => "", _ => "A muted post."));

        for (var i = 1; i <= 5; i++)
        {
            items.MarkAsRead(items.GetItem(user, $"{tech}/{i:00}"), true, user);
        }
        items.SavePost(items.GetItem(user, $"{tech}/07"), user);
        items.SavePost(items.GetItem(user, $"{world}/07"), user);

        // Snapshots for the admin trends chart: inside each of 1h, 1d, 7d, 30d and 1y.
        var now = DateTime.UtcNow;
        var ages = new[] { TimeSpan.FromDays(200), TimeSpan.FromDays(20), TimeSpan.FromDays(3), TimeSpan.FromHours(12), TimeSpan.FromMinutes(40), TimeSpan.FromMinutes(10) };
        for (var i = 0; i < ages.Length; i++)
        {
            stats.RecordSnapshot(new SystemStatsSnapshot
            {
                Timestamp = now - ages[i],
                UserCount = 1,
                FeedCount = 4,
                ItemCount = 50 + i * 10,
                DbSizeBytes = 1_000_000 + i * 50_000,
            });
        }

        // Fold the write-ahead log into the file so it can be copied on its own.
        using (var c = connections.OpenWrite())
        {
            DatabaseSchema.Execute(c, "PRAGMA wal_checkpoint(TRUNCATE);");
        }

        items.Dispose();
        SqliteConnection.ClearAllPools();
        Assert.IsTrue(new FileInfo(path).Length > 0);
    }

    private static List<NewsFeedItem> Posts(
        string feedUrl, string label, int count, long firstOrder, DateTime start, int minuteOffset,
        Func<int, string> titleSuffix, Func<int, string> body, bool pad = true)
    {
        return Enumerable.Range(1, count).Select(i => new NewsFeedItem(
            "0", 0,
            $"{label} {(pad ? i.ToString("00") : i.ToString())}{titleSuffix(i)}",
            $"{feedUrl}/{(pad ? i.ToString("00") : i.ToString())}",
            null,
            start.AddMinutes(i * 60 + minuteOffset).ToString("o"),
            $"<p>{body(i)}</p>",
            null)
        {
            FeedUrl = feedUrl,
            PublishDateOrder = firstOrder + i * 60,
        }).ToList();
    }
}
