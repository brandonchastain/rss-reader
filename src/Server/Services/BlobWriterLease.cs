#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.Logging;
using RssApp.Config;

namespace RssReader.Server.Services
{
    /// <summary>
    /// The writer's lease on a blob (BLOB-DURABILITY-SPEC.md section 3.4): the successor
    /// of <see cref="ShareWriterLock"/>. Azure acquires or refuses a blob lease atomically
    /// and expires it server-side after <see cref="Duration"/>, so there is no heartbeat
    /// file, no stale threshold and no takeover code: a writer that dies without releasing
    /// simply stops renewing, and the next writer's acquire succeeds once the lease has
    /// expired.
    ///
    /// Held for the whole process lifetime. The holder renews every
    /// <see cref="RenewInterval"/>; when renewal keeps failing for longer than
    /// <see cref="Duration"/> the lease is reported lost (<see cref="IsLost"/>, logged as an
    /// error) and the holder keeps serving while it retries the acquire, because a new
    /// writer only exists during a deploy and then this one is being stopped anyway.
    ///
    /// Rollout step 2: taken beside the lock file and logged on every acquire and release
    /// so that any disagreement between the two shows in the logs. Nothing yet depends on
    /// it. The lease protects the Litestream replica, not the database file: two writers
    /// replicating to one path corrupt the LTX chain.
    /// </summary>
    public sealed class BlobWriterLease : IAsyncDisposable
    {
        private readonly BlobClient _blob;
        private readonly ILogger _logger;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private BlobLeaseClient? _lease;
        private CancellationTokenSource? _renewCts;
        private Task? _renewTask;
        private DateTimeOffset _lastRenewedUtc;

        public TimeSpan Duration { get; }
        public TimeSpan RenewInterval { get; }

        /// <summary>How often <see cref="AcquireAsync"/> re-tries while another writer holds the lease.</summary>
        public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

        /// <summary>
        /// How long <see cref="AcquireAsync"/> keeps retrying errors other than contention
        /// (blob service unreachable, no permission) before giving up with a warning. A held
        /// lease is never given up on: the writer waits for its release or expiry.
        /// </summary>
        public TimeSpan GiveUpAfter { get; init; } = TimeSpan.FromSeconds(30);

        public string BlobName => _blob.Name;
        public string ContainerName => _blob.BlobContainerName;
        public bool IsHeld { get; private set; }

        /// <summary>Set when renewal has failed for longer than <see cref="Duration"/>; cleared by a successful re-acquire.</summary>
        public bool IsLost { get; private set; }

        public string? LeaseId => IsHeld ? _lease?.LeaseId : null;

        /// <summary>Runs after each renewal attempt with whether it succeeded. Tests use it to observe the loop.</summary>
        public Action<bool>? OnRenewed { get; set; }

        public BlobWriterLease(BlobContainerClient container, string blobName, TimeSpan duration, TimeSpan renewInterval, ILogger logger)
        {
            if (duration < TimeSpan.FromSeconds(15) || duration > TimeSpan.FromSeconds(60))
                throw new ArgumentOutOfRangeException(nameof(duration), "A blob lease lasts 15 to 60 seconds.");
            if (renewInterval >= duration)
                throw new ArgumentOutOfRangeException(nameof(renewInterval), "Renew well inside the lease duration.");
            _blob = container.GetBlobClient(blobName);
            Duration = duration;
            RenewInterval = renewInterval;
            _logger = logger;
        }

        /// <summary>
        /// Build the lease from configuration, or null when <see cref="DurabilityConfig.LeaseEnabled"/>
        /// is false. A connection string (Azurite) wins over the identity-authenticated endpoint.
        /// </summary>
        public static BlobWriterLease? Create(DurabilityConfig config, ILogger logger)
        {
            if (!config.LeaseEnabled) return null;
            var container = CreateContainerClient(config);
            return new BlobWriterLease(container, config.LeaseBlob, config.LeaseDuration, config.LeaseRenewInterval, logger)
            {
                GiveUpAfter = config.LeaseGiveUpAfter,
            };
        }

