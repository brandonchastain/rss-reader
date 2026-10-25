#nullable enable
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace RssReader.Server.Services
{
    /// <summary>
    /// The writer's boot handover (issue #136).
    ///
    /// During a deploy, Container Apps only sends the old revision SIGTERM once the new
    /// one's startup probe passes, and the old revision hands the database over in response
    /// (a cooperative handoff request, or its SIGTERM). So the new revision must answer
    /// <c>/api/healthz</c> <em>before</em> it can restore <c>/tmp/storage.db</c>. The real app
    /// cannot do that: its repositories open the live database while the host is being
    /// built. This gate runs first instead: a throwaway Kestrel host on the app's port that
    /// answers the probe and 503 for everything else, while the process restores the live
    /// database from the Litestream replica once it holds the blob lease. Then the gate
    /// stops and <c>Program.cs</c> builds the real app.
    ///
    /// Only runs when restore from the replica is configured; a self-hosted single
    /// container with no storage account simply creates its database on first use.
    /// </summary>
    public sealed class WriterStartupGate : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private WriterStartupGate(WebApplication app) => _app = app;

        /// <summary>Addresses the gate listens on (resolved, so port 0 shows the real port).</summary>
        public string[] Addresses =>
            _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.ToArray()
            ?? Array.Empty<string>();

        /// <summary>
        /// Start the throwaway listener. <paramref name="urls"/> overrides the app's
        /// configured URLs (tests pass an ephemeral port); null uses the same binding the
        /// real app will use, so the platform's probe reaches it.
        /// </summary>
        public static async Task<WriterStartupGate> StartAsync(
            string[] args, string[] allowedOrigins, string? urls, CancellationToken cancellationToken,
            Func<bool>? readyForTraffic = null)
        {
            var builder = WebApplication.CreateBuilder(args);
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole();
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            if (urls != null) builder.WebHost.UseUrls(urls);
            var gate = builder.Build();

            // The startup probe. With a cooperative handover (readyForTraffic given) it
            // answers 503 until the initial restore is on disk, so the platform moves
            // traffic here only when the real host is seconds away; the caller bounds that
            // window well inside the probe's budget (2 s * 150). Without one, 200 at once:
            // the old revision's SIGTERM, and so its final sync, depends on it.
            gate.MapGet("/api/healthz", () =>
                readyForTraffic == null || readyForTraffic()
                    ? Results.Ok(new { status = "healthy", role = "writer", phase = "seeding" })
                    : Results.Json(new { status = "starting", role = "writer", phase = "restoring" }, statusCode: StatusCodes.Status503ServiceUnavailable));
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
                await ctx.Response.WriteAsync("Starting: restoring the database from the replica.");
            });

            await gate.StartAsync(cancellationToken);
            return new WriterStartupGate(gate);
        }

        /// <summary>Stop listening and free the port for the real host.</summary>
        public async Task StopAsync()
        {
            await _app.StopAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await _app.DisposeAsync();
        }

        /// <summary>
        /// Run the whole handover: start the gate, run <paramref name="restoreFromReplica"/>,
        /// stop the gate. Throws <see cref="InvalidOperationException"/> when the restore
        /// reports failure (the container then exits and the platform restarts it) and
        /// <see cref="OperationCanceledException"/> when the process is told to stop meanwhile.
        /// </summary>
        public static async Task RunAsync(
            string[] args,
            Func<CancellationToken, Task<bool>> restoreFromReplica,
            string[] allowedOrigins,
            ILoggerFactory loggerFactory,
            string? urls = null,
            CancellationToken cancellationToken = default,
            Func<bool>? readyForTraffic = null)
        {
            var logger = loggerFactory.CreateLogger("WriterStartupGate");

            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; stopping.Cancel(); });
            using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => { ctx.Cancel = true; stopping.Cancel(); });

            var gate = await StartAsync(args, allowedOrigins, urls, stopping.Token, readyForTraffic);
            logger.LogInformation("Startup gate listening: /api/healthz answers while the database is restored from the Litestream replica.");

            try
            {
                if (!await restoreFromReplica(stopping.Token))
                {
                    throw new InvalidOperationException(
                        "The database could not be restored from the Litestream replica (see the errors above). " +
                        "Refusing to start; the platform restarts the container and the restore is tried again.");
                }
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                logger.LogWarning("Stop requested while restoring the database — exiting without starting.");
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
