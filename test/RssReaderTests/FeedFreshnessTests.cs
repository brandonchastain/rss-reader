using Microsoft.Data.Sqlite;
using RssApp.Data;
using RssReader.Server.Services;

namespace RssReaderTests;

/// <summary>The numbers /api/health/feeds reports to the home Health Service.</summary>
[TestClass]
public class FeedFreshnessTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 18, 0, 0, TimeSpan.Zero);

    private string path = null!;

    [TestInitialize]
    public void Create()
    {
        this.path = Path.Combine(Path.GetTempPath(), "freshness-" + Guid.NewGuid().ToString("N") + ".db");
        using var conn = new SqliteConnection($"Data Source={this.path}");
        conn.Open();
        DatabaseSchema.EnsureCreated(conn);
    }

    [TestCleanup]
    public void Delete()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(this.path);
    }

    [TestMethod]
    public async Task EmptyDatabase_HasNoAge()
    {
        var f = await FeedFreshness.ReadAsync(new SqliteDbConnections(this.path), Now);
        Assert.IsNull(f.NewestItemAgeHours);
        Assert.AreEqual(0, f.FeedsFailing);
        Assert.IsNull(f.LastRefreshUtc);
    }

    [TestMethod]
    public async Task ReportsTheNewestStoredPost_FailingSubscribedFeeds_AndTheLastFetch()
    {
        Exec("""
            INSERT INTO Users (Id, Username, CreatedUtc) VALUES (1, 'u', '2026-01-01T00:00:00.0000000+00:00');
            INSERT INTO Feeds (Id, Url, LastFetchedUtc, ConsecutiveFailures, CreatedUtc) VALUES
                (1, 'https://ok.example/feed', '2026-10-28T17:05:00.0000000+00:00', 0, '2026-01-01T00:00:00.0000000+00:00'),
                (2, 'https://down.example/feed', '2026-10-28T16:00:00.0000000+00:00', 3, '2026-01-01T00:00:00.0000000+00:00'),
                (3, 'https://orphan.example/feed', '2026-10-28T17:59:00.0000000+00:00', 9, '2026-01-01T00:00:00.0000000+00:00');
            INSERT INTO Subscriptions (UserId, FeedId, SubscribedUtc) VALUES
                (1, 1, '2026-01-01T00:00:00.0000000+00:00'),
                (1, 2, '2026-01-01T00:00:00.0000000+00:00');
            INSERT INTO Posts (Id, FeedId, Href, PublishDateOrder, FirstSeenUtc) VALUES
                (1, 1, 'a', 1, '2026-10-28T12:00:00.0000000+00:00'),
                (2, 1, 'b', 99999999999, '2026-10-28T15:36:00.0000000+00:00');
            """);

        var f = await FeedFreshness.ReadAsync(new SqliteDbConnections(this.path), Now);

        // Post 2 claims a far-future publish date; its store time is what counts.
        Assert.AreEqual(2.4, f.NewestItemAgeHours);
        // The orphan feed is not fetched any more, so neither its failures nor its fetch time count.
        Assert.AreEqual(1, f.FeedsFailing);
        Assert.AreEqual(new DateTime(2026, 10, 28, 17, 5, 0, DateTimeKind.Utc), f.LastRefreshUtc);
        Assert.AreEqual(DateTimeKind.Utc, f.LastRefreshUtc!.Value.Kind);
    }

    private void Exec(string sql)
    {
        using var conn = new SqliteConnection($"Data Source={this.path}");
        conn.Open();
        DatabaseSchema.Execute(conn, sql);
    }
}