        public static BlobContainerClient CreateContainerClient(DurabilityConfig config)
        {
            // Lease calls are tiny and time-critical (a renewal must land inside the
            // duration), so the SDK's default retries are shortened.
            var options = new BlobClientOptions();
            options.Retry.MaxRetries = 2;
            options.Retry.NetworkTimeout = TimeSpan.FromSeconds(10);
            options.Retry.Delay = TimeSpan.FromMilliseconds(500);
            if (!string.IsNullOrWhiteSpace(config.ConnectionString))
                return new BlobContainerClient(config.ConnectionString, config.LeaseContainer, options);
            // Managed identity in Container Apps; the developer's az login elsewhere.
            var service = new BlobServiceClient(new Uri(config.BlobServiceUri), new DefaultAzureCredential(), options);
            return service.GetBlobContainerClient(config.LeaseContainer);
        }

        /// <summary>
        /// One acquire attempt. <c>(true, null)</c> when the lease is now held; <c>(false, null)</c>
        /// when another writer holds it; <c>(false, error)</c> for anything else.
        /// </summary>
        public async Task<(bool Acquired, Exception? Error)> TryAcquireAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (IsHeld) return (true, null);
                // One attempt is bounded by GiveUpAfter as well, so a blob service that
                // accepts connections and then stalls cannot hold the acquire loop.
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attempt.CancelAfter(GiveUpAfter);
                try
                {
                    await EnsureBlobExistsAsync(attempt.Token);
                    var lease = _blob.GetBlobLeaseClient();
                    await lease.AcquireAsync(Duration, cancellationToken: attempt.Token);
                    _lease = lease;
                    IsHeld = true;
                    IsLost = false;
                    _lastRenewedUtc = DateTimeOffset.UtcNow;
                    StartRenewLoop();
                    return (true, null);
                }
                catch (RequestFailedException ex) when (ex.ErrorCode == BlobErrorCode.LeaseAlreadyPresent)
                {
                    return (false, null);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return (false, new TimeoutException($"The blob service did not answer within {GiveUpAfter}."));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return (false, ex);
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Wait until the lease is held: polls while another writer holds it; gives up with a
        /// warning (returns false) after <see cref="GiveUpAfter"/> of errors that are not
        /// contention, so an unreachable blob service does not stop the writer from booting
        /// while the share is still the authoritative copy. Cancelling the token throws.
        /// </summary>
        public async Task<bool> AcquireAsync(CancellationToken cancellationToken)
        {
            var loggedWait = false;
            DateTimeOffset? failingSince = null;
            var started = DateTimeOffset.UtcNow;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (acquired, error) = await TryAcquireAsync(cancellationToken);
                if (acquired)
                {
                    _logger.LogInformation(
                        "Blob writer lease acquired on {Container}/{Blob} (lease {LeaseId}, {Duration}s, renewed every {Renew}s){Waited}.",
                        ContainerName, BlobName, LeaseId, Duration.TotalSeconds, RenewInterval.TotalSeconds,
                        loggedWait ? $" after waiting {(DateTimeOffset.UtcNow - started).TotalSeconds:F0} s" : string.Empty);
                    return true;
                }

                if (error != null)
                {
                    failingSince ??= DateTimeOffset.UtcNow;
                    if (DateTimeOffset.UtcNow - failingSince >= GiveUpAfter)
                    {
                        _logger.LogWarning(error,
                            "Could not acquire the blob writer lease {Container}/{Blob} for {Wait}; continuing without it.",
                            ContainerName, BlobName, GiveUpAfter);
                        return false;
                    }
                }
                else
                {
                    failingSince = null;
                    if (!loggedWait)
                    {
                        _logger.LogInformation(
                            "Another writer holds the blob lease {Container}/{Blob}; waiting for its release (expires {Duration}s after its last renewal).",
                            ContainerName, BlobName, Duration.TotalSeconds);
                        loggedWait = true;
                    }
                }
                await Task.Delay(PollInterval, cancellationToken);
            }
        }

