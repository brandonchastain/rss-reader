using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RssReader.Server.Services
{
    /// <summary>
    /// Image cache paths. Extracted for testability.
    /// </summary>
    /// <remarks>
    /// The SQLite database is NOT handled here: <see cref="DatabaseBackupToFileService"/>
    /// owns DB durability. <see cref="ActiveDbPath"/> is retained only so tests can
    /// assert this service never touches it.
    /// </remarks>
    public record DatabaseBackupPaths(
        string ActiveDbPath = "/tmp/storage.db",
        string ActiveImagesPath = "wwwroot/images/",
        string BackupImagesPath = "/data/images/");

    /// <summary>
    /// Restores cached favicons from the persistent Azure Files mount (/data/images)
    /// into the ephemeral container filesystem (wwwroot/images) once at startup.
    ///
    /// This used to also run a 5-minute loop that (a) stat'ed every cached image on
    /// the SMB share to find new ones to copy and (b) ran COUNT(*) stats queries.
    /// Both were pure idle-time cost on an always-on replica: new favicons are now
    /// written through to /data by <see cref="FaviconService"/> the moment they are
    /// cached, and stats are captured on demand by <see cref="SystemStatsCollector"/>.
    /// </summary>
    public class DatabaseBackupService : BackgroundService
    {
        private readonly ILogger<DatabaseBackupService> _logger;
        private readonly DatabaseBackupPaths _paths;

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
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Restore cached images from the persistent /data mount in the background.
            // This used to run in StartAsync, which blocks Kestrel from listening: hosted
            // services start before the web host, and copying ~900 files one at a time
            // over the Azure Files SMB mount took most of the app's cold-start budget.
            // Images are a best-effort cache (a miss re-fetches from the feed), so a
            // request served before the copy finishes is fine. Task.Yield returns control
            // to the host before the synchronous file copies begin.
            await Task.Yield();
            await RestoreFromBackupAsync(stoppingToken);
        }

        /// <summary>
        /// Restores cached images from the persistent /data mount on startup.
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
                        await sourceStream.CopyToAsync(destStream, 1024 * 1024, cancellationToken);
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
