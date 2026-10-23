using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using RssApp.Contracts;

namespace RssApp.Data;

public class SQLiteFeedRepository : IFeedRepository
{
    private readonly IDbConnections connections;
    private readonly ILogger<SQLiteFeedRepository> logger;

    public SQLiteFeedRepository(
        IDbConnections connections,
        ILogger<SQLiteFeedRepository> logger,
        bool isReadOnly = false)
    {
        this.connections = connections;
        this.logger = logger;
        if (!isReadOnly)
        {
            using var connection = this.connections.OpenWrite();
            DatabaseSchema.EnsureCreated(connection);
        }
    }

    private const string SubscriptionSelect = """
        SELECT f.Id, f.Url, s.UserId,
               (SELECT group_concat(t.Name, ',') FROM SubscriptionTags st
                JOIN Tags t ON t.Id = st.TagId
                WHERE st.UserId = s.UserId AND st.FeedId = s.FeedId) AS Tags
        FROM Subscriptions s
        JOIN Feeds f ON f.Id = s.FeedId
        """;

    public IEnumerable<NewsFeed> GetFeeds(RssUser user)
    {
        var feeds = new List<NewsFeed>();
        using var connection = this.connections.OpenRead();
        var command = connection.CreateCommand();
        command.CommandText = SubscriptionSelect + " WHERE s.UserId = @userId ORDER BY f.Id";
        command.Parameters.AddWithValue("@userId", user.Id);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            feeds.Add(ReadSingleRecord(reader));
        }

