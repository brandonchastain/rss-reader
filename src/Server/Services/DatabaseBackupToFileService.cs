using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RssReader.Server.Services
{
    /// <summary>
    /// Configuration for the file-mount DB backup. Empty <see cref="BackupDbPath"/> disables
    /// the service entirely (used in local dev when /data is not mounted).
    /// </summary>
    public record DatabaseBackupToFilePaths(
        string ActiveDbPath,
        string BackupDbPath,
        TimeSpan Interval,
        TimeSpan LockStaleThreshold)
    {
        public string LockPath => BackupDbPath + ".lock";

        /// <summary>Glob-friendly prefix for per-writer temp files.</summary>
        public string TempPrefix => Path.GetFileName(BackupDbPath) + ".tmp.";
    }

    /// <summary>
    /// Periodically copies the live SQLite database from <see cref="DatabaseBackupToFilePaths.ActiveDbPath"/>
    /// (ephemeral container disk) to <see cref="DatabaseBackupToFilePaths.BackupDbPath"/> on the
    /// persistent Azure Files mount. The startup entrypoint seeds <c>/tmp</c> from this file,
    /// making cold-start independent of DB history length and replacing Litestream as the sole
    /// durability mechanism in default operating mode.
    ///
    /// Safety contract:
    ///   - Uses SQLite's online <c>BackupDatabase</c> API (no WAL checkpoint forced on source).
    ///   - Writes to a unique temp path, runs <c>PRAGMA quick_check</c>, then atomically renames.
    ///   - Holds a lock file on the share to prevent two writer revisions racing.
    ///   - This is SEPARATE from <see cref="DatabaseBackupService"/>, which still owns image
    ///     sync and stats and must NEVER write the DB.
    /// </summary>
    public class DatabaseBackupToFileService : BackgroundService
    {
        private readonly ILogger<DatabaseBackupToFileService> _logger;
        private readonly DatabaseBackupToFilePaths _paths;
        private readonly string _hostName;
        private readonly int _processId;
        private readonly string _ownerToken;

        public DateTimeOffset? LastSuccessAtUtc { get; private set; }

        public bool Enabled => !string.IsNullOrEmpty(_paths.BackupDbPath);

        public DatabaseBackupToFileService(
            ILogger<DatabaseBackupToFileService> logger,
            DatabaseBackupToFilePaths paths)
        {
            _logger = logger;
            _paths = paths;
            _hostName = Environment.MachineName;
            _processId = Environment.ProcessId;
            _ownerToken = Guid.NewGuid().ToString("N");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!Enabled)
            {
                _logger.LogInformation(
                    "DatabaseBackupToFileService disabled (BackupDbPath empty). Skipping.");
                return;
            }

            // Validate parent directory once at startup; fail fast if missing/unwritable.
            var parent = Path.GetDirectoryName(_paths.BackupDbPath);
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
            {
                throw new InvalidOperationException(
                    $"BackupDbPath parent directory does not exist: '{parent}'. " +
                    "In production this means the /data Azure Files mount is missing — " +
                    "refusing to run silently.");
            }

            _logger.LogInformation(
                "DatabaseBackupToFileService running. Active={Active}, Backup={Backup}, Interval={Interval}",
                _paths.ActiveDbPath, _paths.BackupDbPath, _paths.Interval);

            // Run an initial cycle quickly so a fresh container has a backup before
            // crashing or being recycled (rather than waiting one full interval).
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                await RunBackupCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Initial DB backup cycle failed");
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_paths.Interval, stoppingToken);
                    if (stoppingToken.IsCancellationRequested) break;
                    await RunBackupCycleAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "DatabaseBackupToFileService cycle failed");
                }
            }
        }

        /// <summary>
        /// Run one backup cycle. Returns true if a backup was successfully written;
        /// false if it was skipped (e.g. another writer holds the lock).
        /// Exposed for tests.
        /// </summary>
        internal async Task<bool> RunBackupCycleAsync(CancellationToken cancellationToken)
        {
            if (!Enabled) return false;

            CleanupStaleTempFiles();

            if (!TryAcquireLock(out var lockFs))
            {
                _logger.LogInformation(
                    "Another writer holds the backup lock — skipping this cycle.");
                return false;
            }

            var sw = Stopwatch.StartNew();
            var tempPath = $"{_paths.BackupDbPath}.tmp.{_hostName}.{_processId}.{Guid.NewGuid():N}";
            try
            {
                if (!File.Exists(_paths.ActiveDbPath))
                {
                    _logger.LogWarning(
                        "Active DB '{Path}' does not exist — refusing to overwrite backup.",
                        _paths.ActiveDbPath);
                    return false;
                }

                await BackupSqliteAsync(_paths.ActiveDbPath, tempPath, cancellationToken);

                if (!QuickCheck(tempPath))
                {
                    _logger.LogError(
                        "PRAGMA quick_check failed on temp backup '{Temp}' — discarding.",
                        tempPath);
                    SafeDelete(tempPath);
                    return false;
                }

                // Atomic rename. File.Move with overwrite=true uses rename(2) on Linux.
                File.Move(tempPath, _paths.BackupDbPath, overwrite: true);

                LastSuccessAtUtc = DateTimeOffset.UtcNow;
                var size = new FileInfo(_paths.BackupDbPath).Length;
                _logger.LogInformation(
                    "DB backup OK: {Bytes:N0} bytes in {Ms} ms (host={Host} pid={Pid}).",
                    size, sw.ElapsedMilliseconds, _hostName, _processId);
                return true;
            }
            finally
            {
                SafeDelete(tempPath); // no-op if rename succeeded
                // SQLite may leave -wal/-shm sidecars next to the temp file
                // when the destination connection ran in WAL mode. Sweep them
                // so we don't accumulate cruft in the backup directory.
                SafeDelete(tempPath + "-wal");
                SafeDelete(tempPath + "-shm");
                ReleaseLock(lockFs);
            }
        }

        /// <summary>
        /// Cleanup orphaned temp files older than 2× the lock-stale threshold.
        /// Exposed for tests.
        /// </summary>
        internal void CleanupStaleTempFiles()
        {
            try
            {
                var dir = Path.GetDirectoryName(_paths.BackupDbPath);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

                var cutoff = DateTime.UtcNow - (_paths.LockStaleThreshold + _paths.LockStaleThreshold);
                foreach (var path in Directory.EnumerateFiles(dir, _paths.TempPrefix + "*"))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(path) < cutoff)
                        {
                            File.Delete(path);
                            _logger.LogInformation("Deleted stale temp file: {Path}", path);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to cleanup temp file {Path}", path);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Stale temp cleanup failed");
            }
        }

        private bool TryAcquireLock(out FileStream? lockFs)
        {
            lockFs = null;

            try
            {
                if (File.Exists(_paths.LockPath))
                {
                    var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(_paths.LockPath);
                    if (age < _paths.LockStaleThreshold)
                    {
                        return false;
                    }
                    _logger.LogWarning(
                        "Stale backup lock found (age={Age}) — taking over.", age);
                    SafeDelete(_paths.LockPath);
                }

                lockFs = new FileStream(
                    _paths.LockPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.Read);

                using var writer = new StreamWriter(lockFs, leaveOpen: true);
                // First line is the owner token; readers compare on release to
                // avoid an old writer deleting a newer writer's lock.
                writer.WriteLine(_ownerToken);
                writer.WriteLine($"{_hostName}:{_processId}:{DateTimeOffset.UtcNow:O}");
                writer.Flush();
                return true;
            }
            catch (IOException)
            {
                // Lost the race — another container created the lock between our check and our create.
                lockFs?.Dispose();
                lockFs = null;
                return false;
            }
        }

        private void ReleaseLock(FileStream? lockFs)
        {
            try
            {
                lockFs?.Dispose();

                // Only delete if we still own it. An old/stalled writer must not
                // delete a fresh lock that a newer writer took over.
                if (File.Exists(_paths.LockPath))
                {
                    string? firstLine = null;
                    try
                    {
                        using var reader = new StreamReader(_paths.LockPath);
                        firstLine = reader.ReadLine();
                    }
                    catch (IOException) { }

                    if (firstLine == _ownerToken)
                    {
                        SafeDelete(_paths.LockPath);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Lock token mismatch on release — leaving lock for the current owner.");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to release backup lock");
            }
        }

        private static async Task BackupSqliteAsync(
            string sourcePath, string destPath, CancellationToken cancellationToken)
        {
            await Task.Run(() =>
            {
                using (var src = new SqliteConnection(
                    $"Data Source={sourcePath};Mode=ReadOnly;Pooling=False"))
                using (var dst = new SqliteConnection(
                    $"Data Source={destPath};Pooling=False"))
                {
                    src.Open();
                    ApplyBusyTimeout(src);
                    dst.Open();
                    ApplyBusyTimeout(dst);
                    src.BackupDatabase(dst);

                    // Force the destination out of WAL mode so the -wal/-shm
                    // sidecar files are checkpointed and removed when we close.
                    // Without this, every cycle leaves orphan sidecars in the
                    // backup dir because the post-backup File.Move only renames
                    // the main file.
                    using var pragma = dst.CreateCommand();
                    pragma.CommandText = "PRAGMA journal_mode=DELETE;";
                    pragma.ExecuteNonQuery();
                }

                // Belt-and-suspenders: if the SQLite provider left sidecars
                // behind despite the journal_mode change, sweep them.
                SafeDelete(destPath + "-wal");
                SafeDelete(destPath + "-shm");
            }, cancellationToken);
        }

        private static void ApplyBusyTimeout(SqliteConnection conn)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA busy_timeout = 30000;";
            cmd.ExecuteNonQuery();
        }

        private static bool QuickCheck(string path)
        {
            try
            {
                using var conn = new SqliteConnection(
                    $"Data Source={path};Mode=ReadOnly;Pooling=False");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA quick_check";
                var result = cmd.ExecuteScalar() as string;
                return string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static void SafeDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
