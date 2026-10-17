#nullable enable
using System.Security.Claims;
using AspNet.Security.OAuth.GitHub;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using RssApp.Config;

namespace Server.Authentication;

/// <summary>
/// Registers the session cookie, one remote scheme per configured provider,
/// and (behind a flag) the Static Web Apps header scheme. Which one handles a
/// request is decided per request by a policy scheme.
/// </summary>
public static class AuthenticationSetup
{
    public const string PolicyScheme = "RssAuth";

    public static string SchemeFor(string provider) => "ext-" + provider.ToLowerInvariant();

    public static IServiceCollection AddRssAuthentication(this IServiceCollection services, RssAppConfig config)
    {
        var auth = config.Auth;

        if (!string.IsNullOrWhiteSpace(auth.DataProtectionKeysPath))
        {
            Directory.CreateDirectory(auth.DataProtectionKeysPath);
            services.AddDataProtection()
                .SetApplicationName("rss-reader")
                .PersistKeysToFileSystem(new DirectoryInfo(auth.DataProtectionKeysPath));
        }

        var builder = services
            .AddAuthorization()
            .AddAuthentication(PolicyScheme);

        // Per-request selector. The proxy's gateway header means an SWA-issued
        // principal; the test-user mode lives in the SWA handler too. Otherwise
        // the request is a browser call carrying (or lacking) the API cookie.
        builder.AddPolicyScheme(PolicyScheme, PolicyScheme, options =>
        {
            options.ForwardDefaultSelector = ctx =>
            {
                if (config.IsTestUserEnabled)
                    return StaticWebAppsAuthenticationHandler.AuthenticationScheme;
                if (auth.EnableStaticWebAppsHeaders && ctx.Request.Headers.ContainsKey("X-Gateway-Key"))
                    return StaticWebAppsAuthenticationHandler.AuthenticationScheme;
                return CookieAuthenticationDefaults.AuthenticationScheme;
            };
        });

        if (auth.EnableStaticWebAppsHeaders || config.IsTestUserEnabled)
        {
            builder.AddScheme<StaticWebAppsAuthenticationOptions, StaticWebAppsAuthenticationHandler>(
                StaticWebAppsAuthenticationHandler.AuthenticationScheme,
                options => options.IsTestUserEnabled = config.IsTestUserEnabled);
        }

        builder.AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
        {
            options.Cookie.Name = auth.CookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = auth.CookieSameSite;
            // SameSite=None is only honoured with Secure. Browsers treat
            // localhost as a secure context, so this also works for local http.
            options.Cookie.SecurePolicy = auth.CookieSameSite == SameSiteMode.None
                ? CookieSecurePolicy.Always
                : CookieSecurePolicy.SameAsRequest;
            if (!string.IsNullOrWhiteSpace(auth.CookieDomain))
                options.Cookie.Domain = auth.CookieDomain;
            options.ExpireTimeSpan = auth.SessionLifetime;
            options.SlidingExpiration = true;

            // An API never redirects a fetch to a login page.
            options.Events.OnRedirectToLogin = ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        });

