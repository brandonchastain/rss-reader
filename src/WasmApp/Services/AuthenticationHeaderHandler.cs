using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace WasmApp.Services;

/// <summary>
/// HTTP handler that ensures credentials (cookies) are sent with API requests
/// and redirects appropriately when the SWA Easy Auth session expires.
/// 
/// SECURITY NOTE: We do NOT manually send identity headers. With Easy Auth:
/// 1. User authenticates via /.auth/login/... 
/// 2. SWA sets an auth cookie
/// 3. Browser sends cookie with API requests
/// 4. SWA platform validates cookie and injects x-ms-client-principal server-side
/// 
/// The browser cannot and should not forge identity headers - that's the platform's job.
/// 
/// SESSION EXPIRY: SWA sessions last ~8 hours. When a 401 is detected, we re-check
/// /.auth/me to distinguish intentional logout from session expiry:
/// - If still authenticated: silently re-authenticate via /.auth/login/aad
/// - If no longer authenticated: navigate home (user logged out intentionally)
/// </summary>
public class AuthenticationHeaderHandler : DelegatingHandler
{
    private readonly AuthenticationStateProvider _authStateProvider;
    private readonly NavigationManager _navigationManager;

    private static int _isRedirecting;

    public AuthenticationHeaderHandler(
        AuthenticationStateProvider authStateProvider,
        NavigationManager navigationManager)
    {
        _authStateProvider = authStateProvider;
        _navigationManager = navigationManager;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized
            && Interlocked.CompareExchange(ref _isRedirecting, 1, 0) == 0)
        {
            var authState = await _authStateProvider.GetAuthenticationStateAsync();
            var returnUrl = Uri.EscapeDataString(_navigationManager.Uri);

            if (authState.User?.Identity?.IsAuthenticated == true)
            {
                // Session expired while user was authenticated - do silent re-auth and return to current page
                _navigationManager.NavigateTo(
                    $"/.auth/login/aad?post_login_redirect_uri={returnUrl}",
                    forceLoad: true);
            }
            else
            {
                // User logged out intentionally - navigate home with returnUrl so they can log back in
                _navigationManager.NavigateTo(
                    $"/?returnUrl={returnUrl}",
                    forceLoad: true);
            }
        }

        return response;
    }
}
