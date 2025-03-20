using System.Data.Entity.Core.Metadata.Edm;
using System.Data.SQLite;
using Microsoft.Extensions.Caching.Memory;

namespace RssApp.Persistence;

public class SqlLitePersistedFeeds : IPersistedFeeds
{
    private readonly string connectionString;
    private readonly ILogger<SqlLitePersistedFeeds> logger;
    private List<string> cachedFeeds;

    public SqlLitePersistedFeeds(string connectionString, ILogger<SqlLitePersistedFeeds> logger)
    {
        this.connectionString = connectionString;
        this.logger = logger;
        this.cachedFeeds = new List<string>();

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
                    Url TEXT NOT NULL UNIQUE
                )";
            command.ExecuteNonQuery();
        }
    }

    public IEnumerable<string> GetFeeds()
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
            command.CommandText = "SELECT Url FROM Feeds";
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var response = reader.GetString(0);
                    this.cachedFeeds.Add(response);
                    yield return response;
                }
            }
        }
    }

    public void AddFeed(string url)
    {
        using (var connection = new SQLiteConnection(this.connectionString))
        {
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO Feeds (Url) VALUES (@url)";
            command.Parameters.AddWithValue("@url", url);
            command.ExecuteNonQuery();
        }

        this.cachedFeeds = new List<string>();
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

        this.cachedFeeds = new List<string>();
    }
}