using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using RssApp.Config;
using RssApp.Contracts;
using RssApp.Data;

namespace RssReader.Server.Services;

/// <summary>
/// Computes the admin dashboard's system stats (user, feed and item counts plus DB
/// size) on demand. A snapshot is recorded to the history table at most once per
/// <see cref="MinInterval"/>, and only when someone actually opens the dashboard.
/// This replaced a background loop that ran the same COUNT(*) queries every five
/// minutes around the clock whether or not anyone would ever look at the result.
/// </summary>
public class SystemStatsCollector
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromHours(1);

    private readonly ISystemStatsRepository _statsRepo;
    private readonly string _dbPath;
    private readonly ILogger<SystemStatsCollector> _logger;

    public SystemStatsCollector(ISystemStatsRepository statsRepo, RssAppConfig config, ILogger<SystemStatsCollector> logger)
    {
        _statsRepo = statsRepo ?? throw new ArgumentNullException(nameof(statsRepo));
        _dbPath = config?.DbLocation ?? string.Empty;
        _logger = logger;
    }

    /// <summary>
    /// Returns the latest snapshot, capturing and recording a fresh one first when
    /// the latest is older than <see cref="MinInterval"/> (or none exists).
    /// </summary>
    public async Task<SystemStatsSnapshot> GetOrCaptureAsync()
    {
        var latest = _statsRepo.GetLatestSnapshot();
        if (latest != null && DateTime.UtcNow - latest.Timestamp < MinInterval)
        {
            return latest;
        }

        try
        {
            return await CaptureNowAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to capture system stats snapshot");
            return latest ?? new SystemStatsSnapshot { Timestamp = DateTime.UtcNow };
        }
    }

    /// <summary>
    /// Captures and records a snapshot right now, ignoring <see cref="MinInterval"/>.
    /// Behind the dashboard's refresh button; the counts are cheap indexed
    /// COUNT(*)s, and the endpoint is admin-only. Throws on failure.
    /// </summary>
    public async Task<SystemStatsSnapshot> CaptureNowAsync()
    {
        var fresh = await CaptureAsync();
        _statsRepo.RecordSnapshot(fresh);
        _statsRepo.CleanupOlderThan(StatsHistoryRange.RetentionDays);
        return fresh;
    }

    private async Task<SystemStatsSnapshot> CaptureAsync()
    {
        int userCount = 0, feedCount = 0, itemCount = 0;
        long dbSizeBytes = 0;

        if (File.Exists(_dbPath))
        {
            dbSizeBytes = new FileInfo(_dbPath).Length;

            await Task.Run(() =>
            {
                using var conn = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly;Pooling=False");
                conn.Open();
                userCount = Count(conn, "Users");
                feedCount = Count(conn, "Feeds");
                itemCount = Count(conn, "Posts");
            });
        }

        return new SystemStatsSnapshot
        {
            Timestamp = DateTime.UtcNow,
            UserCount = userCount,
            FeedCount = feedCount,
            ItemCount = itemCount,
            DbSizeBytes = dbSizeBytes,
        };
    }

    private static int Count(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