        return feeds;
    }

    public NewsFeed GetFeed(RssUser user, string url)
    {
        using var connection = this.connections.OpenRead();
        var command = connection.CreateCommand();
        command.CommandText = SubscriptionSelect + " WHERE s.UserId = @userId AND f.Url = @url";
        command.Parameters.AddWithValue("@userId", user.Id);
        command.Parameters.AddWithValue("@url", url);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadSingleRecord(reader) : null;
    }

    public void AddFeed(NewsFeed feed)
    {
        using var connection = this.connections.OpenWrite();
        using var transaction = connection.BeginTransaction();
        Subscribe(connection, transaction, feed);
        transaction.Commit();
    }

    public void AddFeeds(RssUser user, IEnumerable<NewsFeed> feeds)
    {
        if (user == null || feeds == null)
        {
            return;
        }

        using var connection = this.connections.OpenWrite();
        using var transaction = connection.BeginTransaction();
        foreach (var feed in feeds)
        {
            if (string.IsNullOrWhiteSpace(feed.Href)) continue;
            feed.UserId = user.Id;
            Subscribe(connection, transaction, feed);
        }

        transaction.Commit();
    }

    /// <summary>
    /// Upserts the Feeds row by URL (a new row has NextFetchUtc NULL, so the
    /// scheduler fetches it on its next pass), inserts the subscription if it
    /// does not exist, and attaches feed.Tags. Existing posts of the feed are
    /// visible to the subscriber at once; no per-user fetch happens.
    /// </summary>
    private static void Subscribe(SqliteConnection connection, SqliteTransaction transaction, NewsFeed feed)
    {
        var now = Now();

        var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO Feeds (Url, CreatedUtc) VALUES (@url, @now)
            ON CONFLICT(Url) DO NOTHING;
            SELECT Id FROM Feeds WHERE Url = @url;
            """;
        cmd.Parameters.AddWithValue("@url", feed.Href);
        cmd.Parameters.AddWithValue("@now", now);
        feed.FeedId = Convert.ToInt32(cmd.ExecuteScalar());

        cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO Subscriptions (UserId, FeedId, SubscribedUtc) VALUES (@userId, @feedId, @now)
            ON CONFLICT(UserId, FeedId) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("@userId", feed.UserId);
        cmd.Parameters.AddWithValue("@feedId", feed.FeedId);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.ExecuteNonQuery();

        foreach (var tag in (feed.Tags ?? Array.Empty<string>()).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct())
        {
            AttachTag(connection, transaction, feed.UserId, feed.FeedId, tag.Trim());
        }
    }

    private static void AttachTag(SqliteConnection connection, SqliteTransaction transaction, int userId, int feedId, string tag)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO Tags (UserId, Name) VALUES (@userId, @name)
            ON CONFLICT(UserId, Name) DO NOTHING;
            INSERT INTO SubscriptionTags (UserId, FeedId, TagId)
            SELECT @userId, @feedId, Id FROM Tags WHERE UserId = @userId AND Name = @name
            ON CONFLICT(UserId, FeedId, TagId) DO NOTHING;
            """;
        cmd.Parameters.AddWithValue("@userId", userId);
        cmd.Parameters.AddWithValue("@feedId", feedId);
        cmd.Parameters.AddWithValue("@name", tag);
        cmd.ExecuteNonQuery();
    }

    public void AddTag(NewsFeed feed, string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return;
        using var connection = this.connections.OpenWrite();
        using var transaction = connection.BeginTransaction();

        // Callers may hold a NewsFeed built before the subscription existed
        // (FeedId -1); resolve the feed by URL when needed.
        var feedId = feed.FeedId;
        if (feedId <= 0)
        {
            var lookup = connection.CreateCommand();
            lookup.Transaction = transaction;
            lookup.CommandText = "SELECT Id FROM Feeds WHERE Url = @url";
            lookup.Parameters.AddWithValue("@url", feed.Href);
            var id = lookup.ExecuteScalar();
            if (id is null or DBNull) return;
            feedId = Convert.ToInt32(id);
        }

        AttachTag(connection, transaction, feed.UserId, feedId, tag.Trim());
        transaction.Commit();
        feed.Tags ??= new List<string>();
        if (!feed.Tags.Contains(tag)) feed.Tags.Add(tag);
    }

    private static NewsFeed ReadSingleRecord(SqliteDataReader reader)
    {
        var feedId = reader.GetInt32(0);
        var url = reader.IsDBNull(1) ? "" : reader.GetString(1);
        var userId = reader.GetInt32(2);
        var tags = reader.IsDBNull(3) ? "" : reader.GetString(3);
        var res = new NewsFeed(feedId, url, userId);
        res.Tags = tags.Split(',').Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        return res;
    }

    public void DeleteFeed(RssUser user, string url)
    {
        using var connection = this.connections.OpenWrite();
        var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM Subscriptions
            WHERE UserId = @userId AND FeedId IN (SELECT Id FROM Feeds WHERE Url = @url)
            """;
        command.Parameters.AddWithValue("@url", url);
        command.Parameters.AddWithValue("@userId", user.Id);
        command.ExecuteNonQuery();
    }

    public IEnumerable<TagSetting> GetTagSettings(RssUser user)
    {
        var settings = new List<TagSetting>();
        using var connection = this.connections.OpenRead();
        var command = connection.CreateCommand();
        // Only tags that are attached to at least one subscription, like the
        // old per-feed tag strings; a tag row left behind by an unsubscribe is
        // not shown.
        command.CommandText = """
            SELECT t.Name, t.IsHidden FROM Tags t
            WHERE t.UserId = @userId
              AND EXISTS (SELECT 1 FROM SubscriptionTags st WHERE st.TagId = t.Id)
            ORDER BY t.Name
            """;
        command.Parameters.AddWithValue("@userId", user.Id);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            settings.Add(new TagSetting { Tag = reader.GetString(0), IsHidden = reader.GetInt64(1) != 0 });
        }

        return settings;
    }

    public void SetTagHidden(RssUser user, string tag, bool isHidden)
    {
        using var connection = this.connections.OpenWrite();
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Tags (UserId, Name, IsHidden) VALUES (@userId, @name, @isHidden)
            ON CONFLICT(UserId, Name) DO UPDATE SET IsHidden = excluded.IsHidden
            """;
        command.Parameters.AddWithValue("@userId", user.Id);
        command.Parameters.AddWithValue("@name", tag);
        command.Parameters.AddWithValue("@isHidden", isHidden ? 1 : 0);
        command.ExecuteNonQuery();
    }

    public IEnumerable<string> GetHiddenFeedUrls(RssUser user)
    {
        var urls = new List<string>();
        using var connection = this.connections.OpenRead();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT f.Url FROM SubscriptionTags st
            JOIN Tags t ON t.Id = st.TagId
            JOIN Feeds f ON f.Id = st.FeedId
            WHERE st.UserId = @userId AND t.IsHidden = 1
            """;
        command.Parameters.AddWithValue("@userId", user.Id);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            urls.Add(reader.GetString(0));
        }

        return urls;
    }

    public void DeleteAllFeeds(RssUser user)
    {
        using var connection = this.connections.OpenWrite();
        using var transaction = connection.BeginTransaction();

        var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "DELETE FROM Subscriptions WHERE UserId = @userId; DELETE FROM Tags WHERE UserId = @userId;";
        cmd.Parameters.AddWithValue("@userId", user.Id);
        cmd.ExecuteNonQuery();

        transaction.Commit();
    }

    // --- Fetch scheduling ---

    private const string FetchStateSelect = """
        SELECT f.Id, f.Url, f.ETag, f.LastModified, f.LastFetchedUtc, f.NextFetchUtc, f.ConsecutiveFailures
        FROM Feeds f
        """;

    public IReadOnlyList<FeedFetchState> GetFeedFetchStates()
    {
        using var connection = this.connections.OpenRead();
        var command = connection.CreateCommand();
        command.CommandText = FetchStateSelect + " WHERE EXISTS (SELECT 1 FROM Subscriptions s WHERE s.FeedId = f.Id) ORDER BY f.Id";
        return ReadStates(command);
    }

    public FeedFetchState GetFeedFetchState(string url)
    {
        using var connection = this.connections.OpenRead();
        var command = connection.CreateCommand();
        command.CommandText = FetchStateSelect + " WHERE f.Url = @url";
        command.Parameters.AddWithValue("@url", url);
        return ReadStates(command).FirstOrDefault();
    }

    public IReadOnlyList<FeedFetchState> GetDueFeeds(DateTimeOffset now)
        => GetFeedFetchStates().Where(s => s.NextFetchUtc == null || s.NextFetchUtc <= now).ToList();

    private static IReadOnlyList<FeedFetchState> ReadStates(SqliteCommand command)
    {
        var states = new List<FeedFetchState>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            states.Add(new FeedFetchState(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                ParseTime(reader, 3),
                ParseTime(reader, 4),
                ParseTime(reader, 5),
                reader.GetInt32(6)));
        }

        return states;
    }

    public void RecordFetchResult(
        string url,
        DateTimeOffset fetchedUtc,
        DateTimeOffset nextFetchUtc,
        TimeSpan interval,
        string etag,
        DateTimeOffset? lastModified,
        bool keepValidators,
        bool failed,
        string error)
    {
        try
        {
            using var connection = this.connections.OpenWrite();
            var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE Feeds SET
                    LastFetchedUtc = @fetched,
                    NextFetchUtc = @next,
                    IntervalSeconds = @interval,
                    ETag = CASE WHEN @keep THEN ETag ELSE @etag END,
                    LastModified = CASE WHEN @keep THEN LastModified ELSE @lastModified END,
                    ConsecutiveFailures = CASE WHEN @failed THEN ConsecutiveFailures + 1 ELSE 0 END,
                    LastError = @error
                WHERE Url = @url
                """;
            command.Parameters.AddWithValue("@url", url);
            command.Parameters.AddWithValue("@fetched", fetchedUtc.ToString("o", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("@next", nextFetchUtc.ToString("o", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("@interval", (long)interval.TotalSeconds);
            command.Parameters.AddWithValue("@keep", keepValidators ? 1 : 0);
            command.Parameters.AddWithValue("@etag", (object)etag ?? DBNull.Value);
            command.Parameters.AddWithValue("@lastModified",
                lastModified.HasValue ? lastModified.Value.ToString("o", CultureInfo.InvariantCulture) : DBNull.Value);
            command.Parameters.AddWithValue("@failed", failed ? 1 : 0);
            command.Parameters.AddWithValue("@error", (object)error ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            // Best-effort: a schedule-persistence failure must never break a refresh.
            this.logger.LogWarning(ex, "Failed to record fetch result for {url}", url);
        }
    }

    private static DateTimeOffset? ParseTime(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        return DateTimeOffset.TryParse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)
            ? value
            : null;
    }

    private static string Now() => DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
}
