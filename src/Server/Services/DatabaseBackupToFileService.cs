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
        TimeSpan LockStaleThreshold,
        TimeSpan? LockHeartbeatInterval = null,
        TimeSpan? ShutdownBackupTimeout = null)
    {
        public string LockPath => BackupDbPath + ".lock";

        /// <summary>How often the held lock's mtime is refreshed. Keep well under <see cref="LockStaleThreshold"/>.</summary>
        public TimeSpan HeartbeatInterval => LockHeartbeatInterval ?? TimeSpan.FromSeconds(30);

        /// <summary>Upper bound for the final backup on shutdown. Must fit inside the container's termination grace period.</summary>
        public TimeSpan FinalBackupTimeout => ShutdownBackupTimeout ?? TimeSpan.FromSeconds(90);

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
    ///   - Holds the <see cref="ShareWriterLock"/> for the process lifetime so two writer
    ///     revisions never copy at once, and so a booting revision can wait for this one's
    ///     final copy (issue #136).
    ///   - On host shutdown (SIGTERM from a deploy or restart) <see cref="StopAsync"/> runs one
    ///     last copy, ignoring the unchanged-DB skip, before releasing the lock.
    ///   - This is SEPARATE from <see cref="DatabaseBackupService"/>, which still owns image
    ///     sync and stats and must NEVER write the DB.
    /// </summary>
    public class DatabaseBackupToFileService : BackgroundService
    {
        private readonly ILogger<DatabaseBackupToFileService> _logger;
        private readonly DatabaseBackupToFilePaths _paths;
        private readonly ShareWriterLock _lock;
        private readonly string _hostName;
        private readonly int _processId;

        public DateTimeOffset? LastSuccessAtUtc { get; private set; }

        /// <summary>Set when the final backup in <see cref="StopAsync"/> succeeded.</summary>
        public DateTimeOffset? FinalBackupAtUtc { get; private set; }

        /// <summary>Size of the most recent successful backup copy, in bytes.</summary>
        public long LastBackupBytes { get; private set; }

        /// <summary>The share lock this writer holds. Exposed for tests and the startup gate.</summary>
        public ShareWriterLock Lock => _lock;

        /// <summary>
        /// Fingerprint of the active DB (main file + WAL size and mtime) at the start of
        /// the last successful backup. When it has not moved, the DB has not been written
        /// since and the cycle is skipped: every copy is ~1 500 SMB write operations on
        /// Azure Files, which was the single largest line on the bill with a warm replica.
        /// </summary>
        private (long MainLen, long MainMtime, long WalLen, long WalMtime)? _lastBackedUpFingerprint;

        /// <summary>
        /// Copy buffer for the stage → share copy. SMB coalesces up to ~1 MB per write
        /// request, so the default 80 KB buffer cost ~13× the write operations it needed.
        /// </summary>
        private const int ShareCopyBufferBytes = 1024 * 1024;

        public bool Enabled => !string.IsNullOrEmpty(_paths.BackupDbPath);

        private readonly WriterState? _writerState;

        public DatabaseBackupToFileService(
            ILogger<DatabaseBackupToFileService> logger,
            DatabaseBackupToFilePaths paths,
            ShareWriterLock? writerLock = null,
            WriterState? writerState = null)
        {
            _logger = logger;
            _paths = paths;
            _writerState = writerState;
            _lock = writerLock ?? new ShareWriterLock(
                paths.LockPath, paths.LockStaleThreshold, paths.HeartbeatInterval, logger);
            _hostName = Environment.MachineName;
            _processId = Environment.ProcessId;
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

            // Normally the startup gate already took the lock before seeding /tmp; if
            // not (gate skipped), wait for it here so we never copy over another writer.
            // AcquireAsync gives up (false) on an unwritable directory; cycles then
            // retry a non-blocking TryAcquire each time.
            if (!_lock.IsHeld)
            {
                try { await _lock.AcquireAsync(stoppingToken); }
                catch (OperationCanceledException) { return; }
            }

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
        /// Host shutdown: the periodic loop stops, then one final copy goes to the share so
        /// a deploy loses nothing written since the last cycle. Only after that is the lock
        /// released, which is what lets the next revision seed from this copy. Bounded by
        /// <see cref="DatabaseBackupToFilePaths.FinalBackupTimeout"/> (and the host's
        /// shutdown timeout); on timeout the lock is released anyway so a dying replica
        /// cannot hold the new one up for the stale period.
        /// </summary>
        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            var sw = Stopwatch.StartNew();
            await base.StopAsync(cancellationToken);
            if (!Enabled) return;

            if (_writerState is { IsHandedOver: true } handed)
            {
                // Cooperative handoff (spec 3.2 v2): the database was synced to blob and the
                // lease given to the next revision at HandedOverAtUtc; the rollback copy to
                // the share ran right after that (RunHandoverCopyAsync). Nothing has been
                // written since, so the SIGTERM copy would be a second copy of the same bytes.
                _logger.LogWarning(
                    "Shutdown: lease handed over to {To} at {At}; skipping the final share copy (handoff copy {Copy}).",
                    handed.HandedOverTo, handed.HandedOverAtUtc?.ToString("O"),
                    FinalBackupAtUtc is { } t ? $"OK at {t:O}" : "did not complete");
                _lock.Release();
                return;
            }

            try
            {
                if (!EnsureLockOrProceedUnlocked())
                {
                    _logger.LogWarning("Shutdown: another writer holds the lock — skipping final DB backup.");
                    return;
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_paths.FinalBackupTimeout);
                _logger.LogInformation(
                    "Shutdown: running final DB backup to the share ({Ms} ms into StopAsync).",
                    sw.ElapsedMilliseconds);
                var copyStart = sw.ElapsedMilliseconds;
                var ok = await RunBackupCycleAsync(force: true, timeout.Token);
                if (ok)
                {
                    FinalBackupAtUtc = DateTimeOffset.UtcNow;
                    _logger.LogInformation(
                        "Shutdown: final DB backup OK: {Bytes:N0} bytes copied in {CopyMs} ms ({TotalMs} ms into StopAsync).",
                        LastBackupBytes, sw.ElapsedMilliseconds - copyStart, sw.ElapsedMilliseconds);
                }
                else
                {
                    _logger.LogError(
                        "Shutdown: final DB backup did not run after {Ms} ms — writes since the last cycle may be lost.",
                        sw.ElapsedMilliseconds);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogError(
                    "Shutdown: final DB backup timed out after {Timeout} ({Ms} ms into StopAsync) — writes since the last cycle may be lost.",
                    _paths.FinalBackupTimeout, sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Shutdown: final DB backup failed after {Ms} ms", sw.ElapsedMilliseconds);
            }
            finally
            {
                var wasHeld = _lock.IsHeld;
                _lock.Release();
                _logger.LogInformation(
                    "Shutdown: share writer lock released (wasHeld={WasHeld}) {Ms} ms into StopAsync.",
                    wasHeld, sw.ElapsedMilliseconds);
            }
        }

        /// <summary>
        /// The rollback copy after a cooperative handoff: the writers are stopped and the
        /// replica is synced, so this copies the final state to the share for a rollback to
        /// an image that seeds from it. Off the handover's critical path (runs after the
        /// lease is released). Bounded like the shutdown copy.
        /// </summary>
        public async Task<bool> RunHandoverCopyAsync(CancellationToken cancellationToken)
        {
            if (!Enabled) return false;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (!EnsureLockOrProceedUnlocked())
                {
                    _logger.LogWarning("Handoff: another writer holds the share lock; skipping the rollback copy.");
                    return false;
                }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_paths.FinalBackupTimeout);
                _logger.LogInformation("Handoff: copying the final database to the share for rollback.");
                var ok = await RunBackupCycleAsync(force: true, timeout.Token);
                if (ok)
                {
                    FinalBackupAtUtc = DateTimeOffset.UtcNow;
                    _logger.LogInformation("Handoff: rollback copy to the share OK in {Ms} ms.", sw.ElapsedMilliseconds);
                }
                else
                {
                    _logger.LogError("Handoff: rollback copy to the share did not run; the share is as of the last cycle.");
                }
                return ok;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogError("Handoff: rollback copy to the share timed out after {Timeout}.", _paths.FinalBackupTimeout);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Handoff: rollback copy to the share failed.");
                return false;
            }
        }

        /// <summary>
        /// Run one backup cycle. Returns true if a backup was successfully written;
        /// false if it was skipped (e.g. another writer holds the lock, or nothing changed).
        /// Exposed for tests.
        /// </summary>
        internal Task<bool> RunBackupCycleAsync(CancellationToken cancellationToken)
            => RunBackupCycleAsync(force: false, cancellationToken);

        /// <param name="force">Copy even when the active DB's fingerprint has not moved
        /// since the last successful backup (used by the final flush on shutdown).</param>
        internal async Task<bool> RunBackupCycleAsync(bool force, CancellationToken cancellationToken)
        {
            if (!Enabled) return false;

            CleanupStaleTempFiles();

            cancellationToken.ThrowIfCancellationRequested();

            // The lock is held for the process lifetime; a cycle only needs to take it
            // when the service is driven directly (tests, or the gate was skipped).
            if (!EnsureLockOrProceedUnlocked())
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

                // Snapshot the fingerprint BEFORE the backup so any write that lands while
                // VACUUM INTO runs is picked up by the next cycle rather than lost.
                var fingerprint = FingerprintActiveDb();
                if (!force && fingerprint == _lastBackedUpFingerprint && File.Exists(_paths.BackupDbPath))
                {
                    _logger.LogDebug("Active DB unchanged since last backup — skipping this cycle.");
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
                    await src.CopyToAsync(dst, ShareCopyBufferBytes, cancellationToken);
                    // Force the SMB client to push its write-back cache to the share before
                    // we trust the file. On Linux CIFS a write-back failure (e.g. the share
                    // quota filling mid-copy) can otherwise be dropped silently: Dispose
                    // returns normally and the rename below publishes a truncated file.
                    // That is exactly what took production down on 2026-10-02.
                    dst.Flush(flushToDisk: true);
                }

                // Belt and braces for the same failure: never publish a copy whose
                // on-share size differs from the validated stage file.
                if (!CopiedFileMatchesStage(stagePath, tempPath, out var expectedSize, out var actualSize))
                {
                    _logger.LogError(
                        "Share copy '{Temp}' is {Actual:N0} bytes but the staged backup is {Expected:N0} bytes — discarding. " +
                        "Check the Azure Files share quota.",
                        tempPath, actualSize, expectedSize);
                    return false;
                }

                // Atomic rename within the same SMB directory. File.Move with overwrite=true
                // uses rename(2) on Linux and is atomic on Azure Files SMB.
                File.Move(tempPath, _paths.BackupDbPath, overwrite: true);

                LastSuccessAtUtc = DateTimeOffset.UtcNow;
                _lastBackedUpFingerprint = fingerprint;
                var size = new FileInfo(_paths.BackupDbPath).Length;
                LastBackupBytes = size;
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
            }
        }

        private bool _warnedUnlocked;

        /// <summary>
        /// True when this writer may copy: it holds the lock, just took it, or the lock
        /// file cannot be created at all (self-host with an unwritable backup directory:
        /// warn once and carry on, there is no other writer to race). False only on
        /// contention, i.e. another writer's live lock.
        /// </summary>
        private bool EnsureLockOrProceedUnlocked()
        {
            if (_lock.IsHeld || _lock.TryAcquire(out var error)) return true;
            if (error == null) return false;
            if (!_warnedUnlocked)
            {
                _warnedUnlocked = true;
                _logger.LogWarning(error,
                    "Cannot create the writer lock {Lock}; running backups without it.", _lock.LockPath);
            }
            return true;
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

        /// <summary>
        /// Cheap change detector for a WAL-mode SQLite file: a commit appends to the -wal
        /// (size and mtime move); a checkpoint rewrites the main file (mtime moves) and
        /// resets the WAL. Either way at least one of the four values changes.
        /// </summary>
        private (long MainLen, long MainMtime, long WalLen, long WalMtime) FingerprintActiveDb()
        {
            var main = new FileInfo(_paths.ActiveDbPath);
            var wal = new FileInfo(_paths.ActiveDbPath + "-wal");
            return (
                main.Exists ? main.Length : -1,
                main.Exists ? main.LastWriteTimeUtc.Ticks : 0,
                wal.Exists ? wal.Length : -1,
                wal.Exists ? wal.LastWriteTimeUtc.Ticks : 0);
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

        internal static (bool Ok, string? Result, string? Error) QuickCheckDiagnostic(string path)
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

        /// <summary>
        /// True when the copy on the share is byte-for-byte the same length as the
        /// validated stage file. Exposed for tests.
        /// </summary>
        internal static bool CopiedFileMatchesStage(
            string stagePath, string copyPath, out long expectedSize, out long actualSize)
        {
            expectedSize = new FileInfo(stagePath).Length;
            var copy = new FileInfo(copyPath);
            actualSize = copy.Exists ? copy.Length : -1;
            return actualSize == expectedSize;
        }

        private static void SafeDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
