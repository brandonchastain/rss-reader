using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using RssApp.Contracts;
using Server.Authentication;

namespace RssApp.Data;

public class SQLiteUserRepository : IUserRepository
{
    private readonly IDbConnections connections;
    private readonly ILogger<SQLiteUserRepository> logger;

    public SQLiteUserRepository(
        IDbConnections connections,
        ILogger<SQLiteUserRepository> logger,
        bool isReadOnly = false)
    {
        this.connections = connections;
        this.logger = logger;
        if (!isReadOnly)
        {
            using var connection = this.connections.OpenWrite();
            DatabaseSchema.EnsureCreated(connection);
        }
    }

    public RssUser GetUserByName(string username)
        => QueryOne("SELECT Id, Username FROM Users WHERE Username = @p", username);

    public RssUser GetUserById(int userId)
        => QueryOne("SELECT Id, Username FROM Users WHERE Id = @p", userId);

    public RssUser FindByIdentity(string provider, string subject)
    {
        using var connection = this.connections.OpenRead();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT u.Id, u.Username FROM UserIdentities i
            JOIN Users u ON u.Id = i.UserId
            WHERE i.Provider = @provider AND i.Subject = @subject
            """;
        command.Parameters.AddWithValue("@provider", provider);
        command.Parameters.AddWithValue("@subject", subject);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var user = new RssUser(reader.GetString(1), reader.GetInt32(0));
        reader.Close();
        AttachIdentities(connection, user);
        return user;
    }

    public void LinkIdentity(int userId, string provider, string subject)
    {
        using var connection = this.connections.OpenWrite();
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO UserIdentities (Provider, Subject, UserId)
            VALUES (@provider, @subject, @userId)
            ON CONFLICT(Provider, Subject) DO NOTHING
            """;
        command.Parameters.AddWithValue("@provider", provider);
        command.Parameters.AddWithValue("@subject", subject);
        command.Parameters.AddWithValue("@userId", userId);
        command.ExecuteNonQuery();
    }

    public void UpdateUsername(int userId, string newUsername)
    {
        using var connection = this.connections.OpenWrite();
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE Users SET Username = @newUsername WHERE Id = @userId";
        command.Parameters.AddWithValue("@newUsername", newUsername);
        command.Parameters.AddWithValue("@userId", userId);
        command.ExecuteNonQuery();
    }

    public RssUser AddUser(string username, int? id = null)
    {
        using (var connection = this.connections.OpenWrite())
        {
            var command = connection.CreateCommand();
            command.CommandText = id.HasValue
                ? "INSERT INTO Users (Id, Username, CreatedUtc) VALUES (@id, @username, @now)"
                : "INSERT INTO Users (Username, CreatedUtc) VALUES (@username, @now)";
            if (id.HasValue) command.Parameters.AddWithValue("@id", id.Value);
            command.Parameters.AddWithValue("@username", username);
            command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }

        return this.GetUserByName(username);
    }

    public IEnumerable<RssUser> GetAllUsers()
    {
        var users = new List<RssUser>();
        using var connection = this.connections.OpenRead();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Username FROM Users ORDER BY Id";
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                users.Add(new RssUser(reader.GetString(1), reader.GetInt32(0)));
            }
        }

        foreach (var user in users) AttachIdentities(connection, user);
        return users;
    }

    // Everything per-user (identities, subscriptions, tags, read/saved state)
    // cascades from the Users row. Shared feeds and posts are untouched.
    public void DeleteUser(int userId)
    {
        using var connection = this.connections.OpenWrite();
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Users WHERE Id = @userId";
        command.Parameters.AddWithValue("@userId", userId);
        command.ExecuteNonQuery();
    }

    private RssUser QueryOne(string sql, object parameter)
    {
        using var connection = this.connections.OpenRead();
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@p", parameter);
        RssUser user;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) return null;
            user = new RssUser(reader.GetString(1), reader.GetInt32(0));
        }

        AttachIdentities(connection, user);
        return user;
    }

    private static void AttachIdentities(SqliteConnection connection, RssUser user)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT Provider, Subject FROM UserIdentities WHERE UserId = @userId ORDER BY rowid";
        command.Parameters.AddWithValue("@userId", user.Id);
        var identities = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var provider = reader.GetString(0);
            var subject = reader.GetString(1);
            identities.Add(UserIdentity.FormatIdentity(provider, subject));
            if (user.AadUserId == null && provider == UserIdentity.ProviderMicrosoft)
                user.AadUserId = subject;
            if (user.ProviderUserId == null && provider != UserIdentity.ProviderEmailLink)
                user.ProviderUserId = UserIdentity.FormatIdentity(provider, subject);
        }

        user.Identities = identities;
    }
}
