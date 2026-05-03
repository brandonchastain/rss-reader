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
    ///   - The SQLite backup is staged to local disk (next to <c>ActiveDbPath</c>); Azure Files
    ///     SMB does not reliably support the file-locking semantics SQLite needs, so backing up
    ///     directly to the share fails with <c>SQLITE_BUSY</c>. After staging we plain-file-copy
    ///     to a unique temp on the share and atomically rename to <c>BackupDbPath</c>.
    ///   - Runs <c>PRAGMA quick_check</c> on the staged file before publishing.
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
            // Stage the SQLite backup on the LOCAL filesystem (next to ActiveDbPath in /tmp).
            // SQLite's BackupDatabase API requires file-locking semantics that Azure Files
            // SMB does not reliably support; running the backup directly against the SMB
            // mount produces SQLITE_BUSY ("database is locked") errors. We back up to local
            // disk first and then plain-file-copy to a unique SMB temp before atomically
            // renaming to BackupDbPath. (Same approach as the legacy DatabaseBackupService.)
            var stagePath = $"{_paths.ActiveDbPath}.bk-stage.{Guid.NewGuid():N}";
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

                await BackupSqliteAsync(_paths.ActiveDbPath, stagePath, cancellationToken);

                var stageExists = File.Exists(stagePath);
                var stageSize = stageExists ? new FileInfo(stagePath).Length : -1;
                var (qcOk, qcResult, qcError) = QuickCheckDiagnostic(stagePath);
                if (!qcOk)
                {
                    _logger.LogError(
                        "PRAGMA quick_check failed on staged backup '{Stage}' (exists={Exists}, size={Size}, result='{Result}', error='{Error}') — discarding.",
                        stagePath, stageExists, stageSize, qcResult ?? "<null>", qcError ?? "<none>");
                    return false;
                }

                // Plain-file-copy to a unique temp on the SMB share. This involves no SQLite
                // file locking on /data, only sequential bytes-on-the-wire writes.
                using (var src = new FileStream(stagePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var dst = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await src.CopyToAsync(dst, cancellationToken);
                }

                // Atomic rename within the same SMB directory. File.Move with overwrite=true
                // uses rename(2) on Linux and is atomic on Azure Files SMB.
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
                // Local stage cleanup. SQLite ran the destination connection in WAL mode
                // briefly during backup; sweep any sidecars too.
                SafeDelete(stagePath);
                SafeDelete(stagePath + "-wal");
                SafeDelete(stagePath + "-shm");
                // SMB temp cleanup. No-op if the rename succeeded.
                SafeDelete(tempPath);
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
                // Use VACUUM INTO instead of the BackupDatabase API. VACUUM INTO produces a
                // single, clean, fully-checkpointed DB file in DELETE journal mode with NO
                // -wal/-shm sidecars. This avoids the corruption observed in production where
                // BackupDatabase produced a WAL-mode dest, the post-backup PRAGMA journal_mode
                // change failed to fully checkpoint pages into the main file, and our
                // subsequent SafeDelete of -wal then orphaned uncommitted pages — making the
                // staged file fail PRAGMA quick_check on every cycle.
                using var src = new SqliteConnection(
                    $"Data Source={sourcePath};Mode=ReadOnly;Pooling=False");
                src.Open();
                ApplyBusyTimeout(src);
                using var cmd = src.CreateCommand();
                // VACUUM INTO requires a string-literal path; parameter binding is not
                // supported by SQLite for this statement. Escape single quotes by doubling.
                var escaped = destPath.Replace("'", "''");
                cmd.CommandText = $"VACUUM INTO '{escaped}'";
                cmd.ExecuteNonQuery();
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
            return QuickCheckDiagnostic(path).Ok;
        }

        private static (bool Ok, string? Result, string? Error) QuickCheckDiagnostic(string path)
        {
            try
            {
                using var conn = new SqliteConnection(
                    $"Data Source={path};Mode=ReadOnly;Pooling=False");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA quick_check";
                var result = cmd.ExecuteScalar() as string;
                var ok = string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase);
                return (ok, result, null);
            }
            catch (Exception ex)
            {
                return (false, null, ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void SafeDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
