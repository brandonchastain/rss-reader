#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace RssReader.Server.Services
{
    /// <summary>A new writer asking the running one to hand the database over (spec 3.2 v2, cooperative trigger).</summary>
    public sealed record HandoffRequest(string Id, string RequestedBy, DateTimeOffset RequestedAtUtc);

    /// <summary>The running writer's answer: it has synced, recorded the final Litestream transaction id, and released the lease.</summary>
    public sealed record HandoffAck(string Id, string ReleasedBy, long? FinalTxid, DateTimeOffset ReleasedAtUtc);

    /// <summary>
    /// The out-of-band channel between the two writer revisions during a deploy. A blob in
    /// the lease container carries the request and, later, the acknowledgement, as
    /// metadata. The lease blob itself cannot be used: setting metadata on a leased blob
    /// needs the lease id, which the new writer does not have yet.
    /// </summary>
    public interface IWriterHandoffChannel
    {
        Task RequestAsync(HandoffRequest request, CancellationToken cancellationToken);
        Task<(HandoffRequest? Request, HandoffAck? Ack)> ReadAsync(CancellationToken cancellationToken);
        Task AcknowledgeAsync(HandoffAck ack, CancellationToken cancellationToken);
    }

    public sealed class BlobWriterHandoffChannel : IWriterHandoffChannel
    {
        private readonly BlobContainerClient _container;
        private readonly BlobClient _blob;

        public BlobWriterHandoffChannel(BlobContainerClient container, string blobName)
        {
            _container = container;
            _blob = container.GetBlobClient(blobName);
        }

        public async Task RequestAsync(HandoffRequest request, CancellationToken cancellationToken)
        {
            // Overwrite: a request replaces any earlier request and its acknowledgement.
            await _container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
            await _blob.UploadAsync(BinaryData.FromString(string.Empty), new BlobUploadOptions
            {
                Metadata = HandoffMetadata.FromRequest(request),
            }, cancellationToken);
        }

        public async Task<(HandoffRequest? Request, HandoffAck? Ack)> ReadAsync(CancellationToken cancellationToken)
        {
            try
            {
                var props = await _blob.GetPropertiesAsync(cancellationToken: cancellationToken);
                return HandoffMetadata.Parse(props.Value.Metadata);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return (null, null);
            }
        }

        public async Task AcknowledgeAsync(HandoffAck ack, CancellationToken cancellationToken)
        {
            var props = await _blob.GetPropertiesAsync(cancellationToken: cancellationToken);
            var metadata = new Dictionary<string, string>(props.Value.Metadata, StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in HandoffMetadata.FromAck(ack)) metadata[k] = v;
            await _blob.SetMetadataAsync(metadata, cancellationToken: cancellationToken);
        }
    }

    /// <summary>Metadata encoding shared by the blob channel and the tests' in-memory one.</summary>
    public static class HandoffMetadata
    {
        // Blob metadata keys must be valid C# identifiers: no hyphens.
        public const string Id = "handoffid";
        public const string RequestedBy = "requestedby";
        public const string RequestedAt = "requestedat";
        public const string AckId = "ackid";
        public const string ReleasedBy = "releasedby";
        public const string ReleasedAt = "releasedat";
        public const string FinalTxid = "finaltxid";

        public static Dictionary<string, string> FromRequest(HandoffRequest r) => new(StringComparer.OrdinalIgnoreCase)
        {
            [Id] = r.Id,
            [RequestedBy] = r.RequestedBy,
            [RequestedAt] = r.RequestedAtUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
        };

        public static Dictionary<string, string> FromAck(HandoffAck a)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [AckId] = a.Id,
                [ReleasedBy] = a.ReleasedBy,
                [ReleasedAt] = a.ReleasedAtUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            };
            if (a.FinalTxid is { } t) d[FinalTxid] = t.ToString(CultureInfo.InvariantCulture);
            return d;
        }

        public static (HandoffRequest? Request, HandoffAck? Ack) Parse(IDictionary<string, string> m)
        {
            HandoffRequest? request = null;
            HandoffAck? ack = null;
            if (m.TryGetValue(Id, out var id) && m.TryGetValue(RequestedBy, out var by) && m.TryGetValue(RequestedAt, out var at)
                && DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var atUtc))
            {
                request = new HandoffRequest(id, by, atUtc);
            }
            if (m.TryGetValue(AckId, out var ackId) && m.TryGetValue(ReleasedBy, out var relBy) && m.TryGetValue(ReleasedAt, out var relAt)
                && DateTimeOffset.TryParse(relAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var relUtc))
            {
                long? txid = m.TryGetValue(FinalTxid, out var tx) && long.TryParse(tx, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
                ack = new HandoffAck(ackId, relBy, txid, relUtc);
            }
            return (request, ack);
        }
    }

    /// <summary>In-memory channel for tests and for a process with no blob storage.</summary>
    public sealed class InMemoryWriterHandoffChannel : IWriterHandoffChannel
    {
        private readonly object _sync = new();
        private Dictionary<string, string> _metadata = new(StringComparer.OrdinalIgnoreCase);

        public int Requests { get; private set; }
        public int Acks { get; private set; }

        public Task RequestAsync(HandoffRequest request, CancellationToken cancellationToken)
        {
            lock (_sync) { _metadata = HandoffMetadata.FromRequest(request); Requests++; }
            return Task.CompletedTask;
        }

        public Task<(HandoffRequest? Request, HandoffAck? Ack)> ReadAsync(CancellationToken cancellationToken)
        {
            lock (_sync) return Task.FromResult(HandoffMetadata.Parse(_metadata));
        }

        public Task AcknowledgeAsync(HandoffAck ack, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                foreach (var (k, v) in HandoffMetadata.FromAck(ack)) _metadata[k] = v;
                Acks++;
            }
            return Task.CompletedTask;
        }

        /// <summary>Tests: seed the channel as a previous deploy would have left it.</summary>
        public void Seed(HandoffRequest request, HandoffAck? ack = null)
        {
            lock (_sync)
            {
                _metadata = HandoffMetadata.FromRequest(request);
                if (ack != null) foreach (var (k, v) in HandoffMetadata.FromAck(ack)) _metadata[k] = v;
            }
        }
    }
}