        foreach (var (provider, p) in auth.Providers)
        {
            if (!UserIdentity.IsKnownProvider(provider) || !p.IsConfigured)
                continue;

            var scheme = SchemeFor(provider);
            var callbackPath = "/auth/callback/" + provider.ToLowerInvariant();

            switch (provider.ToLowerInvariant())
            {
                case UserIdentity.ProviderGitHub:
                    builder.AddGitHub(scheme, options =>
                    {
                        options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                        options.ClientId = p.ClientId;
                        options.ClientSecret = p.ClientSecret;
                        options.CallbackPath = callbackPath;
                        options.Scope.Add("user:email");
                        options.Events.OnCreatingTicket = ctx =>
                        {
                            NormalizeIdentity(ctx.Principal!, UserIdentity.ProviderGitHub);
                            return Task.CompletedTask;
                        };
                    });
                    break;

                default:
                    builder.AddOpenIdConnect(scheme, options =>
                    {
                        options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                        options.Authority = string.IsNullOrWhiteSpace(p.Authority) ? DefaultAuthority(provider) : p.Authority;
                        options.ClientId = p.ClientId;
                        options.ClientSecret = p.ClientSecret;
                        options.CallbackPath = callbackPath;
                        options.ResponseType = OpenIdConnectResponseType.Code;
                        options.UsePkce = true;
                        options.SaveTokens = false;
                        options.GetClaimsFromUserInfoEndpoint = false;
                        options.MapInboundClaims = false;
                        options.Scope.Clear();
                        options.Scope.Add("openid");
                        options.Scope.Add("email");
                        options.Scope.Add("profile");
                        options.TokenValidationParameters.NameClaimType = "name";

                        if (string.Equals(provider, UserIdentity.ProviderMicrosoft, StringComparison.OrdinalIgnoreCase)
                            && options.Authority.Contains("/common/", StringComparison.OrdinalIgnoreCase))
                        {
                            // The common endpoint's metadata advertises the
                            // issuer as a template; real tokens carry the
                            // user's tenant. Accept any Microsoft tenant.
                            options.TokenValidationParameters.IssuerValidator = (issuer, _, _) =>
                                issuer.StartsWith("https://login.microsoftonline.com/", StringComparison.OrdinalIgnoreCase)
                                && issuer.EndsWith("/v2.0", StringComparison.OrdinalIgnoreCase)
                                    ? issuer
                                    : throw new Microsoft.IdentityModel.Tokens.SecurityTokenInvalidIssuerException($"Issuer '{issuer}' is not a Microsoft identity platform tenant");
                        }

                        options.Events.OnTokenValidated = ctx =>
                        {
                            NormalizeIdentity(ctx.Principal!, provider.ToLowerInvariant());
                            return Task.CompletedTask;
                        };
                    });
                    break;
            }
        }

        return services;
    }

    /// <summary>
    /// Rewrites whatever the provider produced into the three claims the rest
    /// of the app reads: NameIdentifier = provider:subject, Name = display
    /// name, Role = authenticated, plus the provider key.
    /// </summary>
    public static void NormalizeIdentity(ClaimsPrincipal principal, string provider)
    {
        var identity = (ClaimsIdentity)principal.Identity!;
        var subject = UserIdentity.ResolveSubject(provider, principal)
            ?? throw new InvalidOperationException($"{provider} returned no stable subject claim");
        var name = UserIdentity.ResolveDisplayName(principal);
        var microsoftEmail = provider == UserIdentity.ProviderMicrosoft ? UserIdentity.ResolveMicrosoftEmail(principal) : null;

        foreach (var claim in identity.FindAll(ClaimTypes.NameIdentifier).ToList()) identity.RemoveClaim(claim);
        foreach (var claim in identity.FindAll(ClaimTypes.Name).ToList()) identity.RemoveClaim(claim);
        foreach (var claim in identity.FindAll(ClaimTypes.Role).ToList()) identity.RemoveClaim(claim);

        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, UserIdentity.BuildUserId(provider, subject)));
        identity.AddClaim(new Claim(ClaimTypes.Name, name));
        identity.AddClaim(new Claim(ClaimTypes.Role, "authenticated"));
        identity.AddClaim(new Claim(UserIdentity.IdentityProviderClaim, provider));
        if (microsoftEmail != null)
            identity.AddClaim(new Claim(UserIdentity.EmailLinkClaim, UserIdentity.EmailLinkId(microsoftEmail)));

        // Keep the cookie small: drop everything else the provider sent.
        foreach (var claim in identity.Claims.ToList())
        {
            if (claim.Type is ClaimTypes.NameIdentifier or ClaimTypes.Name or ClaimTypes.Role
                or UserIdentity.IdentityProviderClaim or UserIdentity.EmailLinkClaim)
                continue;
            identity.RemoveClaim(claim);
        }
    }

    private static string DefaultAuthority(string provider) => provider.ToLowerInvariant() switch
    {
        UserIdentity.ProviderGoogle => "https://accounts.google.com",
        UserIdentity.ProviderMicrosoft => "https://login.microsoftonline.com/common/v2.0",
        _ => throw new InvalidOperationException($"Auth:Providers:{provider}:Authority is required"),
    };
}
