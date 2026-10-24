#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace RssReader.Server.Services
{
    /// <summary>
    /// The running writer's side of a cooperative handover (BLOB-DURABILITY-SPEC.md 3.2 v2,
    /// rollout step 3). When the next revision asks, before the platform's SIGTERM arrives:
    ///
    /// <list type="number">
    /// <item>refuse writes from now on (<see cref="WriterState"/>), stop the feed scheduler and worker;</item>
    /// <item><c>PRAGMA wal_checkpoint(TRUNCATE)</c>;</item>
    /// <item><c>litestream sync -wait</c> and stop the Litestream child (final transaction id from the sync);</item>
    /// <item>acknowledge on the handoff blob with that transaction id;</item>
    /// <item>release the blob lease, stamping the transaction id on it (spec 3.3 step 5);</item>
    /// <item>then, off the critical path, one last copy to the share for rollback.</item>
    /// </list>
    ///
    /// The steps are delegates so the order can be unit-tested with every side effect faked.
    /// Runs at most once; a second request is answered from the first result.
    /// </summary>
    public sealed class WriterHandover
    {
        public sealed class Steps
        {
            public required Func<CancellationToken, Task> StopWritersAsync { get; init; }
            public required Func<CancellationToken, Task> CheckpointAsync { get; init; }
            /// <summary>Final sync plus Litestream stop; returns the replica transaction id when known.</summary>
            public required Func<CancellationToken, Task<long?>> FinalSyncAsync { get; init; }
            public required Func<IDictionary<string, string>, Task> ReleaseLeaseAsync { get; init; }
            /// <summary>Runs after the lease is released, not awaited by the handover; null to skip.</summary>
            public Func<CancellationToken, Task>? AfterReleaseAsync { get; init; }
        }

        private readonly WriterState _state;
        private readonly IWriterHandoffChannel _channel;
        private readonly Steps _steps;
        private readonly string _self;
        private readonly DateTimeOffset _startedUtc;
        private readonly TimeSpan _drainTimeout;
        private readonly ILogger _logger;
        private readonly SemaphoreSlim _once = new(1, 1);
        private readonly HashSet<string> _ignored = new(StringComparer.Ordinal);

        public string Identity => _self;
        public bool HandedOver => _state.IsHandedOver;
        public Task? AfterReleaseTask { get; private set; }

        public WriterHandover(
            WriterState state, IWriterHandoffChannel channel, Steps steps, string self,
            DateTimeOffset startedUtc, TimeSpan drainTimeout, ILogger logger)
        {
            _state = state;
            _channel = channel;
            _steps = steps;
            _self = self;
            _startedUtc = startedUtc;
            _drainTimeout = drainTimeout;
            _logger = logger;
        }

        /// <summary>
        /// A request is for this writer when it is newer than this process, comes from
        /// another identity, and has not been acknowledged yet. Older markers are what a
        /// previous deploy left behind; our own marker is the one we wrote at boot.
        /// </summary>
        public bool IsForMe(HandoffRequest? request, HandoffAck? ack)
        {
            if (request == null) return false;
            if (string.Equals(request.RequestedBy, _self, StringComparison.Ordinal)) return false;
            if (request.RequestedAtUtc <= _startedUtc) return false;
            if (ack != null && string.Equals(ack.Id, request.Id, StringComparison.Ordinal)) return false;
            return true;
        }

        /// <summary>One poll: read the channel and hand over if asked. Returns true when a handover ran now.</summary>
        public async Task<bool> PollAsync(CancellationToken cancellationToken)
        {
            if (_state.IsHandedOver) return false;
            var (request, ack) = await _channel.ReadAsync(cancellationToken);
            if (!IsForMe(request, ack) || _ignored.Contains(request!.Id)) return false;
            await HandOverAsync(request, cancellationToken);
            return true;
        }

        public async Task HandOverAsync(HandoffRequest request, CancellationToken cancellationToken)
        {
            await _once.WaitAsync(cancellationToken);
            try
            {
                if (_state.IsHandedOver) return;
                var sw = Stopwatch.StartNew();
                _logger.LogWarning(
                    "Handoff requested by {By} at {RequestedAt} (id {Id}); draining at {Utc}. Writes answer 503 from now on.",
                    request.RequestedBy, request.RequestedAtUtc.ToString("O"), request.Id, DateTimeOffset.UtcNow.ToString("O"));

                long? txid = null;
                try
                {
                    using var drain = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    drain.CancelAfter(_drainTimeout);
                    try
                    {
                        await _steps.StopWritersAsync(drain.Token);
                        _logger.LogInformation("Handoff: writers stopped {Ms} ms in.", sw.ElapsedMilliseconds);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogError("Handoff: writers did not stop within {Timeout}; continuing with the checkpoint.", _drainTimeout);
                    }

                    try
                    {
                        await _steps.CheckpointAsync(cancellationToken);
                        _logger.LogInformation("Handoff: checkpoint done {Ms} ms in.", sw.ElapsedMilliseconds);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogError(ex, "Handoff: wal_checkpoint failed; the final sync still carries the WAL.");
                    }

                    txid = await _steps.FinalSyncAsync(cancellationToken);
                    _logger.LogInformation("Handoff: final sync done {Ms} ms in; final txid {Txid}.", sw.ElapsedMilliseconds, txid?.ToString() ?? "unknown");
                }
                finally
                {
                    // Whatever happened above, the lease must move: the new writer is waiting
                    // on it and the platform will stop this revision regardless.
                    _state.MarkHandedOver(request.RequestedBy, txid);
                    var ack = new HandoffAck(request.Id, _self, txid, DateTimeOffset.UtcNow);
                    try { await _channel.AcknowledgeAsync(ack, cancellationToken); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogError(ex, "Handoff: could not write the acknowledgement; the new writer will wait for the replica to go quiet instead.");
                    }

                    var metadata = new Dictionary<string, string>
                    {
                        ["handoffid"] = request.Id,
                        ["releasedto"] = request.RequestedBy,
                        ["releasedat"] = ack.ReleasedAtUtc.UtcDateTime.ToString("O"),
                    };
                    if (txid is { } t) metadata["finaltxid"] = t.ToString();
                    await _steps.ReleaseLeaseAsync(metadata);
                    _logger.LogWarning(
                        "Handoff complete at {Utc}: lease released to {To} {Ms} ms after the request was seen (final txid {Txid}). This revision is read-only until it is stopped.",
                        DateTimeOffset.UtcNow.ToString("O"), request.RequestedBy, sw.ElapsedMilliseconds, txid?.ToString() ?? "unknown");
                }

                if (_steps.AfterReleaseAsync is { } after)
                {
                    AfterReleaseTask = Task.Run(async () =>
                    {
                        try { await after(CancellationToken.None); }
                        catch (Exception ex) { _logger.LogError(ex, "Handoff: post-release work failed."); }
                    }, CancellationToken.None);
                }
            }
            finally
            {
                _once.Release();
            }
        }
    }
}
