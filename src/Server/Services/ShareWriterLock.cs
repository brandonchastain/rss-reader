#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace RssReader.Server.Services
{
    /// <summary>
    /// The writer's lease on the share copy of the database (<c>/data/db/storage.db.lock</c>).
    ///
    /// A writer revision holds the lock for its whole lifetime: it is taken before the
    /// revision seeds <c>/tmp</c> from the share and released only after the final backup
    /// on shutdown (see <see cref="DatabaseBackupToFileService.StopAsync"/>). A new
    /// revision booting during a deploy waits in <see cref="AcquireAsync"/> while the old
    /// one's lock is alive, which is what guarantees it seeds from the old revision's
    /// final copy rather than a copy up to <c>BackupInterval</c> old (issue #136).
    ///
    /// Liveness is the file's mtime: the holder touches the file every
    /// <see cref="HeartbeatInterval"/>, and a lock whose mtime is older than
    /// <see cref="StaleAfter"/> belongs to a replica that died without releasing it and
    /// is taken over. That is the whole mechanism. A holder that stalls for longer than
    /// <see cref="StaleAfter"/> and then resumes could delete or write over a newer
    /// holder's lock; with a 3-minute threshold against a 30-second heartbeat that needs
    /// a live process to lose the share for minutes, which is accepted rather than
    /// guarded against with owner tokens.
    ///
    /// Self-hosting: the lock is plain file operations, so it works on any directory.
    /// If the directory cannot be written at all, <see cref="AcquireAsync"/> gives up
    /// after <see cref="GiveUpAfter"/> with a warning instead of failing boot.
    /// </summary>
    public sealed class ShareWriterLock : IDisposable
    {
        private readonly ILogger _logger;
        private readonly object _gate = new();
        private CancellationTokenSource? _heartbeatCts;

        public string LockPath { get; }
        public TimeSpan StaleAfter { get; }
        public TimeSpan HeartbeatInterval { get; }

        /// <summary>How often <see cref="AcquireAsync"/> re-checks a foreign lock.</summary>
        public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

        /// <summary>
        /// How long <see cref="AcquireAsync"/> keeps retrying when the lock file cannot be
        /// created for a reason other than another holder (unwritable or missing
        /// directory). Contention never times out: a live foreign lock is waited for.
        /// </summary>
        public TimeSpan GiveUpAfter { get; init; } = TimeSpan.FromSeconds(10);

        public bool IsHeld { get; private set; }

        public ShareWriterLock(string lockPath, TimeSpan staleAfter, TimeSpan heartbeatInterval, ILogger logger)
        {
            LockPath = lockPath;
            StaleAfter = staleAfter;
            HeartbeatInterval = heartbeatInterval;
            _logger = logger;
        }

        /// <summary>
        /// True when a lock file exists whose heartbeat is younger than <see cref="StaleAfter"/>,
        /// i.e. some writer (possibly this one) is alive and holding it.
        /// </summary>
        public bool IsAlive(out TimeSpan age)
        {
            age = TimeSpan.Zero;
            if (!File.Exists(LockPath)) return false;
            age = DateTime.UtcNow - File.GetLastWriteTimeUtc(LockPath);
            return age < StaleAfter;
        }

        /// <summary>
        /// Take the lock without waiting. Returns false when another writer holds a live
        /// lock, or when the file cannot be created (see <paramref name="error"/>).
        /// A stale lock is taken over.
        /// </summary>
        public bool TryAcquire(out Exception? error)
        {
            error = null;
            lock (_gate)
            {
                if (IsHeld) return true;

                try
                {
                    if (ProbeExists())
                    {
                        if (IsAlive(out var age)) return false;
                        _logger.LogWarning("Stale writer lock found (age={Age}) — taking over.", age);
                        File.Delete(LockPath);
                    }

                    if (TryCreate()) return true;

                    // CreateNew said "exists" but the server says otherwise: the CIFS
                    // client still holds a positive directory entry for the file the
                    // old holder deleted (it is kept for actimeo). An unlink round-trips
                    // to the server and drops that entry (ENOENT, nothing is deleted), so
                    // one retry claims the lock within this poll instead of the next
                    // cache refresh. A foreign lock created in the window between the
                    // probe and the unlink is the same race CreateNew already closes:
                    // the retry then fails and the next poll sees it.
                    if (ProbeExists()) return false;
                    try { File.Delete(LockPath); } catch (IOException) { }
                    return TryCreate();
                }
                catch (IOException ex)
                {
                    // Lost the race when the file now exists (another container created
                    // it between our check and our create); otherwise the directory
                    // itself is the problem. Decide with the server-side probe, not
                    // File.Exists, which answers from the attribute cache.
                    if (!ProbeExists()) error = ex;
                    return false;
                }
                catch (UnauthorizedAccessException ex)
                {
                    error = ex;
                    return false;
                }
            }
        }

        /// <summary>
        /// The atomic claim: of two writers racing for a missing or stale lock, exactly
        /// one creates the file. Returns false when the file already exists, by the
        /// cached stat (a stale directory entry counts) or by the server; any other
        /// failure propagates.
        /// </summary>
        private bool TryCreate()
        {
            try
            {
                using (var fs = new FileStream(LockPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(fs))
                {
                    writer.WriteLine($"{Environment.MachineName}:{Environment.ProcessId}:{DateTimeOffset.UtcNow:O}");
                }
            }
            catch (IOException) when (File.Exists(LockPath) || ProbeExists())
            {
                return false;
            }

            IsHeld = true;
            StartHeartbeat();
            return true;
        }

        public bool TryAcquire() => TryAcquire(out _);

        /// <summary>
        /// Whether the lock file exists, asked of the file server rather than the local
        /// attribute cache. <see cref="File.Exists(string)"/> is a stat, and on the Azure
        /// Files SMB mount (CIFS, actimeo=30) a stat is answered from a cache that is
        /// refreshed only every 30 s, so a lock the old revision deleted kept "existing"
        /// here for up to 30 s per cache period (observed as a fixed 60 s or 90 s wait in
        /// every deploy). An open always round-trips to the server and, on ENOENT, drops
        /// the stale directory entry, so the new revision sees the release within one
        /// <see cref="PollInterval"/>. Any failure other than "not found" is treated as
        /// "exists" and left to the mtime check and the CreateNew claim to sort out.
        /// </summary>
        private bool ProbeExists()
        {
            try
            {
                using var probe = new FileStream(LockPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.None);
                return true;
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }

        /// <summary>
        /// Wait until the lock can be taken: polls while another writer's lock is alive,
        /// takes over a stale one. Returns false, with a warning logged, when the lock
        /// file cannot be created for <see cref="GiveUpAfter"/> (unwritable directory):
        /// the caller then carries on without a lock. Cancelling the token throws.
        /// </summary>
        public async Task<bool> AcquireAsync(CancellationToken cancellationToken)
        {
            var loggedWait = false;
            DateTime? failingSince = null;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (TryAcquire(out var error))
                {
                    if (loggedWait) _logger.LogInformation("Writer lock acquired after waiting.");
                    return true;
                }

                if (error != null)
                {
                    failingSince ??= DateTime.UtcNow;
                    if (DateTime.UtcNow - failingSince >= GiveUpAfter)
                    {
                        _logger.LogWarning(error,
                            "Could not create the writer lock {Lock} for {Wait}; continuing without it. " +
                            "Check that the backup directory is writable.", LockPath, GiveUpAfter);
                        return false;
                    }
                }
                else
                {
                    failingSince = null;
                    if (!loggedWait)
                    {
                        IsAlive(out var age);
                        _logger.LogInformation(
                            "Another writer holds {Lock} (heartbeat age {Age}); waiting for it to release " +
                            "(stale after {Stale}).", LockPath, age, StaleAfter);
                        loggedWait = true;
                    }
                }
                await Task.Delay(PollInterval, cancellationToken);
            }
        }

        /// <summary>Touch the lock file so its mtime moves. Exposed for tests.</summary>
        public void Heartbeat()
        {
            lock (_gate)
            {
                if (!IsHeld) return;
                // Set the time explicitly rather than relying on a write: NTFS and some
                // SMB clients only refresh the directory entry's mtime on handle close.
                if (!File.Exists(LockPath)) File.WriteAllText(LockPath, string.Empty);
                File.SetLastWriteTimeUtc(LockPath, DateTime.UtcNow);
            }
        }

        /// <summary>Release the lock: stop the heartbeat and delete the file. Idempotent.</summary>
        public void Release()
        {
            lock (_gate)
            {
                if (!IsHeld) return;
                IsHeld = false;
                try
                {
                    _heartbeatCts?.Cancel();
                    _heartbeatCts?.Dispose();
                    _heartbeatCts = null;
                    if (File.Exists(LockPath)) File.Delete(LockPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to release writer lock");
                }
            }
        }

        public void Dispose() => Release();

        private void StartHeartbeat()
        {
            var cts = new CancellationTokenSource();
            _heartbeatCts = cts;
            var token = cts.Token;
            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(HeartbeatInterval, token);
                        Heartbeat();
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Writer lock heartbeat failed");
                    }
                }
            }, CancellationToken.None);
        }
    }
}
