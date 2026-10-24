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
    /// Boot-time restore of the live database from the Litestream blob replica
    /// (BLOB-DURABILITY-SPEC.md 3.2 steps 4 and 5, rollout step 3). The caller holds the
    /// blob lease first, so the previous writer's final sync (3.3 step 4) is in the replica.
    ///
    /// The restore goes to a staging path beside the live file, is checked with
    /// <c>PRAGMA quick_check</c>, and only then renamed over the live path, the same shape
    /// as <see cref="DatabaseSeeder.SeedFromShareAsync"/>. Every failure returns false after
    /// an error log so the caller can fall back to the share copy; nothing here is fatal
    /// while the share is still written (rollout steps 3 and 4).
    /// </summary>
    public static class BlobRestoreSeeder
    {
        public static async Task<bool> RestoreFromReplicaAsync(
            DurabilityConfig config, string activeDbPath, LitestreamCli cli, ILogger logger, CancellationToken cancellationToken)
        {
            var activeDir = Path.GetDirectoryName(activeDbPath);
            if (!string.IsNullOrEmpty(activeDir)) Directory.CreateDirectory(activeDir);

            var stagePath = $"{activeDbPath}.restore.{Guid.NewGuid():N}";
            var sw = Stopwatch.StartNew();
            try
            {
                logger.LogInformation(
                    "Restoring {Active} from the Litestream replica ({Config}) into {Stage}; timeout {Timeout}.",
                    activeDbPath, config.LitestreamConfigPath, stagePath, config.RestoreTimeout);

                var (result, output) = await cli.RestoreAsync(
                    config.LitestreamConfigPath, activeDbPath, stagePath, config.RestoreTimeout, cancellationToken);
                switch (result)
                {
                    case LitestreamSyncResult.TimedOut:
                        logger.LogError(
                            "Startup: litestream restore did not finish within {Timeout}; falling back to the share copy.",
                            config.RestoreTimeout);
                        return false;
                    case LitestreamSyncResult.Failed:
                        logger.LogError(
                            "Startup: litestream restore failed after {Ms} ms; falling back to the share copy. Output: {Output}",
                            sw.ElapsedMilliseconds, output);
                        return false;
                }

                if (!File.Exists(stagePath))
                {
                    logger.LogError(
                        "Startup: litestream restore exited 0 but wrote nothing to {Stage} (no replica?); falling back to the share copy. Output: {Output}",
                        stagePath, output);
                    return false;
                }

                var (ok, check, error) = DatabaseBackupToFileService.QuickCheckDiagnostic(stagePath);
                if (!ok)
                {
                    logger.LogError(
                        "Startup: PRAGMA quick_check failed on the database restored from the replica (result='{Result}', error='{Error}'); falling back to the share copy.",
                        check ?? "<null>", error ?? "<none>");
                    return false;
                }

                // A restore is a clean database with no WAL; drop sidecars of any previous
                // live file so SQLite does not replay a foreign WAL onto it.
                SafeDelete(activeDbPath + "-wal");
                SafeDelete(activeDbPath + "-shm");
                File.Move(stagePath, activeDbPath, overwrite: true);

                var size = new FileInfo(activeDbPath).Length;
                logger.LogInformation(
                    "Startup: restored {Bytes:N0} bytes from the Litestream replica to {Active} in {Ms} ms (quick_check ok).",
                    size, activeDbPath, sw.ElapsedMilliseconds);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Startup: restore from the Litestream replica threw after {Ms} ms; falling back to the share copy.", sw.ElapsedMilliseconds);
                return false;
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
