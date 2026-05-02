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

/// <summary>
/// Sets per-connection PRAGMAs that must be applied to every pooled connection.
/// With Pooling=True, new physical connections start with default PRAGMAs.
/// Calling this after every Open() ensures consistent behavior.
/// </summary>
internal static class SqliteConnectionExtensions
{
    private const string ReadPragmas = """
        PRAGMA busy_timeout = 5000;
        PRAGMA cache_size = -20000;
        PRAGMA mmap_size = 268435456;
    """;

    private const string WritePragmas = """
        PRAGMA busy_timeout = 5000;
        PRAGMA synchronous = NORMAL;
        PRAGMA cache_size = -20000;
        PRAGMA temp_store = MEMORY;
        PRAGMA mmap_size = 268435456;
    """;

    public static void OpenWithReadPragmas(this SqliteConnection connection)
    {
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = ReadPragmas;
        cmd.ExecuteNonQuery();
        ApplyQueryOnly(connection);
    }

    public static async Task OpenWithReadPragmasAsync(this SqliteConnection connection)
    {
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = ReadPragmas;
        await cmd.ExecuteNonQueryAsync();
        await ApplyQueryOnlyAsync(connection);
    }

    public static void OpenWithWritePragmas(this SqliteConnection connection)
    {
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = WritePragmas;
        cmd.ExecuteNonQuery();
        // DB-level backstop: if DatabaseMode.QueryOnly is set (read-replica mode),
        // reject writes even on connections that were opened with write intent.
        ApplyQueryOnly(connection);
    }

    public static async Task OpenWithWritePragmasAsync(this SqliteConnection connection)
    {
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = WritePragmas;
        await cmd.ExecuteNonQueryAsync();
        // DB-level backstop: if DatabaseMode.QueryOnly is set (read-replica mode),
        // reject writes even on connections that were opened with write intent.
        await ApplyQueryOnlyAsync(connection);
    }

    /// <summary>
    /// Opens the connection and applies per-connection PRAGMAs.
    /// Delegates to <see cref="OpenWithReadPragmas"/> or <see cref="OpenWithWritePragmas"/>
    /// based on whether the connection string contains Mode=ReadOnly.
    /// <para>
    /// Used by services that receive a pre-chosen connection string (e.g. SQLiteSystemStatsRepository)
    /// and don't explicitly call the read/write variants. Both variants already enforce
    /// <see cref="DatabaseMode.QueryOnly"/> as a DB-level backstop.
    /// </para>
    /// </summary>
    public static void OpenWithPragmas(this SqliteConnection connection)
    {
        var isReadOnly = connection.ConnectionString?.Contains("Mode=ReadOnly", StringComparison.OrdinalIgnoreCase) == true;
        if (isReadOnly)
            connection.OpenWithReadPragmas();
        else
            connection.OpenWithWritePragmas();
    }

    /// <summary>
    /// Async version of <see cref="OpenWithPragmas"/>.
    /// </summary>
    public static async Task OpenWithPragmasAsync(this SqliteConnection connection)
    {
        var isReadOnly = connection.ConnectionString?.Contains("Mode=ReadOnly", StringComparison.OrdinalIgnoreCase) == true;
        if (isReadOnly)
            await connection.OpenWithReadPragmasAsync();
        else
            await connection.OpenWithWritePragmasAsync();
    }

    private static void ApplyQueryOnly(SqliteConnection connection)
    {
        if (!DatabaseMode.QueryOnly) return;
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA query_only = ON";
        cmd.ExecuteNonQuery();
    }

    private static async Task ApplyQueryOnlyAsync(SqliteConnection connection)
    {
        if (!DatabaseMode.QueryOnly) return;
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA query_only = ON";
        await cmd.ExecuteNonQueryAsync();
    }
}
