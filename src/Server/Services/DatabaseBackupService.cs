using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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
                    _logger.LogWarning("Active database already exists at {ActivePath}. Deleting it.", ActiveDbPath);
                    File.Delete(ActiveDbPath);
                }

                // Ensure /tmp directory exists
                var activeDir = Path.GetDirectoryName(ActiveDbPath);
                if (!string.IsNullOrEmpty(activeDir) && !Directory.Exists(activeDir))
                {
                    Directory.CreateDirectory(activeDir);
                }

                _logger.LogInformation("Restoring database from {BackupPath} to {ActivePath}", BackupDbPath, ActiveDbPath);
                
                // Copy backup to active location
                await CopyFileAsync(BackupDbPath, ActiveDbPath, cancellationToken);
                await CopyFilesAsync(BackupImagesPath, ActiveImagesPath, cancellationToken);
                
                // Also copy WAL and SHM files if they exist
                await CopyFileIfExistsAsync($"{BackupDbPath}-wal", $"{ActiveDbPath}-wal", cancellationToken);
                await CopyFileIfExistsAsync($"{BackupDbPath}-shm", $"{ActiveDbPath}-shm", cancellationToken);
                
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
                
                // Copy active database to backup location
                await CopyFileAsync(ActiveDbPath, BackupDbPath, cancellationToken);
                await CopyFilesAsync(ActiveImagesPath, BackupImagesPath, cancellationToken);
                
                // Also backup WAL and SHM files if they exist
                await CopyFileIfExistsAsync($"{ActiveDbPath}-wal", $"{BackupDbPath}-wal", cancellationToken);
                await CopyFileIfExistsAsync($"{ActiveDbPath}-shm", $"{BackupDbPath}-shm", cancellationToken);
                
                var fileInfo = new FileInfo(ActiveDbPath);
                _logger.LogInformation("Database backed up successfully. Size: {Size:N0} bytes", fileInfo.Length);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to backup database to storage");
            }
        }

        private async Task CopyFileAsync(string sourcePath, string destPath, CancellationToken cancellationToken)
        {
            // Use buffered copy for better performance
            using var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var destStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
            
            await sourceStream.CopyToAsync(destStream, 81920, cancellationToken); // 80KB buffer
            await destStream.FlushAsync(cancellationToken);
        }

        private async Task CopyFileIfExistsAsync(string sourcePath, string destPath, CancellationToken cancellationToken)
        {
            if (File.Exists(sourcePath))
            {
                await CopyFileAsync(sourcePath, destPath, cancellationToken);
            }
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
                var files = Directory.GetFiles(sourceDir);
                
                if (files.Length == 0)
                {
                    _logger.LogInformation("No files found in {SourceDir}", sourceDir);
                    return;
                }

                _logger.LogInformation("Copying {FileCount} files from {SourceDir} to {DestDir}", files.Length, sourceDir, destDir);

                // Copy each file
                foreach (var sourceFile in files)
                {
                    var fileName = Path.GetFileName(sourceFile);
                    var destFile = Path.Combine(destDir, fileName);
                    
                    await CopyFileAsync(sourceFile, destFile, cancellationToken);
                }

                _logger.LogInformation("Successfully copied {FileCount} files", files.Length);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to copy files from {SourceDir} to {DestDir}", sourceDir, destDir);
            }
        }
    }
}
