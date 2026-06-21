using System.Globalization;
using Microsoft.Extensions.Logging;

namespace RssApp.Data;

/// <summary>
/// SQLite-backed per-URL fetch schedule. The table is additive (CREATE TABLE IF
/// NOT EXISTS) so it requires no migration: older builds simply ignore it, and a
/// rollback leaves the existing per-user feed/item tables untouched.
/// </summary>
public class SQLiteFeedScheduleStore : IFeedScheduleStore
{
    private readonly IDbConnections connections;
    private readonly ILogger<SQLiteFeedScheduleStore> logger;

    public SQLiteFeedScheduleStore(
        IDbConnections connections,
        ILogger<SQLiteFeedScheduleStore> logger,
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
            CREATE TABLE IF NOT EXISTS FeedFetchState (
                Url TEXT PRIMARY KEY,
                LastFetchedUtc TEXT,
                NextEarliestFetchUtc TEXT,
                IntervalSeconds INTEGER
            )";
        command.ExecuteNonQuery();
    }

    public IReadOnlyDictionary<string, DateTimeOffset> GetSchedule()
    {
        var schedule = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        using var connection = this.connections.OpenRead();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT Url, NextEarliestFetchUtc FROM FeedFetchState WHERE NextEarliestFetchUtc IS NOT NULL";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var url = reader.GetString(0);
            if (reader.IsDBNull(1)) continue;
            if (DateTimeOffset.TryParse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var next))
            {
                schedule[url] = next;
            }
        }

        return schedule;
    }

    public void Record(string url, DateTimeOffset lastFetchedUtc, DateTimeOffset nextEarliestFetchUtc, TimeSpan interval)
    {
        try
        {
            using var connection = this.connections.OpenWrite();
            var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT INTO FeedFetchState (Url, LastFetchedUtc, NextEarliestFetchUtc, IntervalSeconds)
                VALUES (@url, @lastFetched, @nextEarliest, @intervalSeconds)
                ON CONFLICT(Url) DO UPDATE SET
                    LastFetchedUtc = excluded.LastFetchedUtc,
                    NextEarliestFetchUtc = excluded.NextEarliestFetchUtc,
                    IntervalSeconds = excluded.IntervalSeconds";
            command.Parameters.AddWithValue("@url", url);
            command.Parameters.AddWithValue("@lastFetched", lastFetchedUtc.ToString("o", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("@nextEarliest", nextEarliestFetchUtc.ToString("o", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("@intervalSeconds", (long)interval.TotalSeconds);
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            // Best-effort: a schedule-persistence failure must never break a refresh.
            // Worst case the in-memory schedule still drives this process's cadence.
            this.logger.LogWarning(ex, "Failed to persist feed schedule for {url}", url);
        }
    }
}
