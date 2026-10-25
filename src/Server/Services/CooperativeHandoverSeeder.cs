#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RssApp.Config;

namespace RssReader.Server.Services
{
    /// <summary>
    /// The new writer's side of the cooperative handover (BLOB-DURABILITY-SPEC.md 3.2 v2,
    /// rollout step 3). While the startup gate answers the probe:
    ///
    /// <list type="number">
    /// <item>start <c>litestream restore -f</c> into a staging file: the full restore runs
    ///   now while the old revision keeps serving, and the follower then keeps applying the
    ///   tail. A follower that dies (it races the old writer's compaction deleting LTX files
    ///   from the replica, seen in production) is restarted up to
    ///   <see cref="DurabilityConfig.FollowerAttempts"/> times;</item>
    /// <item>only once the initial restore is on disk, write the handoff request, so the old
    ///   writer is read-only for seconds rather than for the whole restore; it drains, syncs,
    ///   acknowledges with its final transaction id and releases the lease
    ///   (<see cref="WriterHandover"/>);</item>
    /// <item>the gate's probe stays 503 (the old revision keeps serving reads) until the
    ///   database is in place, so traffic moves here when the host is a second or two away.
    ///   It opens early only to unblock the old revision: after
    ///   <see cref="DurabilityConfig.HandoffProbeHold"/> without a finished handover (an old
    ///   image ignores the request and releases on SIGTERM, which needs a passing probe), or
    ///   on any failure;</item>
    /// <item>acquire the lease;</item>
    /// <item>wait until the follower has applied up to the acknowledged transaction id (with
    ///   no acknowledgement, until it goes quiet), bounded by <see cref="DurabilityConfig.CatchUpTimeout"/>;</item>
    /// <item>stop the follower, <c>PRAGMA quick_check</c>, rename over the live path.</item>
    /// </list>
    ///
    /// Returns false, after an error log, on any failure; the caller then refuses to start and
    /// the platform restarts the container, which restores again from the replica (the old
    /// writer's final sync is already in it). Every phase
    /// logs a UTC timestamp so the 503 window can be read back from Log Analytics.
    /// </summary>
    public sealed class CooperativeHandoverSeeder
    {
        private readonly DurabilityConfig _config;
        private readonly string _activeDbPath;
        private readonly IWriterHandoffChannel _channel;
        private readonly Func<ILitestreamFollower> _followerFactory;
        private readonly Func<CancellationToken, Task<bool>> _acquireLease;
        private readonly Action _readyForTraffic;
        private readonly string _self;
        private readonly ILogger _logger;

        /// <summary>Tests: polling cadence while waiting on the follower.</summary>
        public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(200);
        /// <summary>Tests: how long to wait for the follower to exit after SIGTERM.</summary>
        public TimeSpan FollowerStopTimeout { get; init; } = TimeSpan.FromSeconds(10);

        public CooperativeHandoverSeeder(
            DurabilityConfig config,
            string activeDbPath,
            IWriterHandoffChannel channel,
            Func<ILitestreamFollower> followerFactory,
            Func<CancellationToken, Task<bool>> acquireLease,
            Action readyForTraffic,
            string self,
            ILogger logger)
        {
            _config = config;
            _activeDbPath = activeDbPath;
            _channel = channel;
            _followerFactory = followerFactory;
            _acquireLease = acquireLease;
            _readyForTraffic = readyForTraffic;
            _self = self;
            _logger = logger;
        }

