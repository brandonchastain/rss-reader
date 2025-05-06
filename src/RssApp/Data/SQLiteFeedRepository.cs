using System.Data.SQLite;
using RssApp.Contracts;

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
        using (var connection = new SQLiteConnection(this.connectionString))
        {
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS Feeds (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Url TEXT NOT NULL,
                    IsPaywalled BOOLEAN NOT NULL DEFAULT 0,
                    UserId INTEGER NOT NULL,
                    UNIQUE (Url, UserId),
                    FOREIGN KEY (UserId) REFERENCES Users(Id)
                )";
            command.ExecuteNonQuery();

            command = connection.CreateCommand();
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS FeedTags (
                    UserId INTEGER NOT NULL,
                    FeedId INTEGER NOT NULL,
                    TagName TEXT NOT NULL,
                    FOREIGN KEY (UserId) REFERENCES Users(Id),
                    FOREIGN KEY (FeedId) REFERENCES Feeds(Id),
                    Unique(FeedId, TagName, UserId),
                    PRIMARY KEY (FeedId, TagName, UserId)
                )";
            command.ExecuteNonQuery();
            
            command = connection.CreateCommand();
            command.CommandText = @"
                CREATE INDEX IF NOT EXISTS idx_feedtag_feedid_userid
                ON FeedTags (FeedId, UserId);";
            command.ExecuteNonQuery();
        }
    }

    public IEnumerable<NewsFeed> GetFeeds(RssUser user)
    {
        var feeds = new HashSet<NewsFeed>();

        using (var connection = new SQLiteConnection(this.connectionString))
        {
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = """
                SELECT f.Id, f.Url, f.UserId, f.IsPaywalled, t.TagName FROM Feeds f
                LEFT JOIN FeedTags t
                ON f.Id = t.FeedId AND f.UserId = t.UserId
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
                    }

                    feeds.TryGetValue(res, out var existingFeed);

                    if (existingFeed == null)
                    {
                        continue;
                    }
                    
                    existingFeed.Tags = existingFeed.Tags.Union(res.Tags).ToList();
                }
            }
        }

        foreach (var feed in feeds)
        {
            yield return feed;
        }
    }

    public void AddFeed(NewsFeed feed)
    {
        using (var connection = new SQLiteConnection(this.connectionString))
        {
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO Feeds (Url, UserId) VALUES (@url, @userId)";
            command.Parameters.AddWithValue("@url", feed.FeedUrl);
            command.Parameters.AddWithValue("@userId", feed.UserId);
            
            command.ExecuteNonQuery();
        }
        
        using (var connection = new SQLiteConnection(this.connectionString))
        {
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = "SELECT Id FROM Feeds WHERE Url = @url AND UserId = @userId";
            command.Parameters.AddWithValue("@url", feed.FeedUrl);
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

    public void Update(NewsFeed feed)
    {
        using (var connection = new SQLiteConnection(this.connectionString))
        {
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE Feeds
                SET IsPaywalled = @isPaywalled
                WHERE Url = @url AND UserId = @userId
            """;
            command.Parameters.AddWithValue("@isPaywalled", feed.IsPaywalled);
            command.Parameters.AddWithValue("@url", feed.FeedUrl);
            command.Parameters.AddWithValue("@userId", feed.UserId);
            command.ExecuteNonQuery();
        }
    }

    public string? GetTag(int userId, string tagName)
    {
        using (var connection = new SQLiteConnection(this.connectionString))
        {
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = "SELECT TagName FROM FeedTags WHERE TagName = @tagName AND UserId = @userId";
            command.Parameters.AddWithValue("@tagName", tagName);
            command.Parameters.AddWithValue("@userId", userId);
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

    public string? GetTagByFeedId(int feedId, string tagName)
    {
        using (var connection = new SQLiteConnection(this.connectionString))
        {
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = "SELECT TagName FROM FeedTags WHERE TagName = @tagName AND FeedId = @feedId";
            command.Parameters.AddWithValue("@tagName", tagName);
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
        this.logger.LogInformation("Adding {tag} to feed {feedId}", tag, feed.FeedId);
        var existing = GetTagByFeedId(feed.FeedId, tag);
        if (existing != null)
        {
            this.logger.LogInformation("tag {tag} on feed {feedId} exists", tag, feed.FeedId);
            return;
        }
        
        using (var connection = new SQLiteConnection(this.connectionString))
        {
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO FeedTags (FeedId, TagName, UserId) VALUES (@feedId, @tagName, @userId)";
            command.Parameters.AddWithValue("@feedId", feed.FeedId);
            command.Parameters.AddWithValue("@tagName", tag);
            command.Parameters.AddWithValue("@userId", feed.UserId);
            command.ExecuteNonQuery();
            this.logger.LogInformation("Tag {tagName} added to feed {feedId}", tag, feed.FeedId);
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
        var existingUrls = existingFeeds.Select(f => f.FeedUrl).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var feed in feeds)
        {
            // Set the user ID for the feed
            feed.UserId = user.Id;
            
            // Skip if feed already exists for this user
            if (existingUrls.Contains(feed.FeedUrl))
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

    private NewsFeed ReadSingleRecord(SQLiteDataReader reader)
    {
        var feedId = reader.IsDBNull(reader.GetOrdinal("Id")) ? 0 : reader.GetInt32(reader.GetOrdinal("Id"));
        var url = reader.IsDBNull(reader.GetOrdinal("Url")) ? "" : reader.GetString(reader.GetOrdinal("Url"));
        var userId = reader.IsDBNull(reader.GetOrdinal("UserId")) ? 1 : reader.GetInt32(reader.GetOrdinal("UserId"));
        var isPaywalled = reader.IsDBNull(reader.GetOrdinal("IsPaywalled")) ? false : reader.GetBoolean(reader.GetOrdinal("IsPaywalled"));
        var tagName = reader.IsDBNull(reader.GetOrdinal("TagName")) ? "" : reader.GetString(reader.GetOrdinal("TagName"));
        var res = new NewsFeed(feedId, url, userId, isPaywalled);
        res.Tags.Add(tagName);
        return res;
    }

    public void DeleteFeed(RssUser user, string url)
    {
        using (var connection = new SQLiteConnection(this.connectionString))
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