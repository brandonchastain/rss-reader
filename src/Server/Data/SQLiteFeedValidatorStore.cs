using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace RssApp.Data;

/// <summary>
/// SQLite-backed store for feed conditional-GET validators (ETag / Last-Modified),
/// keyed by feed URL. Writes go through the shared single-writer connection; reads
/// are cheap primary-key lookups. The <see cref="FeedRefresher"/> keeps an in-memory
/// cache in front of this, so the DB is only touched on a cache miss (e.g. the first
/// fetch of each feed after a restart) and on a successful fetch that yields new
/// validators.
/// </summary>
public class SQLiteFeedValidatorStore : IFeedValidatorStore
{
    private readonly IDbConnections connections;
    private readonly ILogger<SQLiteFeedValidatorStore> logger;

    public SQLiteFeedValidatorStore(
        IDbConnections connections,
        ILogger<SQLiteFeedValidatorStore> logger,
        bool isReadOnly = false)
    {
        this.connections = connections;
        this.logger = logger;
        if (!isReadOnly) this.InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        using var connection = this.connections.OpenWrite();
        var command = connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS FeedValidators (
                Url TEXT PRIMARY KEY,
                ETag TEXT DEFAULT NULL,
                LastModified TEXT DEFAULT NULL,
                UpdatedUtc TEXT NOT NULL
            )";
        command.ExecuteNonQuery();
    }

    public FeedValidator Get(string url)
    {
        using var connection = this.connections.OpenRead();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT ETag, LastModified FROM FeedValidators WHERE Url = @url";
        command.Parameters.AddWithValue("@url", url);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var etag = reader.IsDBNull(0) ? null : reader.GetString(0);
        DateTimeOffset? lastModified = null;
        if (!reader.IsDBNull(1))
        {
            var raw = reader.GetString(1);
            if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            {
                lastModified = parsed;
            }
        }

        if (etag == null && lastModified == null)
        {
            return null;
        }

        return new FeedValidator(etag, lastModified);
    }

    public void Set(string url, string etag, DateTimeOffset? lastModified)
    {
        try
        {
            using var connection = this.connections.OpenWrite();
            var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT INTO FeedValidators (Url, ETag, LastModified, UpdatedUtc)
                VALUES (@url, @etag, @lastModified, @updatedUtc)
                ON CONFLICT(Url) DO UPDATE SET
                    ETag = excluded.ETag,
                    LastModified = excluded.LastModified,
                    UpdatedUtc = excluded.UpdatedUtc";
            command.Parameters.AddWithValue("@url", url);
            command.Parameters.AddWithValue("@etag", (object)etag ?? DBNull.Value);
            command.Parameters.AddWithValue("@lastModified",
                lastModified.HasValue
                    ? lastModified.Value.ToString("o", CultureInfo.InvariantCulture)
                    : (object)DBNull.Value);
            command.Parameters.AddWithValue("@updatedUtc",
                DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            // Validator persistence is a best-effort optimization — never let a write
            // failure break a refresh. Worst case we fall back to a non-conditional fetch.
            this.logger.LogWarning(ex, "Failed to persist feed validators for {url}", url);
        }
    }
}
