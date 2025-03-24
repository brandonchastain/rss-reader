using System.Data.SQLite;
using RssApp.Contracts;

namespace RssApp.Persistence;

public class SQLiteUserRepository : IUserRepository
{
    private readonly string connectionString;
    private readonly ILogger<SQLiteUserRepository> logger;

    public SQLiteUserRepository(
        string connectionString,
        ILogger<SQLiteUserRepository> logger)
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
                CREATE TABLE IF NOT EXISTS Users (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Username TEXT NOT NULL UNIQUE
                )";
            command.ExecuteNonQuery();
        }
    }

    public RssUser GetUserByName(string username)
    {
        using (var connection = new SQLiteConnection(this.connectionString))
        {
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM Users WHERE Username = @username";
            command.Parameters.AddWithValue("@username", username);

            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read())
                {
                    return null;
                }
                
                var item = this.ReadItemFromResults(reader);
                return item;
            }
        }
    }

    public RssUser GetUserById(int userId)
    {
        this.logger.LogInformation($"[DATABASE] getting user by id {userId}.");
        using (var connection = new SQLiteConnection(this.connectionString))
        {
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM Users WHERE Id = @userId";
            command.Parameters.AddWithValue("@userId", userId);

            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read())
                {
                    return null;
                }
                
                var item = this.ReadItemFromResults(reader);
                return item;
            }
        }
    }

    public RssUser AddUser(string username, int? id = null)
    {
        using (var connection = new SQLiteConnection(this.connectionString))
        {
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO Users (Username";
            if (id.HasValue)
            {
                command.CommandText += ", Id";
            }
            command.CommandText += ") VALUES (@username";
            if (id.HasValue)
            {
                command.CommandText += ", @id";
                command.Parameters.AddWithValue("@id", id.Value);
            }
            command.CommandText += ")";
            command.Parameters.AddWithValue("@username", username);
            command.ExecuteNonQuery();
        }

        var user = this.GetUserByName(username);
        return user;
    }

    public IEnumerable<RssUser> GetAllUsers()
    {
        using (var connection = new SQLiteConnection(this.connectionString))
        {
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM Users";

            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var item = this.ReadItemFromResults(reader);
                    yield return item;
                }
            }
        }
    }

    private RssUser ReadItemFromResults(SQLiteDataReader reader)
    {
        var id = reader.IsDBNull(reader.GetOrdinal("Id")) ? 0 : reader.GetInt32(reader.GetOrdinal("Id"));
        var username = reader.IsDBNull(reader.GetOrdinal("Username")) ? "" : reader.GetString(reader.GetOrdinal("Username"));
        return new RssUser(username, id);
    }
}