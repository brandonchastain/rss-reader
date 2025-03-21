using System.Data.Entity.Core.Metadata.Edm;
using System.Data.SQLite;
using Microsoft.Extensions.Caching.Memory;
using RssApp.Contracts;

namespace RssApp.Persistence;

public class SqlLitePersistedFeeds : IPersistedFeeds
{
    private readonly string connectionString;
    private readonly ILogger<SqlLitePersistedFeeds> logger;
    private List<NewsFeed> cachedFeeds;

    public SqlLitePersistedFeeds(string connectionString, ILogger<SqlLitePersistedFeeds> logger)
    {
        this.connectionString = connectionString;
        this.logger = logger;
        this.cachedFeeds = new List<NewsFeed>();

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
        if (this.cachedFeeds.Any())
        {
            foreach (var feed in this.cachedFeeds)
            {
                yield return feed;
            }

            yield break;
        }

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
                    yield return new NewsFeed(url, isPaywalled);
                }
            }
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
        this.cachedFeeds = new List<NewsFeed>();
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
        this.cachedFeeds = new List<NewsFeed>();

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

        this.cachedFeeds = new List<NewsFeed>();
    }
}