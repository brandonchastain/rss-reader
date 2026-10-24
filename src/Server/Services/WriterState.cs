#nullable enable
using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace RssReader.Server.Services
{
    /// <summary>
    /// Whether this process still owns the database for writing. Flips once, when a
    /// handoff request from the next revision has been served (<see cref="WriterHandover"/>):
    /// from then on the feed scheduler and worker are stopped, Litestream has synced and
    /// exited, the lease is released, and mutating API requests get 503 until the platform
    /// stops this revision. Reads keep working from the local copy.
    /// </summary>
    public sealed class WriterState
    {
        private int _handedOver;
        private readonly System.Threading.CancellationTokenSource _writersStopping = new();

        public bool IsHandedOver => _handedOver == 1;

        /// <summary>
        /// Cancelled when a handoff begins. The feed scheduler, worker and retention service
        /// link it with their host stopping token and exit their loops on it, so the handover
        /// can stop them without <c>IHostedService.StopAsync</c>, whose cancellation the host
        /// treats as a background-service failure and answers by stopping the whole host.
        /// </summary>
        public System.Threading.CancellationToken WritersStopping => _writersStopping.Token;

        public void RequestWritersStop() => _writersStopping.Cancel();
        public DateTimeOffset? HandedOverAtUtc { get; private set; }
        public string? HandedOverTo { get; private set; }
        public long? FinalTxid { get; private set; }

        /// <summary>Refuse writes from now on. Returns false if already handed over.</summary>
        public bool MarkHandedOver(string to, long? finalTxid)
        {
            if (System.Threading.Interlocked.Exchange(ref _handedOver, 1) == 1) return false;
            HandedOverAtUtc = DateTimeOffset.UtcNow;
            HandedOverTo = to;
            FinalTxid = finalTxid;
            return true;
        }

        public static bool IsMutating(string method) =>
            !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method));

        /// <summary>Middleware: 503 for mutating requests once handed over (spec 3.2 v2 step 2).</summary>
        public static Func<HttpContext, RequestDelegate, Task> Middleware(WriterState state) => async (ctx, next) =>
        {
            if (state.IsHandedOver && IsMutating(ctx.Request.Method))
            {
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                ctx.Response.Headers.RetryAfter = "5";
                await ctx.Response.WriteAsJsonAsync(new
                {
                    error = "This revision has handed the database over to the next one; retry in a few seconds.",
                    handedOverAtUtc = state.HandedOverAtUtc,
                });
                return;
            }
            await next(ctx);
        };
    }
}
