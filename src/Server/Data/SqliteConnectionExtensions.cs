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
    /// Opens the connection and applies PRAGMA query_only = ON when
    /// <see cref="DatabaseMode.QueryOnly"/> is set. This is the DB-level
    /// backstop that prevents writes even if the HTTP filter is bypassed.
    /// </summary>
    public static void OpenWithPragmas(this SqliteConnection connection)
    {
        connection.Open();

        if (DatabaseMode.QueryOnly)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA query_only = ON";
            cmd.ExecuteNonQuery();
        }
    }
}
