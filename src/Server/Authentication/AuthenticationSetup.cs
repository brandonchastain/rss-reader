#nullable enable
using System.Security.Claims;
using AspNet.Security.OAuth.GitHub;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
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
                            return Task.CompletedTask;
                        };

                        // A failed callback (provider error, cancelled consent,
                        // or an account the app cannot identify) goes back to
                        // the frontend with the reason instead of a 500 page.
                        options.Events.OnRemoteFailure = ctx =>
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
                        };

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
        if (string.IsNullOrWhiteSpace(name))
            throw new NoEmailException(provider);
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

    /// <summary>
    /// The provider returned an identity with nothing usable as a display
    /// name. With only the email scope requested this means the account has no
    /// email address (some Entra work accounts); such accounts cannot sign in.
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
