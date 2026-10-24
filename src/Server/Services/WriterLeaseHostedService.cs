#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RssReader.Server.Services
{
    /// <summary>
    /// Holds the <see cref="BlobWriterLease"/> for the life of the host and releases it on
    /// shutdown, logging the state of the share lock file beside it each time
    /// (BLOB-DURABILITY-SPEC.md rollout step 2: the two must never disagree).
    ///
    /// The lease is acquired in <c>Program.cs</c> while the startup gate is answering the
    /// probe, so this service only reports at start and releases at stop. Stop order (spec
    /// 3.3 steps 4 and 5; hosted services stop in reverse registration order): the writers
    /// stop, <see cref="LitestreamSupervisor"/> runs the final sync, then this releases the
    /// lease, then <see cref="DatabaseBackupToFileService"/> runs the final share copy and
    /// releases the lock file. The lease goes before the share copy on purpose: the next
    /// writer restores from the replica as soon as it holds the lease (rollout step 3), and
    /// the share copy, which only a rollback reads, finishes in the background of its boot.
    /// </summary>
    public sealed class WriterLeaseHostedService : IHostedService
    {
        private readonly BlobWriterLease? _lease;
        private readonly ShareWriterLock _lock;
        private readonly bool _shareLockDeferred;
        private readonly ILogger _logger;

        /// <param name="shareLockDeferred">
        /// True when startup restored from the replica and left the share lock to the backup
        /// service (<see cref="DatabaseSeeder.RestoreOrSeedAsync"/>), so the lock may not be
        /// held yet when the host starts and its state is reported without a verdict.
        /// </param>
        public WriterLeaseHostedService(BlobWriterLease? lease, ShareWriterLock writerLock, ILogger<WriterLeaseHostedService> logger, bool shareLockDeferred = false)
        {
            _lease = lease;
            _lock = writerLock;
            _logger = logger;
            _shareLockDeferred = shareLockDeferred;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (_lease == null)
            {
                _logger.LogInformation("Blob writer lease not configured; share lock held={LockHeld}.", _lock.IsHeld);
                return Task.CompletedTask;
            }
            Report("Host started", expectLease: true, expectLock: _shareLockDeferred ? null : true);
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (_lease == null) return;
            // The Litestream final sync has run by now (it stops before this service); the
            // share copy and the lock file release come after, so the lock is still held.
            Report("Shutdown, after the Litestream final sync and before lease release", expectLease: true, expectLock: true);
            await _lease.ReleaseAsync();
            Report("Shutdown, after lease release (final share copy next)", expectLease: false, expectLock: true);
        }

        private void Report(string when, bool expectLease, bool? expectLock)
        {
            var asExpected = _lease!.IsHeld == expectLease && (expectLock == null || _lock.IsHeld == expectLock);
            if (asExpected)
            {
                _logger.LogInformation(
                    "{When}: blob lease held={LeaseHeld} (lost={Lost}), share lock held={LockHeld}; as expected{Deferred}.",
                    when, _lease.IsHeld, _lease.IsLost, _lock.IsHeld,
                    expectLock == null ? " (share lock left to the backup service)" : string.Empty);
            }
            else
            {
                _logger.LogWarning(
                    "{When}: blob lease held={LeaseHeld} (lost={Lost}), share lock held={LockHeld}; DISAGREE (expected lease={ExpectLease}, lock={ExpectLock}).",
                    when, _lease.IsHeld, _lease.IsLost, _lock.IsHeld, expectLease, expectLock?.ToString() ?? "any");
            }
        }
    }
}
