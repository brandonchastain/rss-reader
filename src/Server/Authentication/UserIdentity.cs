#nullable enable
using System.Security.Claims;

namespace Server.Authentication;

/// <summary>
/// How a logged-in user is identified across providers and across the
/// Static Web Apps -> API-login migration.
///
/// Today's rows carry a Static Web Apps user id (an opaque per-app hash) in
/// Users.AadUserId. The API login issues ids of the form provider:subject,
/// where subject is the provider's stable identifier (GitHub numeric id,
/// Google sub, Entra oid). A direct Microsoft login cannot reproduce the
/// SWA hash, so while the proxy still forwards SWA principals the handler
/// extracts the Entra oid from the principal's claims and the resolver stores
/// microsoft:{oid} in Users.ProviderUserId next to the SWA id. After cutover
/// the same person logs in directly and is found by that linked id.
/// </summary>
public static class UserIdentity
{
    public const string ProviderGoogle = "google";
    public const string ProviderMicrosoft = "microsoft";
    public const string ProviderGitHub = "github";

    /// <summary>Claim carrying the provider-stable id learned from an SWA principal.</summary>
    public const string LinkedUserIdClaim = "urn:rss:linked-user-id";

    /// <summary>Claim carrying the provider key (google/microsoft/github/aad).</summary>
    public const string IdentityProviderClaim = "urn:rss:identity-provider";

    /// <summary>
    /// Claim carrying the email-hash link id of a Microsoft login
    /// (<see cref="EmailLinkId"/>), used once to find a row that was
    /// linked during the Static Web Apps migration window.
    /// </summary>
    public const string EmailLinkClaim = "urn:rss:email-link-id";

    public const string EmailLinkPrefix = "microsoft-email:";

    /// <summary>
    /// UserIdentities.Provider for the Microsoft email-hash link recorded during
    /// the Static Web Apps migration window.
    /// </summary>
    public const string ProviderEmailLink = "email-link";

    public static readonly string[] KnownProviders = { ProviderGoogle, ProviderMicrosoft, ProviderGitHub };

    /// <summary>
    /// Splits a stored or claimed user id into the (provider, subject) pair that
    /// keys UserIdentities. <c>provider:subject</c> ids from the API login split
    /// on the first colon; <c>microsoft-email:hash</c> link ids become
    /// (<see cref="ProviderEmailLink"/>, hash); anything else is a legacy Static
    /// Web Apps id (an opaque Entra-issued hash) and is filed under microsoft,
    /// which is the provider SWA authenticated it with.
    /// </summary>
    public static (string Provider, string Subject) ParseIdentity(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("id is required", nameof(id));
        id = id.Trim();
        if (id.StartsWith(EmailLinkPrefix, StringComparison.Ordinal))
            return (ProviderEmailLink, id.Substring(EmailLinkPrefix.Length));
        var colon = id.IndexOf(':');
        if (colon > 0 && colon < id.Length - 1)
        {
            var provider = id.Substring(0, colon).ToLowerInvariant();
            if (IsKnownProvider(provider))
                return (provider, id.Substring(colon + 1));
        }
        return (ProviderMicrosoft, id);
    }

    public static string FormatIdentity(string provider, string subject) =>
        provider == ProviderEmailLink ? EmailLinkPrefix + subject : $"{provider}:{subject}";

    public static string BuildUserId(string provider, string subject)
    {
        if (string.IsNullOrWhiteSpace(provider)) throw new ArgumentException("provider is required", nameof(provider));
        if (string.IsNullOrWhiteSpace(subject)) throw new ArgumentException("subject is required", nameof(subject));
        return $"{provider.Trim().ToLowerInvariant()}:{subject.Trim()}";
    }

