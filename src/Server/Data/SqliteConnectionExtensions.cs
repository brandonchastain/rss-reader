using Microsoft.Data.Sqlite;

namespace RssApp.Data;

/// <summary>
/// Sets per-connection PRAGMAs that must be applied to every pooled connection.
/// With Pooling=True, new physical connections start with default PRAGMAs.
/// Calling this after every Open() ensures consistent behavior.
/// Used internally by <see cref="SqliteDbConnections"/>.
/// </summary>
internal static class SqliteConnectionExtensions
{
    private const string ReadPragmas = """
        PRAGMA busy_timeout = 5000;
        PRAGMA cache_size = -20000;
        PRAGMA mmap_size = 268435456;
    """;

    // foreign_keys is per connection; the schema relies on ON DELETE CASCADE
    // (delete a user, a subscription or a feed and everything hanging off it
    // goes too), so every writer turns it on explicitly.
    private const string WritePragmas = """
        PRAGMA foreign_keys = ON;
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
    }

    public static async Task OpenWithReadPragmasAsync(this SqliteConnection connection)
    {
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = ReadPragmas;
        await cmd.ExecuteNonQueryAsync();
    }

    public static void OpenWithWritePragmas(this SqliteConnection connection)
    {
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = WritePragmas;
        cmd.ExecuteNonQuery();
    }

    public static async Task OpenWithWritePragmasAsync(this SqliteConnection connection)
    {
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = WritePragmas;
        await cmd.ExecuteNonQueryAsync();
    }
}
