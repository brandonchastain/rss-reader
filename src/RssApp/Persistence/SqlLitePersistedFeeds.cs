using System.Data.SQLite;

namespace RssApp.Persistence;

public class SqlLitePersistedFeeds : IPersistedFeeds
{
    private readonly string connectionString;
    private readonly ILogger<SqlLitePersistedFeeds> logger;

    public SqlLitePersistedFeeds(string connectionString, ILogger<SqlLitePersistedFeeds> logger)
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
                    Url TEXT NOT NULL UNIQUE
                )";
            command.ExecuteNonQuery();
        }
    }

    public IEnumerable<string> GetFeeds()
    {
        using (var connection = new SQLiteConnection(this.connectionString))
        {
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = "SELECT Url FROM Feeds";
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    yield return reader.GetString(0);
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
    }
}