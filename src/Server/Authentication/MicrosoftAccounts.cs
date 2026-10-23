#nullable enable
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;

namespace Server.Authentication;

/// <summary>
/// Microsoft sign-in is restricted to personal Microsoft accounts. Personal
/// accounts all live in one fixed tenant, so the issuer and the token's
/// <c>tid</c> claim can be matched exactly instead of accepting any Entra
/// tenant (which is what the <c>common</c> endpoint forces). Work and school
/// accounts are refused twice: by the <c>consumers</c> authority at Microsoft's
/// end, and here on the token in case the authority is ever misconfigured.
/// </summary>
public static class MicrosoftAccounts
{
    /// <summary>The tenant every personal Microsoft account is issued from.</summary>
    public const string ConsumersTenantId = "9188040d-6c67-4c5b-b112-36a304b66dad";

    public const string ConsumersAuthority = "https://login.microsoftonline.com/consumers/v2.0";

    /// <summary>Issuer carried by v2.0 id tokens for personal accounts.</summary>
    public const string ConsumersIssuer = "https://login.microsoftonline.com/" + ConsumersTenantId + "/v2.0";

    /// <summary>True when the configured authority is the personal-accounts endpoint.</summary>
    public static bool IsConsumersAuthority(string? authority) =>
        !string.IsNullOrWhiteSpace(authority)
        && (authority.TrimEnd('/').Equals(ConsumersAuthority, StringComparison.OrdinalIgnoreCase)
            || authority.TrimEnd('/').Equals(ConsumersIssuer, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Strict issuer check for the personal-accounts authority: only the
    /// consumers tenant's issuer is accepted, anything else is rejected.
    /// </summary>
    public static string ValidateIssuer(string issuer, SecurityToken? token, TokenValidationParameters? parameters)
    {
        if (string.Equals(issuer?.TrimEnd('/'), ConsumersIssuer, StringComparison.OrdinalIgnoreCase))
            return issuer!;
        throw new SecurityTokenInvalidIssuerException(
            $"Issuer '{issuer}' is not the personal Microsoft accounts tenant; work and school accounts cannot sign in")
        {
            InvalidIssuer = issuer,
        };
    }

    /// <summary>True when the token's tenant claim is the consumers tenant.</summary>
    public static bool IsPersonalAccount(ClaimsPrincipal principal)
    {
        var tid = principal.FindFirst("tid")?.Value
            ?? principal.FindFirst("http://schemas.microsoft.com/identity/claims/tenantid")?.Value;
        return string.Equals(tid, ConsumersTenantId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Thrown from token validation when a work or school account presents itself.</summary>
    public sealed class WorkAccountException()
        : InvalidOperationException("Only personal Microsoft accounts can sign in; work and school accounts are not accepted")
    {
    }
}
