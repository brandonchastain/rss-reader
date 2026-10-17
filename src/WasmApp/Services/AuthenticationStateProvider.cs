using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using RssApp.Config;

namespace WasmApp.Services;

public class MyAuthenticationStateProvider : AuthenticationStateProvider
{
    private readonly RssWasmConfig _config;
    private readonly IAuthClient _authClient;

    public MyAuthenticationStateProvider(RssWasmConfig config, IAuthClient authClient)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _authClient = authClient ?? throw new ArgumentNullException(nameof(authClient));
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        if (_config.EnableTestAuth)
        {
            var identity = new ClaimsIdentity("test");
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, "abcdef"));
            identity.AddClaim(new Claim(ClaimTypes.Name, _config.TestAuthUsername));
            identity.AddClaim(new Claim(ClaimTypes.Role, "authenticated"));
            return new AuthenticationState(new ClaimsPrincipal(identity));
        }

        try
        {
            var principal = await _authClient.GetMeAsync();
            if (principal == null || string.IsNullOrEmpty(principal.UserId))
            {
                return new AuthenticationState(new ClaimsPrincipal());
            }

            var roles = (principal.UserRoles ?? Array.Empty<string>())
                .Except(new[] { "anonymous" }, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (roles.Count == 0)
            {
                return new AuthenticationState(new ClaimsPrincipal());
            }

            // AuthenticationType carries the provider so a session-expiry
            // re-login can go straight back to the same one.
            var identity = new ClaimsIdentity(principal.IdentityProvider);
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, principal.UserId));
            identity.AddClaim(new Claim(ClaimTypes.Name, principal.UserDetails ?? string.Empty));
            identity.AddClaims(roles.Select(r => new Claim(ClaimTypes.Role, r)));

            return new AuthenticationState(new ClaimsPrincipal(identity));
        }
        catch
        {
            return new AuthenticationState(new ClaimsPrincipal());
        }
    }
}
