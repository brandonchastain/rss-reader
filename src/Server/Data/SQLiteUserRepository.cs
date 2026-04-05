using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using RssApp.Contracts;

namespace RssApp.Data;

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
        using (var connection = new SqliteConnection(this.connectionString))
        {
            connection.Open();
            
            // Enable WAL mode for better concurrency on network file systems
            var pragmaCommand = connection.CreateCommand();
            pragmaCommand.CommandText = @"
                PRAGMA journal_mode = WAL;
                PRAGMA busy_timeout = 5000;
                PRAGMA synchronous = NORMAL;";
            pragmaCommand.ExecuteNonQuery();
            
            var command = connection.CreateCommand();
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS Users (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Username TEXT NOT NULL UNIQUE
                )";
            command.ExecuteNonQuery();

            // Add AadUserId column if it doesn't exist (migration for existing databases)
            var alterCommand = connection.CreateCommand();
            alterCommand.CommandText = @"
                SELECT COUNT(*) FROM pragma_table_info('Users') WHERE name = 'AadUserId'";
            var hasColumn = Convert.ToInt64(alterCommand.ExecuteScalar()) > 0;
            if (!hasColumn)
            {
                var addColumn = connection.CreateCommand();
                addColumn.CommandText = "ALTER TABLE Users ADD COLUMN AadUserId TEXT";
                addColumn.ExecuteNonQuery();
                logger.LogInformation("Added AadUserId column to Users table");
            }

            // Create index on AadUserId for fast lookups
            var indexCommand = connection.CreateCommand();
            indexCommand.CommandText = @"
                CREATE UNIQUE INDEX IF NOT EXISTS IX_Users_AadUserId 
                ON Users(AadUserId) WHERE AadUserId IS NOT NULL";
            indexCommand.ExecuteNonQuery();
        }
    }

    public RssUser GetUserByName(string username)
    {
        using (var connection = new SqliteConnection(this.connectionString))
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
                
                return this.ReadItemFromResults(reader);
            }
        }
    }

    public RssUser GetUserById(int userId)
    {
        using (var connection = new SqliteConnection(this.connectionString))
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

    public RssUser GetUserByAadId(string aadUserId)
    {
        using (var connection = new SqliteConnection(this.connectionString))
        {
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM Users WHERE AadUserId = @aadUserId";
            command.Parameters.AddWithValue("@aadUserId", aadUserId);

            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read())
                {
                    return null;
                }

                return this.ReadItemFromResults(reader);
            }
        }
    }

    public void SetAadUserId(int userId, string aadUserId)
    {
        using (var connection = new SqliteConnection(this.connectionString))
        {
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = "UPDATE Users SET AadUserId = @aadUserId WHERE Id = @userId";
            command.Parameters.AddWithValue("@aadUserId", aadUserId);
            command.Parameters.AddWithValue("@userId", userId);
            command.ExecuteNonQuery();
        }
    }

    public RssUser AddUser(string username, int? id = null)
    {
        using (var connection = new SqliteConnection(this.connectionString))
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
        using (var connection = new SqliteConnection(this.connectionString))
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

    private RssUser ReadItemFromResults(SqliteDataReader reader)
    {
        var id = reader.IsDBNull(reader.GetOrdinal("Id")) ? 0 : reader.GetInt32(reader.GetOrdinal("Id"));
        var username = reader.IsDBNull(reader.GetOrdinal("Username")) ? "" : reader.GetString(reader.GetOrdinal("Username"));
        var aadOrdinal = reader.GetOrdinal("AadUserId");
        var aadUserId = reader.IsDBNull(aadOrdinal) ? null : reader.GetString(aadOrdinal);
        return new RssUser(username, id) { AadUserId = aadUserId };
    }
}