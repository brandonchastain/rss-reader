using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace WasmApp.Services;

/// <summary>
/// Sends the API's session cookie with every API call and handles a 401.
///
/// The API is a different origin from the frontend, so each fetch opts in to
/// credentials; the cookie itself is SameSite=None; Secure on the API host.
/// No identity header is ever sent from the browser: the cookie is the proof.
///
/// SESSION EXPIRY: on a 401 we re-check /auth/me to tell an expired session
/// from an intentional logout:
/// - still authenticated per the last state: send the browser back through
///   the same provider's login and return to the current page
/// - not authenticated: navigate home with returnUrl so they can log in again
/// </summary>
public class AuthenticationHeaderHandler : DelegatingHandler
{
    private readonly AuthenticationStateProvider _authStateProvider;
    private readonly NavigationManager _navigationManager;
    private readonly IAuthClient _authClient;

    private static int _isRedirecting;

    public AuthenticationHeaderHandler(
        AuthenticationStateProvider authStateProvider,
        NavigationManager navigationManager,
        IAuthClient authClient)
    {
        _authStateProvider = authStateProvider;
        _navigationManager = navigationManager;
        _authClient = authClient;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);

        var response = await base.SendAsync(request, cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized
            && Interlocked.CompareExchange(ref _isRedirecting, 1, 0) == 0)
        {
            var authState = await _authStateProvider.GetAuthenticationStateAsync();
            var currentUrl = _navigationManager.Uri;

            var provider = authState.User?.Identity?.AuthenticationType;
            if (authState.User?.Identity?.IsAuthenticated == true && !string.IsNullOrEmpty(provider))
            {
                // Session expired while the user was authenticated: re-login
                // through the same provider and come back here.
                _navigationManager.NavigateTo(_authClient.LoginUrl(provider, currentUrl), forceLoad: true);
            }
            else
            {
                // Logged out intentionally: home, with returnUrl so they can log back in.
                _navigationManager.NavigateTo(
                    $"/?returnUrl={Uri.EscapeDataString(currentUrl)}",
                    forceLoad: true);
            }
        }

        return response;
    }
}
