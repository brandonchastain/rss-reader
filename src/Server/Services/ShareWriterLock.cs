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
    /// Liveness is the file's mtime: the holder rewrites the file every
    /// <see cref="HeartbeatInterval"/>, and a lock whose mtime is older than
    /// <see cref="StaleAfter"/> belongs to a replica that died without releasing it and
    /// is taken over. The first line is a per-process token so a holder can check that it
    /// still owns the file before publishing a copy, and never deletes a newer holder's lock.
    /// </summary>
    public sealed class ShareWriterLock : IDisposable
    {
        private readonly ILogger _logger;
        private readonly string _ownerToken;
        private readonly string _hostName;
        private readonly int _processId;
        private readonly object _gate = new();
        private FileStream? _fs;
        private CancellationTokenSource? _heartbeatCts;
        private Task? _heartbeatTask;

        public string LockPath { get; }
        public TimeSpan StaleAfter { get; }
        public TimeSpan HeartbeatInterval { get; }

        /// <summary>How often <see cref="AcquireAsync"/> re-checks a foreign lock.</summary>
        public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

        public bool IsHeld => _fs != null;

        public ShareWriterLock(string lockPath, TimeSpan staleAfter, TimeSpan heartbeatInterval, ILogger logger)
        {
            LockPath = lockPath;
            StaleAfter = staleAfter;
            HeartbeatInterval = heartbeatInterval;
            _logger = logger;
            _ownerToken = Guid.NewGuid().ToString("N");
            _hostName = Environment.MachineName;
            _processId = Environment.ProcessId;
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
        /// lock. A stale lock is taken over.
        /// </summary>
        public bool TryAcquire()
        {
            lock (_gate)
            {
                if (IsHeld) return true;

                try
                {
                    if (File.Exists(LockPath))
                    {
                        if (IsAlive(out var age)) return false;
                        _logger.LogWarning(
                            "Stale writer lock found (age={Age}) — taking over.", age);
                        SafeDelete(LockPath);
                    }

                    var fs = new FileStream(LockPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                    try
                    {
                        WriteContents(fs);
                    }
                    catch
                    {
                        fs.Dispose();
                        SafeDelete(LockPath);
                        throw;
                    }
                    _fs = fs;
                    StartHeartbeat();
                    return true;
                }
                catch (IOException)
                {
                    // Lost the race: another container created the lock between our
                    // check and our create.
                    return false;
                }
                catch (UnauthorizedAccessException)
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Wait until the lock can be taken: polls while another writer's lock is alive,
        /// takes over a stale one. Cancelling the token gives up (lock not held).
        /// </summary>
        public async Task AcquireAsync(CancellationToken cancellationToken)
        {
            var loggedWait = false;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (TryAcquire())
                {
                    if (loggedWait) _logger.LogInformation("Writer lock acquired after waiting.");
                    return;
                }

                if (!loggedWait)
                {
                    IsAlive(out var age);
                    _logger.LogInformation(
                        "Another writer holds {Lock} (heartbeat age {Age}); waiting for it to release " +
                        "(stale after {Stale}).", LockPath, age, StaleAfter);
                    loggedWait = true;
                }
                await Task.Delay(PollInterval, cancellationToken);
            }
        }

        /// <summary>
        /// True when the lock file on the share still carries this process's token. A
        /// holder whose heartbeat stalled long enough for another writer to take over
        /// must not publish a copy.
        /// </summary>
        public bool StillOwned()
        {
            if (!IsHeld) return false;
            return ReadFirstLine(LockPath) == _ownerToken;
        }

        /// <summary>Rewrite the lock file so its mtime moves. Exposed for tests.</summary>
        public void Heartbeat()
        {
            lock (_gate)
            {
                if (_fs == null) return;
                WriteContents(_fs);
            }
        }

        /// <summary>
        /// Release the lock. Deletes the file only if this process still owns it, so a
        /// stalled old writer never deletes the lock a newer writer took over.
        /// </summary>
        public void Release()
        {
            lock (_gate)
            {
                if (_fs == null) return;
                try
                {
                    _heartbeatCts?.Cancel();
                    _fs.Dispose();
                    _fs = null;

                    if (ReadFirstLine(LockPath) == _ownerToken)
                    {
                        SafeDelete(LockPath);
                    }
                    else if (File.Exists(LockPath))
                    {
                        _logger.LogWarning(
                            "Lock token mismatch on release — leaving lock for the current owner.");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to release writer lock");
                }
                finally
                {
                    _heartbeatCts?.Dispose();
                    _heartbeatCts = null;
                    _heartbeatTask = null;
                }
            }
        }

        public void Dispose() => Release();

        private void StartHeartbeat()
        {
            _heartbeatCts = new CancellationTokenSource();
            var token = _heartbeatCts.Token;
            _heartbeatTask = Task.Run(async () =>
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

        private void WriteContents(FileStream fs)
        {
            fs.Seek(0, SeekOrigin.Begin);
            fs.SetLength(0);
            using var writer = new StreamWriter(fs, leaveOpen: true);
            // First line is the owner token; readers compare on release to avoid an
            // old writer deleting a newer writer's lock. Second line is diagnostics.
            writer.WriteLine(_ownerToken);
            writer.WriteLine($"{_hostName}:{_processId}:{DateTimeOffset.UtcNow:O}");
            writer.Flush();
            fs.Flush(flushToDisk: true);
            // Waiters read the mtime through the directory entry, which NTFS (and some
            // SMB clients) only refresh from an open handle on close. Set it explicitly.
            try { File.SetLastWriteTimeUtc(LockPath, DateTime.UtcNow); } catch (IOException) { }
        }

        /// <summary>Read the owner token of a lock file, tolerating a holder's open handle.</summary>
        internal static string? ReadFirstLine(string path)
        {
            try
            {
                using var fs = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs);
                return reader.ReadLine();
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        private static void SafeDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
