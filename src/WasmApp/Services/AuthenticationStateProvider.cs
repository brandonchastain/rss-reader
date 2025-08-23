using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using RssApp.Config;

namespace WasmApp.Services;

public class MyAuthenticationStateProvider : AuthenticationStateProvider
{
    private readonly RssWasmConfig _config;
    private readonly HttpClient _client;
    private readonly ILogger<MyAuthenticationStateProvider> _logger;

    public MyAuthenticationStateProvider(RssWasmConfig config, ILogger<MyAuthenticationStateProvider> logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _client = new HttpClient { BaseAddress = new Uri(config.AuthApiBaseUrl) };
        _logger = logger;
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        if (_config.EnableTestAuth)
        {
            var identity = new ClaimsIdentity("aad");
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, "abcdef"));
            identity.AddClaim(new Claim(ClaimTypes.Name, _config.TestAuthUsername));
            identity.AddClaim(new Claim(ClaimTypes.Role, "authenticated"));
            return new AuthenticationState(new ClaimsPrincipal(identity));
        }

        try
        {
            var state = await _client.GetFromJsonAsync<UserAuthenticationState>("/.auth/me");

            var principal = state.ClientPrincipal;
            principal.UserRoles = principal.UserRoles.Except(new string[] { "anonymous" }, StringComparer.CurrentCultureIgnoreCase);

            if (!principal.UserRoles.Any())
            {
                return new AuthenticationState(new ClaimsPrincipal());
            }

            var identity = new ClaimsIdentity(principal.IdentityProvider);
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, principal.UserId));
            identity.AddClaim(new Claim(ClaimTypes.Name, principal.UserDetails));
            identity.AddClaims(principal.UserRoles.Select(r => new Claim(ClaimTypes.Role, r)));

            return new AuthenticationState(new ClaimsPrincipal(identity));
        }
        catch
        {
            return new AuthenticationState(new ClaimsPrincipal());
        }
    }
}