    /// <summary>
    /// Static Web Apps forwards no claims, only the opaque per-app user id and
    /// the account's email as userDetails. The email is reported by Microsoft,
    /// not typed by the user, and the same account's direct login carries the
    /// same address in its id token, so a hash of it bridges the two. Only the
    /// hash is stored, and it is replaced by the real provider id on the first
    /// direct login.
    /// </summary>
    public static string EmailLinkId(string email)
    {
        var normalized = email.Trim().ToLowerInvariant();
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized));
        return EmailLinkPrefix + Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static bool LooksLikeEmail(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.IndexOf('@') > 0 && value.IndexOf('@') < value.Length - 1;

    /// <summary>The verified email a Microsoft id token carries, if any.</summary>
    public static string? ResolveMicrosoftEmail(ClaimsPrincipal principal)
    {
        var email = principal.FindFirst("email")?.Value ?? principal.FindFirst(ClaimTypes.Email)?.Value;
        if (LooksLikeEmail(email)) return email;
        var upn = principal.FindFirst("preferred_username")?.Value;
        return LooksLikeEmail(upn) ? upn : null;
    }

    public static bool IsKnownProvider(string? provider) =>
        provider != null && KnownProviders.Contains(provider, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Picks the provider-stable subject out of the claims a provider's
    /// handler produced. Microsoft's <c>sub</c> is pairwise per app
    /// registration, so the tenant-wide <c>oid</c> is used instead; it is the
    /// same value SWA's preconfigured Entra app sees, which is what makes the
    /// migration link possible.
    /// </summary>
    public static string? ResolveSubject(string provider, ClaimsPrincipal principal)
    {
        if (string.Equals(provider, ProviderMicrosoft, StringComparison.OrdinalIgnoreCase))
        {
            var oid = principal.FindFirst("oid")?.Value
                ?? principal.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value;
            if (!string.IsNullOrEmpty(oid)) return oid;
        }

        return principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? principal.FindFirst("sub")?.Value;
    }

    /// <summary>A display name that never falls back to storing nothing.</summary>
    public static string ResolveDisplayName(ClaimsPrincipal principal) =>
        principal.FindFirst(ClaimTypes.Email)?.Value
        ?? principal.FindFirst("email")?.Value
        ?? principal.FindFirst("preferred_username")?.Value
        ?? principal.FindFirst(ClaimTypes.Name)?.Value
        ?? principal.FindFirst("name")?.Value
        ?? principal.FindFirst("urn:github:login")?.Value
        ?? string.Empty;
}

/// <summary>
/// Validates the returnUrl of /auth/login and /auth/logout against the
/// configured frontend origins so the API never becomes an open redirector.
/// </summary>
public static class ReturnUrlValidator
{
    /// <summary>
    /// Returns an absolute URL on an allowed origin. A relative path is
    /// resolved against the first allowed origin. Anything else (another
    /// host, a protocol-relative URL, a non-http scheme, an empty list)
    /// falls back to the first allowed origin's root.
    /// </summary>
    public static string Resolve(string? returnUrl, IReadOnlyList<string> allowedOrigins)
    {
        if (allowedOrigins.Count == 0)
            throw new InvalidOperationException("Auth:AllowedFrontendOrigins must list at least one frontend origin");

        var fallback = NormalizeOrigin(allowedOrigins[0]) + "/";
        if (string.IsNullOrWhiteSpace(returnUrl))
            return fallback;

        returnUrl = returnUrl.Trim();

        // Relative path: must be a single leading slash (not "//evil" or "/\evil").
        if (returnUrl.StartsWith('/'))
        {
            if (returnUrl.Length > 1 && (returnUrl[1] == '/' || returnUrl[1] == '\\'))
                return fallback;
            return NormalizeOrigin(allowedOrigins[0]) + returnUrl;
        }

        if (!Uri.TryCreate(returnUrl, UriKind.Absolute, out var uri))
            return fallback;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return fallback;

        var origin = uri.GetLeftPart(UriPartial.Authority);
        foreach (var allowed in allowedOrigins)
        {
            if (string.Equals(NormalizeOrigin(allowed), origin, StringComparison.OrdinalIgnoreCase))
                return uri.ToString();
        }

        return fallback;
    }

    public static string NormalizeOrigin(string origin)
    {
        origin = origin.Trim().TrimEnd('/');
        if (Uri.TryCreate(origin, UriKind.Absolute, out var uri))
            return uri.GetLeftPart(UriPartial.Authority);
        return origin;
    }
}

/// <summary>
/// The shape /auth/me returns: the same clientPrincipal envelope Static Web
/// Apps' /.auth/me used, so the frontend only changes the URL.
/// </summary>
public sealed class ClientPrincipalResponse
{
    public ClientPrincipal? ClientPrincipal { get; init; }

    public static ClientPrincipalResponse Anonymous { get; } = new() { ClientPrincipal = null };

    public static ClientPrincipalResponse From(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
            return Anonymous;

        var userId = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
            return Anonymous;

        var roles = principal.FindAll(ClaimTypes.Role).Select(c => c.Value)
            .Where(r => !string.Equals(r, "anonymous", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!roles.Contains("authenticated", StringComparer.OrdinalIgnoreCase))
            roles.Insert(0, "authenticated");

        return new ClientPrincipalResponse
        {
            ClientPrincipal = new ClientPrincipal
            {
                IdentityProvider = principal.FindFirst(UserIdentity.IdentityProviderClaim)?.Value
                    ?? principal.Identity.AuthenticationType ?? string.Empty,
                UserId = userId,
                UserDetails = principal.FindFirst(ClaimTypes.Name)?.Value ?? string.Empty,
                UserRoles = roles,
            }
        };
    }
}

public sealed class ClientPrincipal
{
    public string IdentityProvider { get; init; } = string.Empty;
    public string UserId { get; init; } = string.Empty;
    public string UserDetails { get; init; } = string.Empty;
    public IReadOnlyList<string> UserRoles { get; init; } = Array.Empty<string>();
}
