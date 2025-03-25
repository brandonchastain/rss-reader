using System.Collections.Concurrent;
using System.Data.SQLite;
using RssApp.Contracts;

namespace RssApp.Persistence;

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
                    Url TEXT NOT NULL UNIQUE,
                    IsPaywalled BOOLEAN NOT NULL DEFAULT 0,
                    UserId INTEGER NOT NULL,
                    FOREIGN KEY (UserId) REFERENCES Users(Id)
                )";
            command.ExecuteNonQuery();
        }
    }

    public IEnumerable<NewsFeed> GetFeeds(RssUser user)
    {
        List<NewsFeed> feeds = new List<NewsFeed>();

        using (var connection = new SQLiteConnection(this.connectionString))
        {
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = """
                SELECT * FROM Feeds
                WHERE UserId = @userId
            """;
            command.Parameters.AddWithValue("@userId", user.Id);
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var url = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    var userId = reader.IsDBNull(reader.GetOrdinal("UserId")) ? 1 : reader.GetInt32(reader.GetOrdinal("UserId"));
                    var isPaywalled = reader.IsDBNull(reader.GetOrdinal("IsPaywalled")) ? false : reader.GetBoolean(reader.GetOrdinal("IsPaywalled"));
                    var res = new NewsFeed(url, userId, isPaywalled);
                    feeds.Add(res);
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