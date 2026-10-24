#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Threading;
using System.Threading.Tasks;

namespace RssReader.Server.Services
{
    /// <summary>
    /// Block hashes of a published base copy, kept in memory by the writer that published
    /// it. The final backup on shutdown compares the live database against this and ships
    /// only the blocks that differ (<see cref="DatabaseDelta"/>), so the deploy handover
    /// costs one sequential read of the live file instead of a full copy to the share.
    /// </summary>
    public sealed class BlockManifest
    {
        /// <summary>
        /// 64 KB: sixteen 4 KB SQLite pages. A feed refresh dirties scattered leaf pages, so
        /// a smaller block keeps the delta small; 64 KB keeps the manifest at 16 bytes per
        /// 64 KB (256 KB for a 1 GB database).
        /// </summary>
        public const int DefaultBlockSize = 64 * 1024;

        public int BlockSize { get; }
        public long Length { get; }
        /// <summary>XxHash128 of the whole base file; ties a delta to the base it was computed against.</summary>
        public byte[] FileHash { get; }
        private readonly List<byte[]> _blocks;

        public int BlockCount => _blocks.Count;

        internal BlockManifest(int blockSize, long length, byte[] fileHash, List<byte[]> blocks)
        {
            BlockSize = blockSize;
            Length = length;
            FileHash = fileHash;
            _blocks = blocks;
        }

        /// <summary>True when block <paramref name="index"/> of the base had this hash.</summary>
        public bool Matches(int index, ReadOnlySpan<byte> hash)
            => index < _blocks.Count && hash.SequenceEqual(_blocks[index]);

        /// <summary>
        /// Build a manifest while streaming <paramref name="source"/> into <paramref name="destination"/>,
        /// so the periodic copy to the share hashes the bytes it is already moving.
        /// </summary>
        public static async Task<BlockManifest> CopyAndHashAsync(
            Stream source, Stream destination, int blockSize, CancellationToken cancellationToken)
        {
            var blocks = new List<byte[]>();
            var fileHash = new XxHash128();
            var buffer = new byte[blockSize];
            long total = 0;
            while (true)
            {
                var read = await FillAsync(source, buffer, cancellationToken);
                if (read == 0) break;
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                fileHash.Append(buffer.AsSpan(0, read));
                blocks.Add(XxHash128.Hash(buffer.AsSpan(0, read)));
                total += read;
                if (read < blockSize) break;
            }
            return new BlockManifest(blockSize, total, fileHash.GetCurrentHash(), blocks);
        }

        /// <summary>Hash a file the way <see cref="CopyAndHashAsync"/> does, without copying it.</summary>
        public static async Task<BlockManifest> HashFileAsync(string path, int blockSize, CancellationToken cancellationToken)
        {
            using var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.SequentialScan);
            return await CopyAndHashAsync(src, Stream.Null, blockSize, cancellationToken);
        }

