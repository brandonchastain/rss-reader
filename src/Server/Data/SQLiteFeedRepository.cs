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
        ILogger<SQLiteFeedRepository> logger)
    {
        this.connectionString = connectionString;
        this.logger = logger;
        this.InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        using (var connection = new SqliteConnection(this.connectionString))
        {
            connection.Open();
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
        }
    }

    public IEnumerable<NewsFeed> GetFeeds(RssUser user)
    {
        var feeds = new HashSet<NewsFeed>();

        using (var connection = new SqliteConnection(this.connectionString))
        {
            connection.Open();
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
            connection.Open();
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
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO Feeds (Url, UserId) VALUES (@url, @userId)";
                command.Parameters.AddWithValue("@url", feed.Href);
                command.Parameters.AddWithValue("@userId", feed.UserId);

                command.ExecuteNonQuery();
            }

            using (var connection = new SqliteConnection(this.connectionString))
            {
                connection.Open();
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
                connection.Open();
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

    public void Update(NewsFeed feed)
    {
    }

    public string? GetTagByFeedId(int feedId, string tagName)
    {
        using (var connection = new SqliteConnection(this.connectionString))
        {
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = "SELECT Tags FROM Feeds WHERE Tags LIKE @tagName AND Id = @feedId";
            command.Parameters.AddWithValue("@tagName", $"%{tagName}%");
            command.Parameters.AddWithValue("@feedId", feedId);
            using (var reader = command.ExecuteReader())
            {
                if (reader.Read())
                {
                    return reader.GetString(0);
                }
            }
        }

        return null;
    }

    public void AddTag(NewsFeed feed, string tag)
    {
        var existing = GetTagByFeedId(feed.FeedId, tag)?.Split(',').ToList();
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
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = "UPDATE Feeds SET Tags = @tags WHERE Id = @feedId AND UserId = @userId";
            command.Parameters.AddWithValue("@feedId", feed.FeedId);
            command.Parameters.AddWithValue("@tags", string.Join(',', existing));
            command.Parameters.AddWithValue("@userId", feed.UserId);
            command.ExecuteNonQuery();
        }
    }

    public void ImportFeeds(RssUser user, IEnumerable<NewsFeed> feeds)
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
            connection.Open();
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
}