#nullable enable
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace RssReader.Server.Services
{
    /// <summary>
    /// The writer's boot handover (issue #136).
    ///
    /// During a deploy, Container Apps only sends the old revision SIGTERM once the new
    /// one's startup probe passes, and the old revision writes its final database copy to
    /// the share in response to that SIGTERM. So the new revision must answer
    /// <c>/api/healthz</c> <em>before</em> it can seed <c>/tmp/storage.db</c>. The real app
    /// cannot do that: its repositories open the live database while the host is being
    /// built. This gate runs first instead: a throwaway Kestrel host on the app's port that
    /// answers the probe with 200 and everything else with 503, while the process waits for
    /// the old writer's lock to be released (or go stale), takes the lock, and seeds the
    /// live database from the share. Then the gate stops and <c>Program.cs</c> builds the
    /// real app, which keeps the lock until its own shutdown flush.
    /// </summary>
    public static class WriterStartupGate
    {
        /// <summary>
        /// Run the handover. Returns with the lock held and the live DB seeded. Throws
        /// <see cref="OperationCanceledException"/> when the process is told to stop while
        /// waiting (the lock is released first).
        /// </summary>
        public static async Task RunAsync(
            string[] args,
            DatabaseBackupToFilePaths paths,
            ShareWriterLock writerLock,
            string[] allowedOrigins,
            ILoggerFactory loggerFactory)
        {
            var logger = loggerFactory.CreateLogger("WriterStartupGate");

            using var stopping = new CancellationTokenSource();
            using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; stopping.Cancel(); });
            using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => { ctx.Cancel = true; stopping.Cancel(); });

            var builder = WebApplication.CreateBuilder(args);
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole();
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            var gate = builder.Build();

            gate.MapGet("/api/healthz", () => Results.Ok(new { status = "healthy", role = "writer", phase = "seeding" }));
            gate.MapFallback(async ctx =>
            {
                // CORS enough for the browser to see the status rather than a network error.
                var origin = ctx.Request.Headers.Origin.ToString();
                if (!string.IsNullOrEmpty(origin) && allowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
                {
                    ctx.Response.Headers.AccessControlAllowOrigin = origin;
                    ctx.Response.Headers.AccessControlAllowCredentials = "true";
                    ctx.Response.Headers.Vary = "Origin";
                }
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                ctx.Response.Headers.RetryAfter = "5";
                await ctx.Response.WriteAsync("Starting: waiting for the previous revision's final database copy.");
            });

            await gate.StartAsync(stopping.Token);
            logger.LogInformation(
                "Startup gate listening: /api/healthz answers while the database is seeded from {Backup}.",
                paths.BackupDbPath);

            try
            {
                await DatabaseSeeder.WaitForLockAndSeedAsync(paths, writerLock, logger, stopping.Token);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                logger.LogWarning("Stop requested while waiting to seed the database — exiting without starting.");
                writerLock.Release();
                throw;
            }
            catch
            {
                writerLock.Release();
                throw;
            }
            finally
            {
                // A short gap on the port follows while the real host builds; the
                // liveness probe (30s period, 3 failures) rides it out.
                await gate.StopAsync();
                await gate.DisposeAsync();
            }
        }
    }
}
