using System.Globalization;
using Microsoft.Data.Sqlite;
using RssApp.Config;
using RssApp.Data;

namespace RssReader.Server.Services;

/// <summary>
/// Nightly retention for the shared post store (DB-REDESIGN-SPEC.md section 4).
/// Posts are never deleted on a read path; this is the one place they go:
/// unsaved posts older than the configured window, and feeds that have had no
/// subscriber for the grace period. Deletes run in small batches so the
/// single-writer lock is never held for long. Runs only while a replica is up.
/// </summary>
public class PostRetentionService : BackgroundService
{
    private const int BatchSize = 500;

    private readonly IDbConnections connections;
    private readonly RssAppConfig config;
    private readonly ILogger<PostRetentionService> logger;

    private readonly RssReader.Server.Services.WriterState? writerState;

    public PostRetentionService(IDbConnections connections, RssAppConfig config, ILogger<PostRetentionService> logger,
        RssReader.Server.Services.WriterState? writerState = null)
    {
        this.connections = connections;
        this.config = config;
        this.logger = logger;
        this.writerState = writerState;
    }

    protected override async Task ExecuteAsync(CancellationToken hostStopping)
    {
        // Also stops at a cooperative handoff (WriterState.WritersStopping).
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            hostStopping, this.writerState?.WritersStopping ?? CancellationToken.None);
        var stoppingToken = linked.Token;
        try
        {
            await Task.Delay(this.config.RetentionStartupDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var result = RunOnce(DateTimeOffset.UtcNow);
                    this.logger.LogInformation(
                        "Retention: removed {Posts} posts older than {Days} days and {Feeds} orphan feeds.",
                        result.PostsDeleted, this.config.PostRetentionDays, result.FeedsDeleted);
                }
                catch (Exception ex)
                {
                    this.logger.LogError(ex, "Retention pass failed; will retry after the interval.");
                }

                await Task.Delay(this.config.RetentionInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    public sealed record RetentionResult(int PostsDeleted, int FeedsDeleted);

    /// <summary>One pass, synchronous so tests can drive it with a chosen clock.</summary>
    public RetentionResult RunOnce(DateTimeOffset now)
    {
        using var connection = this.connections.OpenWrite();
        var postsDeleted = this.config.PostRetentionDays > 0
            ? DeletePosts(connection, now - TimeSpan.FromDays(this.config.PostRetentionDays))
            : 0;
        var feedsDeleted = DeleteOrphanFeeds(connection, now - TimeSpan.FromDays(this.config.FeedOrphanGraceDays));
        return new RetentionResult(postsDeleted, feedsDeleted);
    }

    // Age is the time the post entered the store (FirstSeenUtc), not its
    // publish date: migrated posts all start the clock at the migration, and a
    // feed that republishes old archive entries is not swept immediately.
    private static int DeletePosts(SqliteConnection connection, DateTimeOffset cutoff)
    {
        var total = 0;
        while (true)
        {
            using var transaction = connection.BeginTransaction();
            var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
                DELETE FROM Posts WHERE Id IN (
                    SELECT p.Id FROM Posts p
                    WHERE p.FirstSeenUtc < @cutoff
                      AND NOT EXISTS (SELECT 1 FROM UserPostState s WHERE s.PostId = p.Id AND s.IsSaved = 1)
                    LIMIT @batch)
                """;
            cmd.Parameters.AddWithValue("@cutoff", cutoff.ToString("o", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("@batch", BatchSize);
            var deleted = cmd.ExecuteNonQuery();
            transaction.Commit();
            total += deleted;
            if (deleted < BatchSize) return total;
        }
    }

    // A feed with no subscribers is not fetched (GetFeedFetchStates skips it),
    // so LastFetchedUtc stops moving when the last subscriber leaves; that, or
    // CreatedUtc for a feed never fetched, dates the orphan. Feeds holding a
    // saved post are kept so the saved post stays reachable.
    private static int DeleteOrphanFeeds(SqliteConnection connection, DateTimeOffset cutoff)
    {
        using var transaction = connection.BeginTransaction();
        var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            DELETE FROM Feeds WHERE Id IN (
                SELECT f.Id FROM Feeds f
                WHERE NOT EXISTS (SELECT 1 FROM Subscriptions s WHERE s.FeedId = f.Id)
                  AND COALESCE(f.LastFetchedUtc, f.CreatedUtc) < @cutoff
                  AND NOT EXISTS (
                        SELECT 1 FROM Posts p JOIN UserPostState s ON s.PostId = p.Id
                        WHERE p.FeedId = f.Id AND s.IsSaved = 1))
            """;
        cmd.Parameters.AddWithValue("@cutoff", cutoff.ToString("o", CultureInfo.InvariantCulture));
        var deleted = cmd.ExecuteNonQuery();
        transaction.Commit();
        return deleted;
    }
}
