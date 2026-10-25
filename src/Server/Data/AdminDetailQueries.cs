using System.Globalization;
using Microsoft.Data.Sqlite;
using RssApp.Config;
using RssApp.Contracts;

namespace RssApp.Data;

/// <summary>
/// Read-only detail tables behind the admin dashboard's stat cards (users, feeds,
/// posts, database). Everything is computed from the live tables, not from history.
/// </summary>
public class AdminDetailQueries
{
    public const int PostLimit = 200;
    public static readonly string[] Sections = { "users", "feeds", "posts", "db" };

    private static readonly string[] DbTables =
        { "Users", "UserIdentities", "Feeds", "Subscriptions", "Tags", "SubscriptionTags", "Posts", "PostContent", "UserPostState", "SystemStats" };

    private readonly IDbConnections _connections;
    private readonly string _dbPath;

    public AdminDetailQueries(IDbConnections connections, RssAppConfig config)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _dbPath = config?.DbLocation ?? string.Empty;
    }

    public AdminTable Get(string section) => section?.ToLowerInvariant() switch
    {
        "users" => Users(),
        "feeds" => Feeds(),
        "posts" => Posts(),
        "db" => Database(),
        _ => null,
    };

    private AdminTable Users() => Query(
        new AdminTable { Title = "Users", Deletable = true, Columns = { "ID", "Username", "Created", "Feeds", "Posts visible" } },
        @"SELECT u.Id, u.Username, u.CreatedUtc,
                 (SELECT COUNT(*) FROM Subscriptions s WHERE s.UserId = u.Id),
                 (SELECT COUNT(*) FROM Posts p JOIN Subscriptions s ON s.FeedId = p.FeedId WHERE s.UserId = u.Id)
          FROM Users u ORDER BY u.Id");

    private AdminTable Feeds() => Query(
        new AdminTable { Title = "Feeds", Deletable = true, Columns = { "ID", "Title", "URL", "Subscribers", "Posts", "Failures", "Last fetched", "Last error" } },
        @"SELECT f.Id, COALESCE(f.Title, ''), f.Url,
                 (SELECT COUNT(*) FROM Subscriptions s WHERE s.FeedId = f.Id),
                 (SELECT COUNT(*) FROM Posts p WHERE p.FeedId = f.Id),
                 f.ConsecutiveFailures, COALESCE(f.LastFetchedUtc, 'never'), COALESCE(f.LastError, '')
          FROM Feeds f ORDER BY f.ConsecutiveFailures DESC, f.Id");

    private AdminTable Posts() => Query(
        new AdminTable
        {
            Title = "Posts",
            Deletable = true,
            Note = $"The {PostLimit} most recently first-seen posts.",
            Columns = { "ID", "Feed", "Title", "Published", "First seen" },
        },
        $@"SELECT p.Id, COALESCE(f.Title, f.Url), COALESCE(p.Title, ''), COALESCE(p.PublishDate, ''), p.FirstSeenUtc
           FROM Posts p JOIN Feeds f ON f.Id = p.FeedId
           ORDER BY p.Id DESC LIMIT {PostLimit}");

    private AdminTable Database()
    {
        var table = new AdminTable { Title = "Database", Columns = { "Item", "Rows / size" } };
        using var conn = _connections.OpenRead();
        foreach (var name in DbTables)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {name}";
            table.Rows.Add(new List<string> { name, Convert.ToInt64(cmd.ExecuteScalar()).ToString("N0", CultureInfo.InvariantCulture) + " rows" });
        }
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = _dbPath + suffix;
            table.Rows.Add(new List<string> { "File " + System.IO.Path.GetFileName(path), System.IO.File.Exists(path) ? FormatBytes(new System.IO.FileInfo(path).Length) : "absent" });
        }
        return table;
    }

    /// <summary>
    /// Deletes one user, feed or post by id; returns whether a row went. Per-user data,
    /// a feed's posts and a post's content/state all cascade from the parent row.
    /// </summary>
    public bool Delete(string section, long id)
    {
        var sql = section?.ToLowerInvariant() switch
        {
            "users" => "DELETE FROM Users WHERE Id = @id",
            "feeds" => "DELETE FROM Feeds WHERE Id = @id",
            "posts" => "DELETE FROM Posts WHERE Id = @id",
            _ => null,
        };
        if (sql == null) return false;
        using var conn = _connections.OpenWrite();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    private AdminTable Query(AdminTable table, string sql)
    {
        using var conn = _connections.OpenRead();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var row = new List<string>(reader.FieldCount);
            for (var i = 0; i < reader.FieldCount; i++)
                row.Add(Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? "");
            table.Rows.Add(row);
        }
        return table;
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1_048_576 ? $"{bytes / 1_048_576.0:F1} MB" : bytes >= 1024 ? $"{bytes / 1024.0:F1} KB" : $"{bytes} B";
}
