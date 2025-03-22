using System.Collections.Concurrent;
using System.Data.SQLite;
using RssApp.Contracts;

namespace RssApp.Persistence;

public class SQLiteFeedRepository : IFeedRepository
{
    private readonly string connectionString;
    private readonly ILogger<SQLiteFeedRepository> logger;
    private ConcurrentBag<NewsFeed> cachedFeeds;

    public SQLiteFeedRepository(
        string connectionString,
        ILogger<SQLiteFeedRepository> logger)
    {
        this.connectionString = connectionString;
        this.logger = logger;
        this.cachedFeeds = new ConcurrentBag<NewsFeed>();
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
                    IsPaywalled BOOLEAN NOT NULL DEFAULT 0
                )";
            command.ExecuteNonQuery();
        }
    }

    public IEnumerable<NewsFeed> GetFeeds()
    {
        if (this.cachedFeeds.Count == 0)
        {
            List<NewsFeed> feeds = new List<NewsFeed>();
            using (var connection = new SQLiteConnection(this.connectionString))
            {
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText = "SELECT * FROM Feeds";
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var url = reader.IsDBNull(1) ? "" : reader.GetString(1);
                        var isPaywalled = reader.FieldCount < 2 || reader.IsDBNull(2) ? false : reader.GetBoolean(2);
                        var res = new NewsFeed(url, isPaywalled);
                        feeds.Add(res);
                    }
                }
            }
            foreach (var feed in feeds)
            {
                this.cachedFeeds.Add(feed);
            }
        }
        
        foreach (var feed in this.cachedFeeds)
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
            command.CommandText = "INSERT INTO Feeds (Url) VALUES (@url)";
            command.Parameters.AddWithValue("@url", feed.FeedUrl);
            command.ExecuteNonQuery();
        }

        this.logger.LogInformation("add getting lock");
        this.cachedFeeds.Add(feed);
    }

    public void Update(NewsFeed feed)
    {
        using (var connection = new SQLiteConnection(this.connectionString))
        {
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = "UPDATE Feeds SET IsPaywalled = @isPaywalled WHERE Url = @url";
            command.Parameters.AddWithValue("@isPaywalled", feed.IsPaywalled);
            command.Parameters.AddWithValue("@url", feed.FeedUrl);
            command.ExecuteNonQuery();
        }
        this.logger.LogInformation("Updated feed {Url} in the database", feed.FeedUrl);

        this.logger.LogInformation("update getting lock");
        this.cachedFeeds.Clear();
    }

    public void DeleteFeed(string url)
    {
        using (var connection = new SQLiteConnection(this.connectionString))
        {
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM Feeds WHERE Url = @url";
            command.Parameters.AddWithValue("@url", url);
            command.ExecuteNonQuery();
        }
        this.logger.LogInformation("Deleted feed {Url} from the database", url);

        this.logger.LogInformation("delete getting lock");
        this.cachedFeeds.Clear();
    }
}