        public async Task<bool> RunAsync(CancellationToken cancellationToken)
        {
            var sw = Stopwatch.StartNew();
            var ready = false;
            void Ready(string why)
            {
                if (ready) return;
                ready = true;
                _logger.LogInformation("Handover: probe answers 200 from {Utc} ({Why}).", DateTimeOffset.UtcNow.ToString("O"), why);
                _readyForTraffic();
            }

            var activeDir = Path.GetDirectoryName(_activeDbPath);
            if (!string.IsNullOrEmpty(activeDir)) Directory.CreateDirectory(activeDir);

            ILitestreamFollower? follower = null;
            using var holdCts = new CancellationTokenSource();
            try
            {
                // 1. Follower, with restarts: the initial restore reads LTX files that the
                // old writer's compaction may delete underneath it.
                var attempt = 0;
                while (true)
                {
                    attempt++;
                    if (follower != null) await follower.DisposeAsync();
                    follower = _followerFactory();
                    try
                    {
                        follower.Start();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Handover: the follower could not be started.");
                        Ready("follower failed to start");
                        return false;
                    }

                    var restoreDeadline = DateTimeOffset.UtcNow + _config.RestoreTimeout;
                    while (follower.ReadTxid() == null && !follower.HasExited && DateTimeOffset.UtcNow < restoreDeadline)
                        await Task.Delay(PollInterval, cancellationToken);
                    if (follower.ReadTxid() != null) break;

                    if (follower.HasExited && attempt < _config.FollowerAttempts)
                    {
                        _logger.LogWarning("Handover: the follower exited (code {Code}) before the initial restore completed, attempt {Attempt} of {Max}; restarting it.",
                            follower.ExitCode, attempt, _config.FollowerAttempts);
                        continue;
                    }
                    _logger.LogError(follower.HasExited
                            ? "Handover: the follower exited (code {Code}) on attempt {Attempt}."
                            : "Handover: the initial restore did not complete within {Timeout} (attempt {Attempt}).",
                        follower.HasExited ? follower.ExitCode : _config.RestoreTimeout, attempt);
                    Ready("follower failed");
                    return false;
                }
                _logger.LogInformation("Handover: initial restore complete at {Utc}, {Ms} ms in, follower at txid {Txid}.",
                    DateTimeOffset.UtcNow.ToString("O"), sw.ElapsedMilliseconds, follower.ReadTxid());

                // 2. Ask only now, so the old writer is read-only for seconds, not for the restore.
                var request = new HandoffRequest(Guid.NewGuid().ToString("N"), _self, DateTimeOffset.UtcNow);
                try
                {
                    await _channel.RequestAsync(request, cancellationToken);
                    _logger.LogWarning("Handover: requested at {Utc} (id {Id}, from {Self}).", request.RequestedAtUtc.ToString("O"), request.Id, _self);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Handover: could not write the handoff request; the old writer will release on SIGTERM, so the probe opens now.");
                    Ready("request not written");
                }

                // The probe opens by itself if the handover is still unfinished after the hold:
                // an old image ignores the request and only releases after SIGTERM.
                _ = Task.Run(async () =>
                {
                    try { await Task.Delay(_config.HandoffProbeHold, holdCts.Token); Ready("hold elapsed without a finished handover"); }
                    catch (OperationCanceledException) { }
                }, CancellationToken.None);

                // The lease: released by the old writer at the end of its drain, or on its
                // SIGTERM if it runs an image that does not know the marker, or by expiry.
                var leaseHeld = await _acquireLease(cancellationToken);
                if (!leaseHeld)
                {
                    _logger.LogError("Handover: blob lease not held.");
                    return false;
                }
                _logger.LogInformation("Handover: lease held at {Utc}, {Ms} ms in.", DateTimeOffset.UtcNow.ToString("O"), sw.ElapsedMilliseconds);

                // The acknowledgement tells us where the old writer stopped.
                long? target = null;
                try
                {
                    var (_, ack) = await _channel.ReadAsync(cancellationToken);
                    if (ack != null && ack.Id == request.Id)
                    {
                        target = ack.FinalTxid;
                        _logger.LogInformation("Handover: acknowledged by {By} at {At}; final txid {Txid}.",
                            ack.ReleasedBy, ack.ReleasedAtUtc.ToString("O"), target?.ToString() ?? "unknown");
                    }
                    else
                    {
                        _logger.LogWarning("Handover: no acknowledgement for request {Id} (old writer released on SIGTERM or expiry); waiting for the replica to go quiet.", request.Id);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Handover: could not read the acknowledgement; waiting for the replica to go quiet.");
                }

                var caughtUp = await WaitForCatchUpAsync(follower.ReadTxid, () => follower.HasExited, target, _config.CatchUpTimeout, _config.CatchUpQuietPeriod, PollInterval, cancellationToken);
                if (!caughtUp)
                {
                    _logger.LogError("Handover: follower at txid {Txid} did not reach {Target} within {Timeout}.",
                        follower.ReadTxid()?.ToString() ?? "none", target?.ToString() ?? "quiet", _config.CatchUpTimeout);
                    return false;
                }
                var finalTxid = follower.ReadTxid();
                _logger.LogInformation("Handover: follower caught up at {Utc} (txid {Txid}), {Ms} ms in; stopping it.", DateTimeOffset.UtcNow.ToString("O"), finalTxid, sw.ElapsedMilliseconds);

                var exit = await follower.StopAsync(FollowerStopTimeout, cancellationToken);
                if (exit != 0)
                {
                    _logger.LogError("Handover: follower exited with code {Code}.", exit?.ToString() ?? "none");
                    return false;
                }
                if (!File.Exists(follower.OutputPath))
                {
                    _logger.LogError("Handover: followed copy {Output} is missing after the follower stopped.", follower.OutputPath);
                    return false;
                }

                var (ok, check, error) = DatabaseIntegrity.QuickCheck(follower.OutputPath);
                if (!ok)
                {
                    _logger.LogError("Handover: PRAGMA quick_check failed on the followed copy (result='{Result}', error='{Error}').", check ?? "<null>", error ?? "<none>");
                    return false;
                }

                SafeDelete(_activeDbPath + "-wal");
                SafeDelete(_activeDbPath + "-shm");
                File.Move(follower.OutputPath, _activeDbPath, overwrite: true);
                var size = new FileInfo(_activeDbPath).Length;
                _logger.LogWarning(
                    "Handover: restored {Bytes:N0} bytes from the Litestream replica to {Active} at txid {Txid} in {Ms} ms (quick_check ok); host starts now ({Utc}).",
                    size, _activeDbPath, finalTxid, sw.ElapsedMilliseconds, DateTimeOffset.UtcNow.ToString("O"));
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Handover: failed {Ms} ms in.", sw.ElapsedMilliseconds);
                Ready("handover failed");
                return false;
            }
            finally
            {
                holdCts.Cancel();
                Ready("handover finished");
                if (follower != null)
                {
                    await follower.DisposeAsync();
                    SafeDelete(follower.OutputPath);
                    SafeDelete(follower.OutputPath + "-txid");
                    SafeDelete(follower.OutputPath + "-wal");
                    SafeDelete(follower.OutputPath + "-shm");
                }
            }
        }

        /// <summary>
        /// Wait for the follower to reach <paramref name="target"/>, or, when there is none,
        /// for its transaction id to stay put for <paramref name="quietPeriod"/>. False on
        /// timeout or if the follower dies. Pure logic, exposed for tests.
        /// </summary>
        public static async Task<bool> WaitForCatchUpAsync(
            Func<long?> readTxid, Func<bool> hasExited, long? target,
            TimeSpan timeout, TimeSpan quietPeriod, TimeSpan poll, CancellationToken cancellationToken)
        {
            var deadline = DateTimeOffset.UtcNow + timeout;
            var last = readTxid();
            var lastChange = DateTimeOffset.UtcNow;
            while (true)
            {
                var now = readTxid();
                if (now != last) { last = now; lastChange = DateTimeOffset.UtcNow; }
                if (now != null)
                {
                    if (target is { } t && now >= t) return true;
                    if (target == null && DateTimeOffset.UtcNow - lastChange >= quietPeriod) return true;
                }
                if (hasExited()) return false;
                if (DateTimeOffset.UtcNow >= deadline) return false;
                await Task.Delay(poll, cancellationToken);
            }
        }

        private static void SafeDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
