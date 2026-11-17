using System.Security.Claims;
using AspNet.Security.OAuth.GitHub;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Azure.Extensions.AspNetCore.DataProtection.Blobs;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using RssApp.Config;

namespace Server.Authentication;

/// <summary>
/// Registers the session cookie, one remote scheme per configured provider,
/// and (local test mode only) the fake-user scheme. Which one handles a
/// request is decided per request by a policy scheme.
/// </summary>
public static class AuthenticationSetup
{
    public const string PolicyScheme = "RssAuth";

    public static string SchemeFor(string provider) => "ext-" + provider.ToLowerInvariant();

    public static IServiceCollection AddRssAuthentication(this IServiceCollection services, RssAppConfig config)
    {
        var auth = config.Auth;

        var keyBlob = DataProtectionKeyStore.CreateBlobClient(config);
        if (keyBlob != null)
        {
            services.AddDataProtection()
                .SetApplicationName("rss-reader")
                .PersistKeysToAzureBlobStorage(keyBlob);
        }
        else if (!string.IsNullOrWhiteSpace(auth.DataProtectionKeysPath))
        {
            Directory.CreateDirectory(auth.DataProtectionKeysPath);
            services.AddDataProtection()
                .SetApplicationName("rss-reader")
                .PersistKeysToFileSystem(new DirectoryInfo(auth.DataProtectionKeysPath));
        }

        // Secure by default: controllers require a signed-in caller (MapControllers
        // in Program.cs), and EveryEndpoint_DeclaresItsAuthorization fails the build
        // for any endpoint that declares neither [Authorize] nor AllowAnonymous.
        // Not a FallbackPolicy: that also covers requests matching no endpoint, so
        // an anonymous GET to a POST-only route answered 401 instead of 405 (and an
        // unknown path 401 instead of 404), which the deploy smoke check relies on.
        services.AddSingleton<IAuthorizationHandler, AdminAuthorizationHandler>();
        var builder = services
            .AddAuthorization(options =>
            {
                options.AddPolicy(AdminPolicy.Name, policy => policy
                    .RequireAuthenticatedUser()
                    .AddRequirements(new AdminRequirement()));
            })
            .AddAuthentication(PolicyScheme);

        // Per-request selector: test-user mode short-circuits to the fake
        // user, otherwise the request is a browser call carrying (or lacking)
        // the API cookie.
        builder.AddPolicyScheme(PolicyScheme, PolicyScheme, options =>
        {
            options.ForwardDefaultSelector = ctx =>
            {
                if (config.IsTestUserEnabled)
                    return TestUserAuthenticationHandler.AuthenticationScheme;
                return CookieAuthenticationDefaults.AuthenticationScheme;
            };
        });

        if (config.IsTestUserEnabled)
        {
            builder.AddScheme<AuthenticationSchemeOptions, TestUserAuthenticationHandler>(
                TestUserAuthenticationHandler.AuthenticationScheme, _ => { });
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
            options.Events.OnValidatePrincipal = ctx =>
            {
                if (ctx.Principal != null && StripLegacyClaims(ctx.Principal))
                {
                    ctx.ReplacePrincipal(ctx.Principal);
                    ctx.ShouldRenew = true;
                }
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
                            EnsureAccount(ctx.HttpContext, ctx.Principal!);
                            return Task.CompletedTask;
                        };
                        // Cancelled consent (?error=access_denied) or a replayed
                        // callback: back to the frontend, not a 500 on the API host.
                        options.Events.OnRemoteFailure = ctx => RedirectFailureToFrontendAsync(ctx, auth);
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
                        // Only openid + email: the consent screen then asks
                        // for the address alone. The stable subject (oid /
                        // sub) rides along with openid, and the display name
                        // is the email, so "profile" (name, picture, UPN) buys
                        // nothing. An account with no email cannot sign in; see
                        // NormalizeIdentity.
                        options.Scope.Add("openid");
                        options.Scope.Add("email");
                        options.TokenValidationParameters.NameClaimType = "name";

                        // Personal Microsoft accounts only. The consumers
                        // endpoint's metadata advertises the issuer as a
                        // tenant template, so the handler's default issuer
                        // check cannot be used; match the one tenant exactly
                        // instead, and check the token's tenant claim too.
                        var personalOnly = string.Equals(provider, UserIdentity.ProviderMicrosoft, StringComparison.OrdinalIgnoreCase)
                            && MicrosoftAccounts.IsConsumersAuthority(options.Authority);
                        if (personalOnly)
                        {
                            options.TokenValidationParameters.ValidIssuer = MicrosoftAccounts.ConsumersIssuer;
                            options.TokenValidationParameters.IssuerValidator = MicrosoftAccounts.ValidateIssuer;
                        }

                        options.Events.OnTokenValidated = ctx =>
                        {
                            if (personalOnly && !MicrosoftAccounts.IsPersonalAccount(ctx.Principal!))
                                throw new MicrosoftAccounts.WorkAccountException();
                            NormalizeIdentity(ctx.Principal!, provider.ToLowerInvariant());
                            EnsureAccount(ctx.HttpContext, ctx.Principal!);
                            return Task.CompletedTask;
                        };

                        // A failed callback (provider error, cancelled consent,
                        // or an account the app cannot identify) goes back to
                        // the frontend with the reason instead of a 500 page.
                        options.Events.OnRemoteFailure = ctx => RedirectFailureToFrontendAsync(ctx, auth);

                        // Always show the account picker: people with several
                        // Microsoft or Google accounts signed in must get to
                        // choose, instead of being logged in as whichever one
                        // the provider's session happens to hold.
                        options.Events.OnRedirectToIdentityProvider = ctx =>
                        {
                            ctx.ProtocolMessage.Prompt = "select_account";
                            return Task.CompletedTask;
                        };
                    });
                    break;
            }
        }

        return services;
    }

    /// <summary>
    /// Sends a failed callback (provider error, cancelled consent, or an
    /// account the app cannot identify) back to the frontend with the reason
    /// instead of a 500 page.
    /// </summary>
    private static Task RedirectFailureToFrontendAsync(RemoteFailureContext ctx, AuthConfig auth)
    {
        // Without the state cookie (a replayed or expired callback) there
        // are no properties; "/" would land on the API host, so fall back
        // to the frontend.
        var target = ctx.Properties?.RedirectUri
            ?? ReturnUrlValidator.Resolve(null, auth.AllowedFrontendOrigins);
        var reason = ctx.Failure switch
        {
            NoEmailException => "no-email",
            MicrosoftAccounts.WorkAccountException => "work-account",
            Microsoft.IdentityModel.Tokens.SecurityTokenInvalidIssuerException => "work-account",
            _ => "provider",
        };
        ctx.Response.Redirect(QueryHelpers.AddQueryString(target, "authError", reason));
        ctx.HandleResponse();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Rewrites whatever the provider produced into the claims the rest of the
    /// app reads: NameIdentifier = provider:subject, Role = authenticated, and
    /// the provider key. The email and name are dropped, so the cookie never
    /// carries them.
    /// </summary>
    public static void NormalizeIdentity(ClaimsPrincipal principal, string provider)
    {
        var identity = (ClaimsIdentity)principal.Identity!;
        var subject = UserIdentity.ResolveSubject(provider, principal)
            ?? throw new InvalidOperationException($"{provider} returned no stable subject claim");
        if (!UserIdentity.HasEmailOrLogin(principal))
            throw new NoEmailException(provider);
        var microsoftEmail = provider == UserIdentity.ProviderMicrosoft ? UserIdentity.ResolveMicrosoftEmail(principal) : null;

        foreach (var claim in identity.FindAll(ClaimTypes.NameIdentifier).ToList()) identity.RemoveClaim(claim);
        foreach (var claim in identity.FindAll(ClaimTypes.Role).ToList()) identity.RemoveClaim(claim);

        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, UserIdentity.BuildUserId(provider, subject)));
        identity.AddClaim(new Claim(ClaimTypes.Role, "authenticated"));
        identity.AddClaim(new Claim(UserIdentity.IdentityProviderClaim, provider));
        if (microsoftEmail != null)
            identity.AddClaim(new Claim(UserIdentity.EmailLinkClaim, UserIdentity.EmailLinkId(microsoftEmail)));

        // Keep the cookie small and free of personal data: drop everything
        // else the provider sent, including the email and name.
        foreach (var claim in identity.Claims.ToList())
        {
            if (claim.Type is ClaimTypes.NameIdentifier or ClaimTypes.Role
                or UserIdentity.IdentityProviderClaim or UserIdentity.EmailLinkClaim)
                continue;
            identity.RemoveClaim(claim);
        }
    }

    /// <summary>
    /// Sign-in is also sign-up: once the provider has identified the person,
    /// find their account or create it, so every signed-in session has one.
    /// A failure here fails the sign-in (OnRemoteFailure sends the browser
    /// back to the frontend with the reason).
    /// </summary>
    public static void EnsureAccount(HttpContext httpContext, ClaimsPrincipal principal)
    {
        httpContext.RequestServices.GetRequiredService<RssApp.ComponentServices.IUserResolver>()
            .ResolveOrCreateUser(principal);
    }

    /// <summary>
    /// Cookies issued before the email was dropped still carry it in the Name
    /// claim. Remove it and reissue the cookie on the next request, so active
    /// sessions are cleaned without signing anyone out.
    /// </summary>
    public static bool StripLegacyClaims(ClaimsPrincipal principal)
    {
        var stripped = false;
        foreach (var identity in principal.Identities)
        {
            foreach (var claim in identity.FindAll(ClaimTypes.Name).ToList())
            {
                identity.RemoveClaim(claim);
                stripped = true;
            }
        }
        return stripped;
    }

    /// <summary>
    /// The provider returned no email (or GitHub login). With only the email
    /// scope requested this means the account has no email address (some
    /// Entra work accounts); such accounts cannot sign in.
    /// </summary>
    public sealed class NoEmailException(string provider)
        : InvalidOperationException($"{provider} returned no email address for this account; the app cannot sign it in")
    {
    }

    private static string DefaultAuthority(string provider) => provider.ToLowerInvariant() switch
    {
        UserIdentity.ProviderGoogle => "https://accounts.google.com",
        UserIdentity.ProviderMicrosoft => MicrosoftAccounts.ConsumersAuthority,
        _ => throw new InvalidOperationException($"Auth:Providers:{provider}:Authority is required"),
    };
}
