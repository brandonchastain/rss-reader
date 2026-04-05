using Microsoft.Data.Sqlite;
using RssApp.Contracts;
using Microsoft.Extensions.Logging;

namespace RssApp.Data;

public class SQLiteFeedRepository : IFeedRepository
{
    private readonly string connectionString;
    private readonly ILogger<SQLiteFeedRepository> logger;

    public SQLiteFeedRepository(
        string connectionString,
        ILogger<SQLiteFeedRepository> logger,
        bool isReadOnly = false)
    {
        this.connectionString = connectionString;
        this.logger = logger;
        if (!isReadOnly) this.InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        using (var connection = new SqliteConnection(this.connectionString))
        {
            connection.OpenWithPragmas();
            
            // Enable WAL mode for better concurrency on network file systems
            var pragmaCommand = connection.CreateCommand();
            pragmaCommand.CommandText = @"
                PRAGMA journal_mode = WAL;
                PRAGMA busy_timeout = 5000;
                PRAGMA synchronous = NORMAL;";
            pragmaCommand.ExecuteNonQuery();
            
            var command = connection.CreateCommand();
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS Feeds (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Url TEXT NOT NULL,
                    IsPaywalled BOOLEAN NOT NULL DEFAULT 0,
                    UserId INTEGER NOT NULL,
                    Tags TEXT DEFAULT NULL,
                    UNIQUE (Url, UserId),
                    FOREIGN KEY (UserId) REFERENCES Users(Id)
                )";
            command.ExecuteNonQuery();

            var tagSettingsCommand = connection.CreateCommand();
            tagSettingsCommand.CommandText = @"
                CREATE TABLE IF NOT EXISTS UserTagSettings (
                    UserId INTEGER NOT NULL,
                    Tag TEXT NOT NULL,
                    IsHidden BOOLEAN NOT NULL DEFAULT 0,
                    PRIMARY KEY (UserId, Tag),
                    FOREIGN KEY (UserId) REFERENCES Users(Id)
                )";
            tagSettingsCommand.ExecuteNonQuery();
        }
    }

