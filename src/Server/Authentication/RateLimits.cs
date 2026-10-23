#nullable enable
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using RssApp.Config;

namespace Server.Authentication;

/// <summary>
/// Conservative per-client limits on the endpoints that are cheap to call
/// and expensive, annoying or abusable when hammered: the login/logout
/// redirects (per client address) and adding feeds or importing OPML (per
/// user). Everything else is bounded by authentication already.
/// </summary>
public static class RateLimits
{
    public const string AuthPolicy = "auth";
    public const string AddFeedPolicy = "add-feed";

    public static IServiceCollection AddRssRateLimiting(this IServiceCollection services, AuthConfig auth)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (ctx, _) =>
            {
                ctx.HttpContext.Response.Headers.RetryAfter = "60";
                return ValueTask.CompletedTask;
            };

            options.AddPolicy(AuthPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
                ClientAddress(ctx),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = Math.Max(1, auth.LoginRateLimitPerMinute),
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));

            options.AddPolicy(AddFeedPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? ClientAddress(ctx),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = Math.Max(1, auth.AddFeedRateLimitPerMinute),
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
        });
        return services;
    }

    /// <summary>
    /// The caller's address as seen after the forwarded-headers middleware
    /// (Container Apps ingress sets X-Forwarded-For). Falls back to one shared
    /// bucket when no address is known, which only happens in-process.
    /// </summary>
    private static string ClientAddress(HttpContext ctx) =>
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
