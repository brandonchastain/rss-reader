using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RssReader.Server.Services
{
    /// <summary>
    /// Background service that handles backing up and restoring the SQLite database
    /// to/from Azure Files mounted storage. 
    /// - On startup: Restores database from backup if it exists
    /// - Periodically: Backs up active database to mounted storage
    /// </summary>
    public class DatabaseBackupService : BackgroundService
    {
        private readonly ILogger<DatabaseBackupService> _logger;
        private readonly TimeSpan _backupInterval = TimeSpan.FromMinutes(5);
        private const string ActiveDbPath = "/tmp/storage.db";
        private const string BackupDbPath = "/data/storage.db";
        private const string ActiveImagesPath = "wwwroot/images/";
        private const string BackupImagesPath = "/data/images/";
        private string _lastBackupHash = string.Empty; // Track last written hash to avoid redundant writes

        public DatabaseBackupService(ILogger<DatabaseBackupService> logger)
        {
            _logger = logger;
        }

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("DatabaseBackupService starting...");
            
            // Restore from backup on startup
            await RestoreFromBackupAsync(cancellationToken);
            
            await base.StartAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("DatabaseBackupService is running. Backup interval: {Interval} minutes", _backupInterval.TotalMinutes);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_backupInterval, stoppingToken);
                    
                    if (!stoppingToken.IsCancellationRequested)
                    {
                        await BackupToStorageAsync(stoppingToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected when shutting down
                    _logger.LogInformation("DatabaseBackupService is shutting down");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during database backup cycle");
                    // Continue running despite errors
                }
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("DatabaseBackupService stopping. Performing final backup...");
            
            try
            {
                // Final backup before shutdown
                await BackupToStorageAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during final backup on shutdown");
            }
            
            await base.StopAsync(cancellationToken);
        }

        public async Task RestoreFromBackupAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Check if backup exists
                if (!File.Exists(BackupDbPath))
                {
                    _logger.LogInformation("No backup database found at {BackupPath}. Starting with empty database", BackupDbPath);
                    return;
                }

                // Check if active database already exists (shouldn't happen on container start, but be safe)
                if (File.Exists(ActiveDbPath))
                {
                    _logger.LogWarning("Active database already exists at {ActivePath}. Skipping restore", ActiveDbPath);
                    return;
                }

                // Ensure /tmp directory exists
                var activeDir = Path.GetDirectoryName(ActiveDbPath);
                if (!string.IsNullOrEmpty(activeDir) && !Directory.Exists(activeDir))
                {
                    Directory.CreateDirectory(activeDir);
                }

                _logger.LogInformation("Restoring database from {BackupPath} to {ActivePath}", BackupDbPath, ActiveDbPath);
                
                // Simple file copy on startup is safe since nothing is using the database yet
                await CopyFileAsync(BackupDbPath, ActiveDbPath, cancellationToken);
                
                // Compute hash of the backup so we can detect changes later
                _lastBackupHash = await ComputeFileHashAsync(BackupDbPath, cancellationToken);
                _logger.LogInformation("Computed baseline backup hash for change detection");
                
                // Restore image files
                await CopyFilesAsync(BackupImagesPath, ActiveImagesPath, cancellationToken);
                
                var fileInfo = new FileInfo(ActiveDbPath);
                _logger.LogInformation("Database restored successfully. Size: {Size:N0} bytes", fileInfo.Length);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to restore database from backup. Starting with empty database");
            }
        }

        private async Task BackupToStorageAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Check if active database exists
                if (!File.Exists(ActiveDbPath))
                {
                    _logger.LogWarning("No active database found at {ActivePath}. Skipping backup", ActiveDbPath);
                    return;
                }

                // Ensure backup directory exists
                var backupDir = Path.GetDirectoryName(BackupDbPath);
                if (!string.IsNullOrEmpty(backupDir) && !Directory.Exists(backupDir))
                {
                    Directory.CreateDirectory(backupDir);
                    _logger.LogInformation("Created backup directory: {BackupDir}", backupDir);
                }

                _logger.LogInformation("Backing up database from {ActivePath} to {BackupPath}", ActiveDbPath, BackupDbPath);
                
                // Create backup in /tmp first (SQLite backup API doesn't work well with network filesystems)
                var tempBackupPath = "/tmp/storage-backup.db";
                
                // Delete any existing temp backup file
                if (File.Exists(tempBackupPath))
                {
                    File.Delete(tempBackupPath);
                }
                
                try
                {
                    // Use SQLite's native backup API to create consistent backup in /tmp
                    await PerformSqliteBackupAsync(ActiveDbPath, tempBackupPath, cancellationToken);
                    
                    // Compute hash of the backup to detect changes
                    var backupHash = await ComputeFileHashAsync(tempBackupPath, cancellationToken);
                    
                    // Only copy to Azure Files if content has changed
                    if (backupHash != _lastBackupHash)
                    {
                        _logger.LogInformation("Database content changed. Uploading to Azure Files...");
                        await CopyFileAsync(tempBackupPath, BackupDbPath, cancellationToken);
                        _lastBackupHash = backupHash;
                        
                        var fileInfo = new FileInfo(BackupDbPath);
                        _logger.LogInformation("Database backed up successfully. Size: {Size:N0} bytes", fileInfo.Length);
                    }
                    else
                    {
                        _logger.LogInformation("Database content unchanged. Skipping upload to Azure Files (saving transaction costs)");
                    }
                    
                    // Backup image files
                    await CopyFilesAsync(ActiveImagesPath, BackupImagesPath, cancellationToken);
                }
                finally
                {
                    // Always clean up temp backup file, even if an error occurred
                    if (File.Exists(tempBackupPath))
                    {
                        File.Delete(tempBackupPath);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to backup database to storage");
            }
        }

        /// <summary>
        /// Computes SHA256 hash of a file to detect content changes
        /// </summary>
        private async Task<string> ComputeFileHashAsync(string filePath, CancellationToken cancellationToken)
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hashBytes = await SHA256.HashDataAsync(stream, cancellationToken);
            return Convert.ToHexString(hashBytes);
        }

        /// <summary>
        /// Uses SQLite's native backup API to create a consistent backup while the database is in use.
        /// This is much safer than copying files directly and prevents database corruption.
        /// </summary>
        private async Task PerformSqliteBackupAsync(string sourceDbPath, string destDbPath, CancellationToken cancellationToken)
        {
            await Task.Run(() =>
            {
                using var source = new SqliteConnection($"Data Source={sourceDbPath};Mode=ReadWriteCreate;Cache=Shared;Pooling=True");
                using var destination = new SqliteConnection($"Data Source={destDbPath};Mode=ReadWriteCreate");
                
                source.Open();
                destination.Open();
                
                // SQLite's backup API creates a consistent point-in-time snapshot
                // even while other connections are reading/writing
                source.BackupDatabase(destination);
                
            }, cancellationToken);
        }

        private async Task CopyFileAsync(string sourcePath, string destPath, CancellationToken cancellationToken)
        {
            // Use buffered copy for better performance
            using var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var destStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
            
            await sourceStream.CopyToAsync(destStream, 81920, cancellationToken); // 80KB buffer
            await destStream.FlushAsync(cancellationToken);
        }

        private async Task CopyFilesAsync(string sourceDir, string destDir, CancellationToken cancellationToken)
        {
            try
            {
                // Check if source directory exists
                if (!Directory.Exists(sourceDir))
                {
                    _logger.LogInformation("Source directory {SourceDir} does not exist. Skipping copy.", sourceDir);
                    return;
                }

                // Ensure destination directory exists
                if (!Directory.Exists(destDir))
                {
                    Directory.CreateDirectory(destDir);
                    _logger.LogInformation("Created destination directory: {DestDir}", destDir);
                }

                // Get all files in source directory
                var sourceFiles = Directory.GetFiles(sourceDir);
                
                if (sourceFiles.Length == 0)
                {
                    _logger.LogInformation("No files found in {SourceDir}", sourceDir);
                    return;
                }

                // Get existing files in destination to avoid re-copying static files
                var existingDestFiles = new HashSet<string>(
                    Directory.GetFiles(destDir).Select(Path.GetFileName),
                    StringComparer.OrdinalIgnoreCase
                );

                var filesToCopy = sourceFiles
                    .Where(f => !existingDestFiles.Contains(Path.GetFileName(f)))
                    .ToList();

                if (filesToCopy.Count == 0)
                {
                    _logger.LogInformation("All {FileCount} files already backed up in {DestDir}. Skipping (saving transaction costs)", sourceFiles.Length, destDir);
                    return;
                }

                _logger.LogInformation("Copying {NewCount} new files from {SourceDir} to {DestDir} ({SkippedCount} already backed up)", 
                    filesToCopy.Count, sourceDir, destDir, sourceFiles.Length - filesToCopy.Count);

                // Copy only new files
                foreach (var sourceFile in filesToCopy)
                {
                    var fileName = Path.GetFileName(sourceFile);
                    var destFile = Path.Combine(destDir, fileName);
                    
                    await CopyFileAsync(sourceFile, destFile, cancellationToken);
                }

                _logger.LogInformation("Successfully copied {FileCount} new files", filesToCopy.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to copy files from {SourceDir} to {DestDir}", sourceDir, destDir);
            }
        }
    }
}
