using Microsoft.AspNetCore.Http;

namespace RssReader.Server.Services;

/// <summary>
/// How many user requests the server is answering right now. Background work
/// (the feed refresher) reads it to step aside while someone is waiting on the
/// single half-core. One Interlocked counter; the cost per request is two atomic
/// increments, nothing is allocated.
/// </summary>
public sealed class RequestActivity
{
    private int inFlight;

    /// <summary>Requests currently inside the pipeline, excluding the health probe.</summary>
    public int InFlight => Volatile.Read(ref this.inFlight);

    public void Enter() => Interlocked.Increment(ref this.inFlight);

    public void Exit() => Interlocked.Decrement(ref this.inFlight);
}

/// <summary>
/// Counts requests in and out of the pipeline. Registered first in Program.cs so a
/// request is counted for its whole life, whatever later middleware does with it.
/// The health probe is not counted: it fires on a timer whether or not anyone is
/// using the site, and must not hold the refresher back on an idle replica.
/// </summary>
public sealed class RequestActivityMiddleware
{
    private readonly RequestDelegate next;
    private readonly RequestActivity activity;

    public RequestActivityMiddleware(RequestDelegate next, RequestActivity activity)
    {
        this.next = next;
        this.activity = activity;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (IsHealthProbe(context.Request.Path))
        {
            await this.next(context);
            return;
        }

        this.activity.Enter();
        try
        {
            await this.next(context);
        }
        finally
        {
            this.activity.Exit();
        }
    }

    internal static bool IsHealthProbe(PathString path)
        => path.Equals("/api/healthz", StringComparison.OrdinalIgnoreCase);
}
