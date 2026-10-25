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
    /// shutdown (BLOB-DURABILITY-SPEC.md 3.3 step 5).
    ///
    /// The lease is acquired in <c>Program.cs</c> while the startup gate is answering the
    /// probe, so this service only reports at start and releases at stop. Stop order (hosted
    /// services stop in reverse registration order): the writers stop, then
    /// <see cref="LitestreamSupervisor"/> runs the final sync, then this releases the lease.
    /// The next writer restores from the replica as soon as it holds the lease, so the release
    /// comes only after everything is uploaded.
    /// </summary>
    public sealed class WriterLeaseHostedService : IHostedService
    {
        private readonly BlobWriterLease? _lease;
        private readonly WriterState? _writerState;
        private readonly ILogger _logger;

        /// <param name="writerState">When set and handed over, the lease left at the handoff and shutdown has nothing to release.</param>
        public WriterLeaseHostedService(BlobWriterLease? lease, ILogger<WriterLeaseHostedService> logger, WriterState? writerState = null)
        {
            _lease = lease;
            _logger = logger;
            _writerState = writerState;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (_lease == null)
            {
                _logger.LogInformation("Blob writer lease not configured.");
                return Task.CompletedTask;
            }
            if (_lease.IsHeld)
                _logger.LogInformation("Host started: blob lease held (lost={Lost}).", _lease.IsLost);
            else
                _logger.LogWarning("Host started WITHOUT the blob lease (lost={Lost}); another writer may hold it.", _lease.IsLost);
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (_lease == null) return;
            if (_writerState is { IsHandedOver: true } handed)
            {
                // Cooperative handoff: the lease went to the next revision at HandedOverAtUtc.
                _logger.LogInformation("Shutdown, lease handed over to {To} at {At:O}; nothing to release.", handed.HandedOverTo, handed.HandedOverAtUtc);
                return;
            }
            // The Litestream final sync has run by now (it stops before this service).
            await _lease.ReleaseAsync();
            _logger.LogInformation("Shutdown, after lease release.");
        }
    }
}
