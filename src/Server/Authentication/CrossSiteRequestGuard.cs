#nullable enable
using Microsoft.AspNetCore.Http;
using RssApp.Config;

namespace Server.Authentication;

/// <summary>
/// Cross-site request forgery guard for a cookie-authenticated API whose
/// frontend lives on another origin.
///
/// The session cookie is SameSite=None, so the browser attaches it to any
/// request to the API, including ones a hostile page makes. CORS only stops
/// requests that need a preflight; a plain HTML form POST or an image GET
/// goes straight through with the cookie. Browsers do, however, always put the
/// page's origin in the <c>Origin</c> header of a cross-origin POST, PUT or
/// DELETE (and send the literal <c>null</c> when the origin is opaque). So any
/// state-changing request that carries an <c>Origin</c> outside the allowed
/// frontend origins is refused before it reaches authentication.
///
/// Requests with no <c>Origin</c> at all are let through: those are not
/// browser cross-site requests (curl, the in-process tests, server-to-server
/// calls), and the GET routes that used to change state have been removed so
/// an image tag cannot do anything.
/// </summary>
public sealed class CrossSiteRequestGuard
{
    private readonly RequestDelegate next;
    private readonly HashSet<string> allowedOrigins;

    public CrossSiteRequestGuard(RequestDelegate next, RssAppConfig config)
    {
        this.next = next;
        this.allowedOrigins = new HashSet<string>(
            config.Auth.AllowedFrontendOrigins.Select(ReturnUrlValidator.NormalizeOrigin),
            StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsSafeMethod(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);

    /// <summary>
    /// Decides whether a request is a forbidden cross-site write. Exposed for
    /// unit tests; the middleware calls it with the request's method and
    /// <c>Origin</c> header value (null when absent).
    /// </summary>
    public static bool IsForbidden(string method, string? origin, IReadOnlySet<string> allowedOrigins)
    {
        if (IsSafeMethod(method)) return false;
        if (string.IsNullOrEmpty(origin)) return false;
        return !allowedOrigins.Contains(ReturnUrlValidator.NormalizeOrigin(origin));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var origin = context.Request.Headers.Origin.Count > 0 ? context.Request.Headers.Origin.ToString() : null;
        if (IsForbidden(context.Request.Method, origin, this.allowedOrigins))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error\":\"Cross-site request refused.\"}");
            return;
        }

        await this.next(context);
    }
}
