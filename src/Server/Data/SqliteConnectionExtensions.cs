using Microsoft.Data.Sqlite;

namespace RssApp.Data;

/// <summary>
/// Controls whether new SQLite connections are opened with PRAGMA query_only = ON.
/// Set once at startup after schema initialization; never changed afterward.
/// </summary>
public static class DatabaseMode
{
    public static bool QueryOnly { get; private set; }

    /// <summary>
    /// Enable query_only mode for all subsequent connections opened via
    /// <see cref="SqliteConnectionExtensions.OpenWithPragmas"/>.
    /// Call this AFTER schema initialization (CREATE TABLE) has completed.
    /// </summary>
    public static void EnableQueryOnly()
    {
        QueryOnly = true;
    }

    /// <summary>
    /// Reset for testing only. Not intended for production use.
    /// </summary>
    internal static void ResetForTesting()
    {
        QueryOnly = false;
    }
}

public static class SqliteConnectionExtensions
{
    /// <summary>
    /// Opens the connection and applies per-connection PRAGMAs:
    /// - busy_timeout=5000 (wait up to 5s on write lock) — skipped for read-only connections
    /// - query_only=ON when <see cref="DatabaseMode.QueryOnly"/> is set
    /// </summary>
    public static void OpenWithPragmas(this SqliteConnection connection)
    {
        connection.Open();

        var isReadOnly = connection.ConnectionString?.Contains("Mode=ReadOnly", StringComparison.OrdinalIgnoreCase) == true;
        if (isReadOnly && !DatabaseMode.QueryOnly) return;

        using var cmd = connection.CreateCommand();
        if (isReadOnly && DatabaseMode.QueryOnly)
        {
            cmd.CommandText = "PRAGMA query_only = ON";
        }
        else if (DatabaseMode.QueryOnly)
        {
            cmd.CommandText = "PRAGMA busy_timeout=5000; PRAGMA query_only = ON";
        }
        else
        {
            cmd.CommandText = "PRAGMA busy_timeout=5000";
        }
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Async version of <see cref="OpenWithPragmas"/>.
    /// </summary>
    public static async Task OpenWithPragmasAsync(this SqliteConnection connection)
    {
        await connection.OpenAsync();

        var isReadOnly = connection.ConnectionString?.Contains("Mode=ReadOnly", StringComparison.OrdinalIgnoreCase) == true;
        if (isReadOnly && !DatabaseMode.QueryOnly) return;

        using var cmd = connection.CreateCommand();
        if (isReadOnly && DatabaseMode.QueryOnly)
        {
            cmd.CommandText = "PRAGMA query_only = ON";
        }
        else if (DatabaseMode.QueryOnly)
        {
            cmd.CommandText = "PRAGMA busy_timeout=5000; PRAGMA query_only = ON";
        }
        else
        {
            cmd.CommandText = "PRAGMA busy_timeout=5000";
        }
        await cmd.ExecuteNonQueryAsync();
    }
}