    public IEnumerable<NewsFeed> GetFeeds(RssUser user)
    {
        var feeds = new HashSet<NewsFeed>();

        using (var connection = new SqliteConnection(this.connectionString))
        {
            connection.OpenWithPragmas();
            var command = connection.CreateCommand();
            command.CommandText = """
                SELECT f.Id, f.Url, f.UserId, f.IsPaywalled, f.Tags FROM Feeds f
                WHERE f.UserId = @userId
            """;
            command.Parameters.AddWithValue("@userId", user.Id);
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var res = ReadSingleRecord(reader);

                    if (!feeds.Contains(res))
                    {
                        feeds.Add(res);
                        yield return res;
                    }
                }
            }
        }
    }


    public NewsFeed GetFeed(RssUser user, string url)
    {
        var feeds = new HashSet<NewsFeed>();

        using (var connection = new SqliteConnection(this.connectionString))
        {
            connection.OpenWithPragmas();
            var command = connection.CreateCommand();
            command.CommandText = """
                SELECT f.Id, f.Url, f.UserId, f.IsPaywalled, f.Tags FROM Feeds f
                WHERE f.UserId = @userId AND f.Url = @url
            """;
            command.Parameters.AddWithValue("@userId", user.Id);
            command.Parameters.AddWithValue("@url", url);
            using (var reader = command.ExecuteReader())
            {
                if (!reader.HasRows)
                {
                    return null;
                }
                
                if (reader.Read())
                {
                    var res = ReadSingleRecord(reader);
                    return res;
                }
            }
        }

        throw new InvalidDataException($"Feed with URL {url} not found for user {user.Id}.");
    }

    public void AddFeed(NewsFeed feed)
    {
        try
        {
            using (var connection = new SqliteConnection(this.connectionString))
            {
                connection.OpenWithPragmas();
                var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO Feeds (Url, UserId) VALUES (@url, @userId)";
                command.Parameters.AddWithValue("@url", feed.Href);
                command.Parameters.AddWithValue("@userId", feed.UserId);

                command.ExecuteNonQuery();
            }

            using (var connection = new SqliteConnection(this.connectionString))
            {
                connection.OpenWithPragmas();
                var command = connection.CreateCommand();
                command.CommandText = "SELECT Id FROM Feeds WHERE Url = @url AND UserId = @userId";
                command.Parameters.AddWithValue("@url", feed.Href);
                command.Parameters.AddWithValue("@userId", feed.UserId);

                using (var reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        feed.FeedId = reader.GetInt32(0);
                    }
                }
            }
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19 && ex.Message.Contains("UNIQUE"))
        {
            // Feed already exists, just update the ID
            using (var connection = new SqliteConnection(this.connectionString))
            {
                connection.OpenWithPragmas();
                var command = connection.CreateCommand();
                command.CommandText = "SELECT Id FROM Feeds WHERE Url = @url AND UserId = @userId";
                command.Parameters.AddWithValue("@url", feed.Href);
                command.Parameters.AddWithValue("@userId", feed.UserId);

                using (var reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        feed.FeedId = reader.GetInt32(0);
                    }
                }
            }
        }
    }

    public string GetTagsByFeedId(int feedId)
    {
        using (var connection = new SqliteConnection(this.connectionString))
        {
            connection.OpenWithPragmas();
            var command = connection.CreateCommand();
            command.CommandText = "SELECT Tags FROM Feeds WHERE Id = @feedId";
            command.Parameters.AddWithValue("@feedId", feedId);
            using (var reader = command.ExecuteReader())
            {
                if (reader.Read())
                {
                    return reader.IsDBNull(0) ? null : reader.GetString(0);
                }
            }
        }

        return null;
    }

    public void AddTag(NewsFeed feed, string tag)
    {
        var existing = GetTagsByFeedId(feed.FeedId)?.Split(',').ToList();
        if (existing == null)
        {
            existing = new List<string>();
        }

        if (existing.Contains(tag))
        {
            return;
        }

        existing.Add(tag);

        using (var connection = new SqliteConnection(this.connectionString))
        {
            connection.OpenWithPragmas();
            var command = connection.CreateCommand();
            command.CommandText = "UPDATE Feeds SET Tags = @tags WHERE Id = @feedId AND UserId = @userId";
            command.Parameters.AddWithValue("@feedId", feed.FeedId);
            command.Parameters.AddWithValue("@tags", string.Join(',', existing));
            command.Parameters.AddWithValue("@userId", feed.UserId);
            command.ExecuteNonQuery();
        }
    }

    public void AddFeeds(RssUser user, IEnumerable<NewsFeed> feeds)
    {
        if (user == null || feeds == null || !feeds.Any())
        {
            return;
        }

        // Get existing feeds for this user to check for duplicates
        var existingFeeds = GetFeeds(user).ToList();
        var existingUrls = existingFeeds.Select(f => f.Href).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var feed in feeds)
        {
            // Set the user ID for the feed
            feed.UserId = user.Id;
            
            // Skip if feed already exists for this user
            if (existingUrls.Contains(feed.Href))
            {
                continue;
            }

            // Add the feed using the existing method
            AddFeed(feed);
            
            // Add tags if any
            if (feed.Tags != null && feed.Tags.Any())
            {
                foreach (var tag in feed.Tags)
                {
                    if (!string.IsNullOrWhiteSpace(tag))
                    {
                        AddTag(feed, tag);
                    }
                }
            }
        }
    }

    private NewsFeed ReadSingleRecord(SqliteDataReader reader)
    {
        var feedId = reader.IsDBNull(reader.GetOrdinal("Id")) ? 0 : reader.GetInt32(reader.GetOrdinal("Id"));
        var url = reader.IsDBNull(reader.GetOrdinal("Url")) ? "" : reader.GetString(reader.GetOrdinal("Url"));
        var tags = reader.IsDBNull(reader.GetOrdinal("Tags")) ? "" : reader.GetString(reader.GetOrdinal("Tags"));
        var userId = reader.IsDBNull(reader.GetOrdinal("UserId")) ? 1 : reader.GetInt32(reader.GetOrdinal("UserId"));
        //var isPaywalled = reader.IsDBNull(reader.GetOrdinal("IsPaywalled")) ? false : reader.GetBoolean(reader.GetOrdinal("IsPaywalled"));
        var res = new NewsFeed(feedId, url, userId);
        res.Tags = tags?.Split(',').Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        return res;
    }

    public void DeleteFeed(RssUser user, string url)
    {
        using (var connection = new SqliteConnection(this.connectionString))
        {
            connection.OpenWithPragmas();
            var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM Feeds
                WHERE Url = @url AND UserId = @userId
            """;
            command.Parameters.AddWithValue("@url", url);
            command.Parameters.AddWithValue("@userId", user.Id);
            command.ExecuteNonQuery();
        }
    }

    public IEnumerable<TagSetting> GetTagSettings(RssUser user)
    {
        var allTags = GetFeeds(user)
            .SelectMany(f => f.Tags ?? Enumerable.Empty<string>())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var hiddenTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using (var connection = new SqliteConnection(this.connectionString))
        {
            connection.OpenWithPragmas();
            var command = connection.CreateCommand();
            command.CommandText = "SELECT Tag FROM UserTagSettings WHERE UserId = @userId AND IsHidden = 1";
            command.Parameters.AddWithValue("@userId", user.Id);
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    hiddenTags.Add(reader.GetString(0));
                }
            }
        }

        return allTags.Select(t => new TagSetting
        {
            Tag = t,
            IsHidden = hiddenTags.Contains(t)
        }).OrderBy(t => t.Tag).ToList();
    }

    public void SetTagHidden(RssUser user, string tag, bool isHidden)
    {
        using (var connection = new SqliteConnection(this.connectionString))
        {
            connection.OpenWithPragmas();
            var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT INTO UserTagSettings (UserId, Tag, IsHidden)
                VALUES (@userId, @tag, @isHidden)
                ON CONFLICT(UserId, Tag) DO UPDATE SET IsHidden = excluded.IsHidden";
            command.Parameters.AddWithValue("@userId", user.Id);
            command.Parameters.AddWithValue("@tag", tag);
            command.Parameters.AddWithValue("@isHidden", isHidden);
            command.ExecuteNonQuery();
        }
    }

    public IEnumerable<string> GetHiddenFeedUrls(RssUser user)
    {
        var hiddenTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using (var connection = new SqliteConnection(this.connectionString))
        {
            connection.OpenWithPragmas();
            var command = connection.CreateCommand();
            command.CommandText = "SELECT Tag FROM UserTagSettings WHERE UserId = @userId AND IsHidden = 1";
            command.Parameters.AddWithValue("@userId", user.Id);
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    hiddenTags.Add(reader.GetString(0));
                }
            }
        }

        if (!hiddenTags.Any())
        {
            return Enumerable.Empty<string>();
        }

        return GetFeeds(user)
            .Where(f => f.Tags != null && f.Tags.Any(t => hiddenTags.Contains(t)))
            .Select(f => f.Href)
            .ToList();
    }
}