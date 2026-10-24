#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace RssReader.Server.Services
{
    /// <summary>
    /// Boot-time copy of the share's database (<c>/data/db/storage.db</c>) to the live path
    /// on local disk (<c>/tmp/storage.db</c>). Replaces the <c>cp</c> that
    /// <c>docker-entrypoint.sh</c> used to run before starting the app, so that the copy can
    /// wait for the previous writer revision's final backup (issue #136).
    /// </summary>
    public static class DatabaseSeeder
    {
        /// <summary>
        /// Wait until no other writer holds a live lock on the share, take the lock, then
        /// copy the share's database over the live path. Returns once the live DB is in
        /// place and the lock is held; the caller keeps the lock for the process lifetime.
        /// </summary>
        /// <returns>True when the live DB was replaced from the share; false when the share
        /// has no copy yet (first boot) and the live path was left as is.</returns>
        public static async Task<bool> WaitForLockAndSeedAsync(
            DatabaseBackupToFilePaths paths,
            ShareWriterLock writerLock,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            // False means the lock directory is unwritable (self-host misconfiguration):
            // already logged; seed anyway rather than refuse to boot.
            await writerLock.AcquireAsync(cancellationToken);
            return await SeedFromShareAsync(paths, logger, cancellationToken);
        }

        /// <summary>
        /// Copy the share's database over the live path, validating the copy with
        /// <c>PRAGMA quick_check</c> before it replaces anything. Caller must hold the lock.
        /// </summary>
        public static async Task<bool> SeedFromShareAsync(
            DatabaseBackupToFilePaths paths, ILogger logger, CancellationToken cancellationToken)
        {
            if (!File.Exists(paths.BackupDbPath))
            {
                logger.LogWarning(
                    "No database copy at {Backup} — starting from the local database (first boot?).",
                    paths.BackupDbPath);
                return false;
            }

            var activeDir = Path.GetDirectoryName(paths.ActiveDbPath);
            if (!string.IsNullOrEmpty(activeDir)) Directory.CreateDirectory(activeDir);

            // Stage next to the live file so the final step is a same-volume rename.
            var stagePath = $"{paths.ActiveDbPath}.seed.{Guid.NewGuid():N}";
            var deltaLocalPath = stagePath + ".delta";
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                BlockManifest baseCopy;
                using (var src = new FileStream(paths.BackupDbPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan))
                using (var dst = new FileStream(stagePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024))
                {
                    // Hashed on the way: the delta below names the base it was computed on.
                    baseCopy = await BlockManifest.CopyAndHashAsync(src, dst, BlockManifest.DefaultBlockSize, cancellationToken);
                }

                var applied = await ApplyDeltaAsync(paths, stagePath, deltaLocalPath, baseCopy, logger, cancellationToken);

                var (ok, result, error) = DatabaseBackupToFileService.QuickCheckDiagnostic(stagePath);
                if (!ok)
                {
                    throw new InvalidOperationException(
                        $"PRAGMA quick_check failed on the seeded database copied from '{paths.BackupDbPath}'" +
                        (applied ? $" with '{paths.DeltaPath}' applied" : "") +
                        $" (result='{result ?? "<null>"}', error='{error ?? "<none>"}'). " +
                        "Refusing to start; restore a known-good copy to the share and redeploy.");
                }

                // The staged file is complete on its own (any shipped WAL was folded in
                // above); drop the sidecars of a previous live file so SQLite does not
                // replay a foreign WAL onto it, and the stage's own empty ones.
                SafeDelete(paths.ActiveDbPath + "-wal");
                SafeDelete(paths.ActiveDbPath + "-shm");
                File.Move(stagePath, paths.ActiveDbPath, overwrite: true);

                var size = new FileInfo(paths.ActiveDbPath).Length;
                logger.LogInformation(
                    "Seeded {Active} from {Backup}{Delta}: {Bytes:N0} bytes in {Ms} ms (quick_check ok).",
                    paths.ActiveDbPath, paths.BackupDbPath, applied ? " + delta" : "", size, sw.ElapsedMilliseconds);
                return true;
            }
            finally
            {
                SafeDelete(stagePath);
                SafeDelete(stagePath + "-wal");
                SafeDelete(stagePath + "-shm");
                SafeDelete(deltaLocalPath);
            }
        }

        /// <summary>
        /// Apply the previous writer's shutdown delta (<see cref="DatabaseBackupToFilePaths.DeltaPath"/>)
        /// to the staged base copy, then fold its WAL into the main file so the stage is a
        /// single complete file. Returns false, leaving the stage as the plain base, when
        /// there is no delta or it does not belong to this base (left over from an earlier
        /// one). A delta that fails its own integrity check is an error worth a loud log but
        /// not a refusal to start: the base alone is the state of the last periodic copy.
        /// </summary>
        private static async Task<bool> ApplyDeltaAsync(
            DatabaseBackupToFilePaths paths, string stagePath, string deltaLocalPath, BlockManifest baseCopy,
            ILogger logger, CancellationToken cancellationToken)
        {
            if (!File.Exists(paths.DeltaPath)) return false;

            // One sequential read off the share; the apply then reads the local copy twice.
            using (var src = new FileStream(paths.DeltaPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan))
            using (var dst = new FileStream(deltaLocalPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024))
            {
                await src.CopyToAsync(dst, 1024 * 1024, cancellationToken);
            }

            var apply = await DatabaseDelta.ApplyAsync(
                deltaLocalPath, baseCopy.Length, baseCopy.FileHash, stagePath, stagePath + "-wal", cancellationToken);
            switch (apply.Outcome)
            {
                case DatabaseDelta.ApplyOutcome.BaseMismatch:
                    logger.LogWarning(
                        "Ignoring {Delta}: it belongs to an earlier base copy ({Detail}).", paths.DeltaPath, apply.Detail);
                    return false;
                case DatabaseDelta.ApplyOutcome.Corrupt:
                    logger.LogError(
                        "Ignoring {Delta}: it failed its integrity check ({Detail}). Seeding from the base copy alone; " +
                        "writes between the last periodic backup and the previous shutdown are lost.",
                        paths.DeltaPath, apply.Detail);
                    return false;
            }

            if (apply.WalBytes > 0)
            {
                // Fold the shipped frames into the main file. A read-write open replays the
                // WAL; TRUNCATE leaves it empty, so the stage is complete as one file.
                using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={stagePath};Pooling=False");
                conn.Open();
                using var checkpoint = conn.CreateCommand();
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                checkpoint.ExecuteNonQuery();
                conn.Close();
                if (DatabaseBackupToFileService.WalHoldsFrames(stagePath))
                {
                    throw new InvalidOperationException(
                        $"The write-ahead log shipped in '{paths.DeltaPath}' could not be folded into the seeded database. " +
                        "Refusing to start; restore a known-good copy to the share and redeploy.");
                }
            }

            logger.LogInformation(
                "Applied {Delta}: {Blocks} changed blocks and {Wal:N0} WAL bytes on top of the base copy.",
                paths.DeltaPath, apply.ChangedBlocks, apply.WalBytes);
            return true;
        }

        private static void SafeDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
