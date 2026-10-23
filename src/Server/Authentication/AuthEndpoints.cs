#nullable enable
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using RssApp.Config;

namespace Server.Authentication;

/// <summary>
/// /auth/* routes. Login and logout are top-level navigations from the
/// frontend; /auth/me and /auth/providers are credentialed fetches.
/// </summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app, AuthConfig auth)
    {
        var group = app.MapGroup("/auth").AllowAnonymous();

        group.MapGet("/providers", () =>
            Results.Json(ConfiguredProviders(auth)));

        group.MapGet("/login/{provider}", (string provider, string? returnUrl, HttpContext ctx) =>
        {
            if (!IsEnabled(auth, provider))
                return Results.NotFound(new { error = $"Login provider '{provider}' is not configured" });

            var target = ReturnUrlValidator.Resolve(returnUrl, auth.AllowedFrontendOrigins);
            var scheme = AuthenticationSetup.SchemeFor(provider);
            return Results.Challenge(LoginProperties(target), new[] { scheme });
        }).RequireRateLimiting(RateLimits.AuthPolicy);

        // The provider handlers own /auth/callback/{provider}; nothing to map here.

        group.MapGet("/logout", async (string? returnUrl, HttpContext ctx) =>
        {
            var target = ReturnUrlValidator.Resolve(returnUrl, auth.AllowedFrontendOrigins);
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect(target);
        }).RequireRateLimiting(RateLimits.AuthPolicy);

        group.MapGet("/me", async (HttpContext ctx) =>
        {
            // Authenticate explicitly: the group is anonymous, but the default
            // scheme still populates ctx.User when the cookie (or, during the
            // migration, the proxy's headers) is present.
            var result = await ctx.AuthenticateAsync();
            var body = ClientPrincipalResponse.From(result.Succeeded ? result.Principal : null);
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Json(body);
        });

        return app;
    }

    /// <summary>
    /// Properties the provider handler carries through to the cookie sign-in.
    /// IsPersistent makes the cookie carry an Expires (= Auth:SessionLifetime,
    /// sliding) instead of dying with the browser window, so people stay
    /// signed in between visits. Logout still deletes the cookie outright.
    /// </summary>
    public static AuthenticationProperties LoginProperties(string redirectUri) =>
        new() { RedirectUri = redirectUri, IsPersistent = true };

    public static IReadOnlyList<string> ConfiguredProviders(AuthConfig auth) =>
        UserIdentity.KnownProviders.Where(p => IsEnabled(auth, p)).ToList();

    private static bool IsEnabled(AuthConfig auth, string provider) =>
        UserIdentity.IsKnownProvider(provider)
        && auth.Providers.TryGetValue(provider, out var p)
        && p.IsConfigured;
}
