using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Authorization;

namespace WasmApp.Services;

public class AuthenticationHeaderHandler : DelegatingHandler
{
    private readonly AuthenticationStateProvider _authStateProvider;

    public AuthenticationHeaderHandler(AuthenticationStateProvider authStateProvider)
    {
        _authStateProvider = authStateProvider;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var authState = await _authStateProvider.GetAuthenticationStateAsync();
        var user = authState.User;

        if (user.Identity?.IsAuthenticated == true)
        {
            // Create a client principal object similar to what SWA sends
            var clientPrincipal = new
            {
                identityProvider = user.FindFirst(c => c.Type == "http://schemas.microsoft.com/identity/claims/identityprovider")?.Value ?? "aad",
                userId = user.FindFirst(c => c.Type == "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value ?? "",
                userDetails = user.FindFirst(c => c.Type == "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name")?.Value ?? "",
                userRoles = user.FindAll(c => c.Type == "http://schemas.microsoft.com/ws/2008/06/identity/claims/role")
                    .Select(c => c.Value)
                    .ToArray()
            };

            // Serialize and base64 encode like SWA does
            var json = JsonSerializer.Serialize(clientPrincipal);
            var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

            // Add the header
            request.Headers.Add("X-MS-CLIENT-PRINCIPAL", base64);
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
