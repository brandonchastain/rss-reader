using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RssApp.Contracts;
using RssApp.Data;

namespace RssReader.Server.Services
{
    /// <summary>
    /// Configuration for image and stats paths. Extracted for testability.
    /// </summary>
    /// <remarks>
    /// As of the Litestream migration, the SQLite database itself is no longer backed up by this
    /// service — Litestream replicates the DB to Azure Blob Storage. This record only retains the
    /// active DB path (used for stats snapshots) and image sync paths.
    /// </remarks>
    public record DatabaseBackupPaths(
        string ActiveDbPath = "/tmp/storage.db",
        string ActiveImagesPath = "wwwroot/images/",
        string BackupImagesPath = "/data/images/");

    /// <summary>
    /// Periodically syncs cached images between the ephemeral container filesystem (wwwroot/images)
    /// and the persistent Azure Files mount (/data/images), and records system stats snapshots.
    ///
    /// Note: this service used to also back up the SQLite database to Azure Files via SQLite's
    /// BackupDatabase() API. That responsibility now belongs entirely to Litestream, which
    /// replicates the WAL to Azure Blob Storage. The previous implementation triggered WAL
    /// checkpoints on every backup cycle, causing Litestream to capture full-DB-sized LTX deltas
    /// (~127 MB every 5 min) and bloat the cold-start restore chain.
    /// </summary>
    public class DatabaseBackupService : BackgroundService
    {
        private readonly ILogger<DatabaseBackupService> _logger;
        private readonly DatabaseBackupPaths _paths;
        private readonly IServiceProvider _serviceProvider;
        private readonly TimeSpan _syncInterval = TimeSpan.FromMinutes(5);

        public DatabaseBackupService(ILogger<DatabaseBackupService> logger)
            : this(logger, new DatabaseBackupPaths(), null)
        {
        }

        public DatabaseBackupService(ILogger<DatabaseBackupService> logger, DatabaseBackupPaths paths)
            : this(logger, paths, null)
        {
        }

        public DatabaseBackupService(
            ILogger<DatabaseBackupService> logger,
            DatabaseBackupPaths paths,
            IServiceProvider serviceProvider)
        {
            _logger = logger;
            _paths = paths;
            _serviceProvider = serviceProvider;
        }

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("DatabaseBackupService starting (image sync + stats only)...");

            // Restore cached images from the persistent /data mount on startup.
            await RestoreFromBackupAsync(cancellationToken);

            await base.StartAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("DatabaseBackupService is running. Sync interval: {Interval} minutes", _syncInterval.TotalMinutes);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_syncInterval, stoppingToken);

                    if (!stoppingToken.IsCancellationRequested)
                    {
                        await SyncImagesAsync(stoppingToken);
                        await RecordStatsSnapshotAsync();
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("DatabaseBackupService is shutting down");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during periodic sync cycle");
                    // Continue running despite errors
                }
            }
        }

        private async Task RecordStatsSnapshotAsync()
        {
            if (_serviceProvider == null) return;

            try
            {
                var statsRepo = _serviceProvider.GetService<ISystemStatsRepository>();
                if (statsRepo == null) return;

                int userCount = 0, feedCount = 0, itemCount = 0;
                long dbSizeBytes = 0;

                // Count directly from DB tables to avoid per-user repository API
                if (File.Exists(_paths.ActiveDbPath))
                {
                    dbSizeBytes = new FileInfo(_paths.ActiveDbPath).Length;

                    await Task.Run(() =>
                    {
                        using var conn = new SqliteConnection(
                            $"Data Source={_paths.ActiveDbPath};Mode=ReadOnly;Pooling=False");
                        conn.Open();

                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = "SELECT COUNT(*) FROM Users";
                            userCount = Convert.ToInt32(cmd.ExecuteScalar());
                        }
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = "SELECT COUNT(*) FROM Feeds";
                            feedCount = Convert.ToInt32(cmd.ExecuteScalar());
                        }
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = "SELECT COUNT(*) FROM Items";
                            itemCount = Convert.ToInt32(cmd.ExecuteScalar());
                        }
                    });
                }

                var snapshot = new SystemStatsSnapshot
                {
                    Timestamp   = DateTime.UtcNow,
                    UserCount   = userCount,
                    FeedCount   = feedCount,
                    ItemCount   = itemCount,
                    DbSizeBytes = dbSizeBytes
                };

                statsRepo.RecordSnapshot(snapshot);
                statsRepo.CleanupOlderThan(30);
                _logger.LogInformation(
                    "Stats snapshot recorded: {Users} users, {Feeds} feeds, {Items} items, {Db:N0} bytes",
                    userCount, feedCount, itemCount, dbSizeBytes);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to record stats snapshot");
            }
        }

        /// <summary>
        /// Restores cached images from the persistent /data mount on startup. The DB itself is
        /// restored by Litestream (run before the .NET process starts in docker-entrypoint.sh).
        /// </summary>
        public async Task RestoreFromBackupAsync(CancellationToken cancellationToken)
        {
            try
            {
                await CopyFilesAsync(_paths.BackupImagesPath, _paths.ActiveImagesPath, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to restore cached images from /data");
            }
        }

        /// <summary>
        /// Syncs new images from wwwroot/images to /data/images so they survive container recycles.
        /// </summary>
        internal async Task SyncImagesAsync(CancellationToken cancellationToken)
        {
            try
            {
                await CopyFilesAsync(_paths.ActiveImagesPath, _paths.BackupImagesPath, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to sync cached images to /data");
            }
        }

        private async Task CopyFilesAsync(string sourceDir, string destDir, CancellationToken cancellationToken)
        {
            try
            {
                if (!Directory.Exists(sourceDir))
                {
                    _logger.LogInformation("Source directory {SourceDir} does not exist. Skipping copy.", sourceDir);
                    return;
                }

                if (!Directory.Exists(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }

                var files = Directory.GetFiles(sourceDir);
                int copied = 0;
                foreach (var sourceFile in files)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    var fileName = Path.GetFileName(sourceFile);
                    var destFile = Path.Combine(destDir, fileName);

                    // Only copy new files (no overwrites)
                    if (!File.Exists(destFile))
                    {
                        using var sourceStream = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read);
                        using var destStream = new FileStream(destFile, FileMode.Create, FileAccess.Write, FileShare.None);
                        await sourceStream.CopyToAsync(destStream, 81920, cancellationToken);
                        await destStream.FlushAsync(cancellationToken);
                        copied++;
                    }
                }

                if (copied > 0)
                {
                    _logger.LogInformation("Copied {Count} new file(s) from {SourceDir} to {DestDir}", copied, sourceDir, destDir);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to copy files from {SourceDir} to {DestDir}", sourceDir, destDir);
            }
        }
    }
}
