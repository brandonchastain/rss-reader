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
    // Sized for a 0.5 GiB replica. cache_size is per pooled connection (4 MB each,
    // was 20 MB), and mapped database pages count against the container's memory,
    // so mmap is 64 MB (was 256 MB); the OS page cache still serves the rest.
    private const string ReadPragmas = """
        PRAGMA busy_timeout = 5000;
        PRAGMA cache_size = -4000;
        PRAGMA mmap_size = 67108864;
    """;

    // foreign_keys is per connection; the schema relies on ON DELETE CASCADE
    // (delete a user, a subscription or a feed and everything hanging off it
    // goes too), so every writer turns it on explicitly. temp_store is FILE so a
    // VACUUM or a large sort spills to disk instead of holding a copy in memory.
    private const string WritePragmas = """
        PRAGMA foreign_keys = ON;
        PRAGMA busy_timeout = 5000;
        PRAGMA synchronous = NORMAL;
        PRAGMA cache_size = -4000;
        PRAGMA temp_store = FILE;
        PRAGMA mmap_size = 67108864;
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
