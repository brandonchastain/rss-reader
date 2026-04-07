using Microsoft.Data.Sqlite;
using RssApp.Contracts;

namespace RssApp.Data;

public class SQLiteSystemStatsRepository : ISystemStatsRepository
{
    private readonly string _connectionString;

    public SQLiteSystemStatsRepository(string connectionString)
    {
        _connectionString = connectionString;
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.OpenWithPragmas();

        var pragmaCmd = connection.CreateCommand();
        pragmaCmd.CommandText = @"
            PRAGMA journal_mode = WAL;
            PRAGMA busy_timeout = 5000;
            PRAGMA synchronous = NORMAL;";
        pragmaCmd.ExecuteNonQuery();

        var createCmd = connection.CreateCommand();
        createCmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS SystemStats (
                Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                Timestamp TEXT    NOT NULL,
                UserCount INT     NOT NULL DEFAULT 0,
                FeedCount INT     NOT NULL DEFAULT 0,
                ItemCount INT     NOT NULL DEFAULT 0,
                DbSizeBytes INT   NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS IX_SystemStats_Timestamp
                ON SystemStats(Timestamp);";
        createCmd.ExecuteNonQuery();
    }

    public void RecordSnapshot(SystemStatsSnapshot snapshot)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.OpenWithPragmas();

        var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO SystemStats (Timestamp, UserCount, FeedCount, ItemCount, DbSizeBytes)
            VALUES (@ts, @users, @feeds, @items, @dbSize)";
        cmd.Parameters.AddWithValue("@ts", snapshot.Timestamp.ToString("o"));
        cmd.Parameters.AddWithValue("@users", snapshot.UserCount);
        cmd.Parameters.AddWithValue("@feeds", snapshot.FeedCount);
        cmd.Parameters.AddWithValue("@items", snapshot.ItemCount);
        cmd.Parameters.AddWithValue("@dbSize", snapshot.DbSizeBytes);
        cmd.ExecuteNonQuery();
    }

    public SystemStatsSnapshot GetLatestSnapshot()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.OpenWithPragmas();

        var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT Id, Timestamp, UserCount, FeedCount, ItemCount, DbSizeBytes
            FROM SystemStats
            ORDER BY Timestamp DESC
            LIMIT 1";

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        return ReadSnapshot(reader);
    }

    public IEnumerable<SystemStatsSnapshot> GetHistory(int days = 30)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.OpenWithPragmas();

        var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT Id, Timestamp, UserCount, FeedCount, ItemCount, DbSizeBytes
            FROM SystemStats
            WHERE Timestamp >= @cutoff
            ORDER BY Timestamp ASC";
        cmd.Parameters.AddWithValue("@cutoff", DateTime.UtcNow.AddDays(-days).ToString("o"));

        var results = new List<SystemStatsSnapshot>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadSnapshot(reader));
        }
        return results;
    }

    public void CleanupOlderThan(int days = 30)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.OpenWithPragmas();

        var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            DELETE FROM SystemStats
            WHERE Timestamp < @cutoff";
        cmd.Parameters.AddWithValue("@cutoff", DateTime.UtcNow.AddDays(-days).ToString("o"));
        cmd.ExecuteNonQuery();
    }

    private static SystemStatsSnapshot ReadSnapshot(SqliteDataReader reader)
    {
        return new SystemStatsSnapshot
        {
            Id          = reader.GetInt64(0),
            Timestamp   = DateTime.Parse(reader.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind),
            UserCount   = reader.GetInt32(2),
            FeedCount   = reader.GetInt32(3),
            ItemCount   = reader.GetInt32(4),
            DbSizeBytes = reader.GetInt64(5)
        };
    }
}