        /// <summary>Read until the buffer is full or the stream ends.</summary>
        internal static async Task<int> FillAsync(Stream source, byte[] buffer, CancellationToken cancellationToken)
        {
            var filled = 0;
            while (filled < buffer.Length)
            {
                var n = await source.ReadAsync(buffer.AsMemory(filled), cancellationToken);
                if (n == 0) break;
                filled += n;
            }
            return filled;
        }
    }

    /// <summary>
    /// The shutdown artifact: the blocks of the live main file that differ from the base
    /// copy on the share, plus the live write-ahead log, in one sequential file next to
    /// the base (<c>storage.db.delta</c>). The next revision seeds from base + delta.
    ///
    /// Layout (little-endian):
    /// <code>
    ///   "RSSDELTA" int32 version=1 int32 blockSize int64 baseLength byte[16] baseHash int64 newLength
    ///   repeated: int64 blockIndex (>= 0) int32 length byte[length]
    ///   int64 -1 (end of blocks)
    ///   int64 walLength byte[walLength]
    ///   byte[16] XxHash128 of everything above
    /// </code>
    /// A delta is only applied to a base whose length and hash match its header, so a
    /// delta left behind by an earlier base (crash between publishing a new base and
    /// deleting the old delta) is ignored rather than applied to the wrong file.
    /// </summary>
    public static class DatabaseDelta
    {
        private static readonly byte[] Magic = "RSSDELTA"u8.ToArray();
        private const int Version = 1;
        private const long EndOfBlocks = -1;
        private const int HashSize = 16;
        /// <summary>A write-ahead log holds a 32-byte header before its first frame.</summary>
        private const int WalHeaderSize = 32;

        public sealed record WriteResult(long BytesWritten, int ChangedBlocks, int TotalBlocks, long WalBytes);

        /// <summary>
        /// Compare the live main file against <paramref name="manifest"/> and stream the
        /// changed blocks and the WAL (if any frames) to <paramref name="destination"/>.
        /// The caller holds a read transaction on the live database for the duration so
        /// the WAL cannot be reset underneath the read (see the backup service).
        /// </summary>
        public static async Task<WriteResult> WriteAsync(
            string liveMainPath, string liveWalPath, BlockManifest manifest, Stream destination,
            CancellationToken cancellationToken)
        {
            var hashed = new HashingStream(destination);
            var changed = 0;
            var total = 0;
            long walBytes = 0;
            using (var main = new FileStream(liveMainPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.SequentialScan))
            {
                var newLength = main.Length;
                await hashed.WriteAsync(Magic, cancellationToken);
                await WriteInt32Async(hashed, Version, cancellationToken);
                await WriteInt32Async(hashed, manifest.BlockSize, cancellationToken);
                await WriteInt64Async(hashed, manifest.Length, cancellationToken);
                await hashed.WriteAsync(manifest.FileHash, cancellationToken);
                await WriteInt64Async(hashed, newLength, cancellationToken);

                var buffer = new byte[manifest.BlockSize];
                var hash = new byte[HashSize];
                for (long index = 0; ; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = await BlockManifest.FillAsync(main, buffer, cancellationToken);
                    if (read == 0) break;
                    total++;
                    XxHash128.Hash(buffer.AsSpan(0, read), hash);
                    // A short last block never matches a full base block of the same index,
                    // and the length check at apply time truncates or extends the file.
                    if (!(read == manifest.BlockSize && manifest.Matches((int)index, hash)))
                    {
                        changed++;
                        await WriteInt64Async(hashed, index, cancellationToken);
                        await WriteInt32Async(hashed, read, cancellationToken);
                        await hashed.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    }
                    if (read < manifest.BlockSize) break;
                }
                await WriteInt64Async(hashed, EndOfBlocks, cancellationToken);
            }

            // The WAL is read after the main file: any page a concurrent checkpoint wrote
            // into the main file during the read above is also a frame in here, and frames
            // supersede main-file pages on replay.
            var wal = new FileInfo(liveWalPath);
            if (wal.Exists && wal.Length > WalHeaderSize)
            {
                using var walStream = new FileStream(liveWalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.SequentialScan);
                walBytes = walStream.Length;
                await WriteInt64Async(hashed, walBytes, cancellationToken);
                await walStream.CopyToAsync(hashed, 1024 * 1024, cancellationToken);
            }
            else
            {
                await WriteInt64Async(hashed, 0, cancellationToken);
            }

            await destination.WriteAsync(hashed.CurrentHash(), cancellationToken);
            return new WriteResult(hashed.BytesWritten + HashSize, changed, total, walBytes);
        }

        public enum ApplyOutcome
        {
            /// <summary>Blocks and WAL applied to the target file.</summary>
            Applied,
            /// <summary>The delta was computed against a different base; left the target alone.</summary>
            BaseMismatch,
            /// <summary>The delta's trailer hash or layout is invalid; left the target alone.</summary>
            Corrupt,
        }

        public sealed record ApplyResult(ApplyOutcome Outcome, int ChangedBlocks, long WalBytes, string? Detail = null);

        /// <summary>
        /// Apply a delta to <paramref name="targetMainPath"/>, which must be a byte-for-byte
        /// copy of the base, writing the shipped WAL to <paramref name="targetWalPath"/>.
        /// Verifies the trailer over the whole file before touching the target, so a
        /// truncated or torn delta changes nothing. <paramref name="baseLength"/> and
        /// <paramref name="baseHash"/> describe the copy the caller made of the base.
        /// </summary>
        public static async Task<ApplyResult> ApplyAsync(
            string deltaPath, long baseLength, byte[] baseHash, string targetMainPath, string targetWalPath,
            CancellationToken cancellationToken)
        {
            // Pass 1: integrity. The delta is read in full once for the trailer, then again
            // to apply; it is a local file by the time this runs (the seeder copies it off
            // the share first), so the second pass is cheap.
            byte[] expected;
            using (var file = new FileStream(deltaPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan))
            {
                if (file.Length < Magic.Length + 4 + 4 + 8 + HashSize + 8 + 8 + 8 + HashSize)
                    return new ApplyResult(ApplyOutcome.Corrupt, 0, 0, "file too short");
                var body = file.Length - HashSize;
                var hasher = new XxHash128();
                var buffer = new byte[1024 * 1024];
                long remaining = body;
                while (remaining > 0)
                {
                    var n = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
                    if (n == 0) return new ApplyResult(ApplyOutcome.Corrupt, 0, 0, "short read");
                    hasher.Append(buffer.AsSpan(0, n));
                    remaining -= n;
                }
                expected = new byte[HashSize];
                await file.ReadExactlyAsync(expected, cancellationToken);
                if (!hasher.GetCurrentHash().AsSpan().SequenceEqual(expected))
                    return new ApplyResult(ApplyOutcome.Corrupt, 0, 0, "trailer hash mismatch");
            }

            // Pass 2: header checks, then apply.
            using var delta = new FileStream(deltaPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
            var magic = new byte[Magic.Length];
            await delta.ReadExactlyAsync(magic, cancellationToken);
            if (!magic.AsSpan().SequenceEqual(Magic)) return new ApplyResult(ApplyOutcome.Corrupt, 0, 0, "bad magic");
            var version = await ReadInt32Async(delta, cancellationToken);
            if (version != Version) return new ApplyResult(ApplyOutcome.Corrupt, 0, 0, $"unknown version {version}");
            var blockSize = await ReadInt32Async(delta, cancellationToken);
            if (blockSize <= 0 || blockSize > 64 * 1024 * 1024) return new ApplyResult(ApplyOutcome.Corrupt, 0, 0, $"bad block size {blockSize}");
            var headerBaseLength = await ReadInt64Async(delta, cancellationToken);
            var headerBaseHash = new byte[HashSize];
            await delta.ReadExactlyAsync(headerBaseHash, cancellationToken);
            var newLength = await ReadInt64Async(delta, cancellationToken);
            if (headerBaseLength != baseLength || !headerBaseHash.AsSpan().SequenceEqual(baseHash))
                return new ApplyResult(ApplyOutcome.BaseMismatch, 0, 0,
                    $"delta expects a base of {headerBaseLength:N0} bytes, the share copy is {baseLength:N0} bytes" +
                    (headerBaseLength == baseLength ? " with a different hash" : ""));

            var changed = 0;
            using (var target = new FileStream(targetMainPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var block = new byte[blockSize];
                while (true)
                {
                    var index = await ReadInt64Async(delta, cancellationToken);
                    if (index == EndOfBlocks) break;
                    var length = await ReadInt32Async(delta, cancellationToken);
                    if (index < 0 || length <= 0 || length > blockSize || index * blockSize + length > newLength)
                        throw new InvalidDataException($"Delta block {index} of {length} bytes is outside the new file length {newLength}.");
                    await delta.ReadExactlyAsync(block.AsMemory(0, length), cancellationToken);
                    target.Position = index * (long)blockSize;
                    await target.WriteAsync(block.AsMemory(0, length), cancellationToken);
                    changed++;
                }
                target.SetLength(newLength);
                target.Flush(flushToDisk: true);
            }

            var walLength = await ReadInt64Async(delta, cancellationToken);
            if (walLength > 0)
            {
                using var wal = new FileStream(targetWalPath, FileMode.Create, FileAccess.Write, FileShare.None);
                var buffer = new byte[1024 * 1024];
                long remaining = walLength;
                while (remaining > 0)
                {
                    var n = await delta.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
                    if (n == 0) throw new InvalidDataException("Delta ends inside the WAL section.");
                    await wal.WriteAsync(buffer.AsMemory(0, n), cancellationToken);
                    remaining -= n;
                }
                wal.Flush(flushToDisk: true);
            }
            return new ApplyResult(ApplyOutcome.Applied, changed, walLength);
        }

        private static async Task WriteInt32Async(Stream s, int value, CancellationToken ct)
        {
            var b = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(b, value);
            await s.WriteAsync(b, ct);
        }

        private static async Task WriteInt64Async(Stream s, long value, CancellationToken ct)
        {
            var b = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(b, value);
            await s.WriteAsync(b, ct);
        }

        private static async Task<int> ReadInt32Async(Stream s, CancellationToken ct)
        {
            var b = new byte[4];
            await s.ReadExactlyAsync(b, ct);
            return BinaryPrimitives.ReadInt32LittleEndian(b);
        }

        private static async Task<long> ReadInt64Async(Stream s, CancellationToken ct)
        {
            var b = new byte[8];
            await s.ReadExactlyAsync(b, ct);
            return BinaryPrimitives.ReadInt64LittleEndian(b);
        }

        /// <summary>Write-through stream that hashes what passes, for the trailer.</summary>
        private sealed class HashingStream : Stream
        {
            private readonly Stream _inner;
            private readonly XxHash128 _hash = new();
            public long BytesWritten { get; private set; }
            public HashingStream(Stream inner) => _inner = inner;
            public byte[] CurrentHash() => _hash.GetCurrentHash();

            public override void Write(byte[] buffer, int offset, int count)
            {
                _inner.Write(buffer, offset, count);
                _hash.Append(buffer.AsSpan(offset, count));
                BytesWritten += count;
            }

            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                await _inner.WriteAsync(buffer, cancellationToken);
                _hash.Append(buffer.Span);
                BytesWritten += buffer.Length;
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override void Flush() => _inner.Flush();
            public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
