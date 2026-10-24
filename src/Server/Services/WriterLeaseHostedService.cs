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
    /// probe, so this service only reports at start and releases at stop. Registered before
    /// <see cref="DatabaseBackupToFileService"/>, so it stops after it: the lease goes last,
    /// after the final share copy and the lock file release, mirroring the order the lease
    /// alone will follow once the share is gone (spec 3.3 step 5).
    /// </summary>
    public sealed class WriterLeaseHostedService : IHostedService
    {
        private readonly BlobWriterLease? _lease;
        private readonly ShareWriterLock _lock;
        private readonly ILogger _logger;

        public WriterLeaseHostedService(BlobWriterLease? lease, ShareWriterLock writerLock, ILogger<WriterLeaseHostedService> logger)
        {
            _lease = lease;
            _lock = writerLock;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (_lease == null)
            {
                _logger.LogInformation("Blob writer lease not configured; share lock held={LockHeld}.", _lock.IsHeld);
                return Task.CompletedTask;
            }
            Report("Host started", expectLease: true, expectLock: true);
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (_lease == null) return;
            // The backup service has run the final copy and released the lock file by now;
            // the lease is still held and goes last.
            Report("Shutdown, before lease release", expectLease: true, expectLock: false);
            await _lease.ReleaseAsync();
            Report("Shutdown, after lease release", expectLease: false, expectLock: false);
        }

        private void Report(string when, bool expectLease, bool expectLock)
        {
            var asExpected = _lease!.IsHeld == expectLease && _lock.IsHeld == expectLock;
            if (asExpected)
            {
                _logger.LogInformation(
                    "{When}: blob lease held={LeaseHeld} (lost={Lost}), share lock held={LockHeld}; as expected.",
                    when, _lease.IsHeld, _lease.IsLost, _lock.IsHeld);
            }
            else
            {
                _logger.LogWarning(
                    "{When}: blob lease held={LeaseHeld} (lost={Lost}), share lock held={LockHeld}; DISAGREE (expected lease={ExpectLease}, lock={ExpectLock}).",
                    when, _lease.IsHeld, _lease.IsLost, _lock.IsHeld, expectLease, expectLock);
            }
        }
    }
}
