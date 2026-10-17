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
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                using (var src = new FileStream(paths.BackupDbPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var dst = new FileStream(stagePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await src.CopyToAsync(dst, 1024 * 1024, cancellationToken);
                }

                var (ok, result, error) = DatabaseBackupToFileService.QuickCheckDiagnostic(stagePath);
                if (!ok)
                {
                    throw new InvalidOperationException(
                        $"PRAGMA quick_check failed on the seeded database copied from '{paths.BackupDbPath}' " +
                        $"(result='{result ?? "<null>"}', error='{error ?? "<none>"}'). " +
                        "Refusing to start; restore a known-good copy to the share and redeploy.");
                }

                // The share copy is a VACUUM INTO product (no WAL); drop any sidecars of a
                // previous live file so SQLite does not replay a foreign WAL onto it.
                SafeDelete(paths.ActiveDbPath + "-wal");
                SafeDelete(paths.ActiveDbPath + "-shm");
                File.Move(stagePath, paths.ActiveDbPath, overwrite: true);

                var size = new FileInfo(paths.ActiveDbPath).Length;
                logger.LogInformation(
                    "Seeded {Active} from {Backup}: {Bytes:N0} bytes in {Ms} ms (quick_check ok).",
                    paths.ActiveDbPath, paths.BackupDbPath, size, sw.ElapsedMilliseconds);
                return true;
            }
            finally
            {
                SafeDelete(stagePath);
            }
        }

        private static void SafeDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