        /// <summary>One renewal. Exposed for tests; the renew loop calls it on its own.</summary>
        public async Task<bool> RenewAsync(CancellationToken cancellationToken)
        {
            var lease = _lease;
            if (!IsHeld || lease == null) return false;
            try
            {
                await lease.RenewAsync(cancellationToken: cancellationToken);
                _lastRenewedUtc = DateTimeOffset.UtcNow;
                if (IsLost)
                {
                    IsLost = false;
                    _logger.LogInformation("Blob writer lease {Blob} renewed again after being reported lost.", BlobName);
                }
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                var silent = DateTimeOffset.UtcNow - _lastRenewedUtc;
                if (silent > Duration && !IsLost)
                {
                    IsLost = true;
                    _logger.LogError(ex,
                        "Blob writer lease {Blob} lost: renewal has failed for {Silent:F0} s, longer than the {Duration}s lease. " +
                        "Continuing to serve; another writer may now take the lease.", BlobName, silent.TotalSeconds, Duration.TotalSeconds);
                }
                else
                {
                    _logger.LogWarning(ex, "Blob writer lease {Blob} renewal failed ({Silent:F0} s since the last success).", BlobName, silent.TotalSeconds);
                }
                return false;
            }
        }

        /// <summary>
        /// Release the lease, optionally stamping metadata on the blob first (the final
        /// Litestream transaction id, spec 3.3 step 5). Failures are logged, never thrown:
        /// an unreleased lease expires on its own. Idempotent.
        /// </summary>
        public async Task ReleaseAsync(IDictionary<string, string>? metadata = null)
        {
            await _gate.WaitAsync();
            try
            {
                if (!IsHeld) return;
                IsHeld = false;
                await StopRenewLoopAsync();
                var lease = _lease;
                _lease = null;
                if (lease == null) return;
                try
                {
                    if (metadata != null && metadata.Count > 0)
                    {
                        await _blob.SetMetadataAsync(metadata, new BlobRequestConditions { LeaseId = lease.LeaseId });
                    }
                    await lease.ReleaseAsync();
                    _logger.LogInformation("Blob writer lease released on {Container}/{Blob} (lease {LeaseId}).", ContainerName, BlobName, lease.LeaseId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to release the blob writer lease {Blob}; it expires on its own in {Duration}s.", BlobName, Duration.TotalSeconds);
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await ReleaseAsync();
            _gate.Dispose();
        }

        private async Task EnsureBlobExistsAsync(CancellationToken cancellationToken)
        {
            // The container is created by Bicep in production; Azurite in the tests starts
            // empty, and Litestream does not create its container either (11.1).
            await _blob.GetParentBlobContainerClient().CreateIfNotExistsAsync(cancellationToken: cancellationToken);
            try
            {
                await _blob.UploadAsync(
                    BinaryData.FromString(string.Empty),
                    new BlobUploadOptions { Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All } },
                    cancellationToken);
            }
            catch (RequestFailedException ex) when (ex.Status == 409 || ex.Status == 412)
            {
                // Already there (409 BlobAlreadyExists, or 412 when it exists and is leased).
            }
        }

        private void StartRenewLoop()
        {
            var cts = new CancellationTokenSource();
            _renewCts = cts;
            var token = cts.Token;
            _renewTask = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(RenewInterval, token);
                        var ok = await RenewAsync(token);
                        OnRenewed?.Invoke(ok);
                        if (!ok && IsLost)
                        {
                            // Spec 3.4: keep serving, retry the acquire. The lease may have
                            // expired and be free again, or a new writer may hold it.
                            var (acquired, _) = await TryReacquireAsync(token);
                            if (acquired) _logger.LogInformation("Blob writer lease {Blob} re-acquired.", BlobName);
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Blob writer lease renew loop error");
                    }
                }
            }, CancellationToken.None);
        }

        private async Task<(bool, Exception?)> TryReacquireAsync(CancellationToken token)
        {
            try
            {
                var lease = _blob.GetBlobLeaseClient();
                await lease.AcquireAsync(Duration, cancellationToken: token);
                _lease = lease;
                IsLost = false;
                _lastRenewedUtc = DateTimeOffset.UtcNow;
                return (true, null);
            }
            catch (RequestFailedException ex) when (ex.ErrorCode == BlobErrorCode.LeaseAlreadyPresent)
            {
                return (false, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return (false, ex);
            }
        }

        private async Task StopRenewLoopAsync()
        {
            var cts = _renewCts;
            var task = _renewTask;
            _renewCts = null;
            _renewTask = null;
            if (cts == null) return;
            await cts.CancelAsync();
            if (task != null)
            {
                try { await task; } catch (OperationCanceledException) { }
            }
            cts.Dispose();
        }
    }
}
