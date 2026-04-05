using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace WasmApp.Services;

/// <summary>
/// HTTP handler that ensures credentials (cookies) are sent with API requests
/// and auto-redirects to login when the SWA Easy Auth session expires.
/// 
/// SECURITY NOTE: We do NOT manually send identity headers. With Easy Auth:
/// 1. User authenticates via /.auth/login/... 
/// 2. SWA sets an auth cookie
/// 3. Browser sends cookie with API requests
/// 4. SWA platform validates cookie and injects x-ms-client-principal server-side
/// 
/// The browser cannot and should not forge identity headers - that's the platform's job.
/// 
/// SESSION EXPIRY: SWA sessions last ~8 hours, but the AAD browser session persists
/// much longer. When a 401 is detected, we redirect to /.auth/login/aad which will
/// silently re-authenticate if the AAD session is still valid (no password prompt).
/// </summary>
public class AuthenticationHeaderHandler : DelegatingHandler
{
    private readonly AuthenticationStateProvider _authStateProvider;
    private readonly NavigationManager _navigationManager;

    private static volatile bool _isRedirecting;

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

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && !_isRedirecting)
        {
            _isRedirecting = true;
            var returnUrl = Uri.EscapeDataString(_navigationManager.Uri);
            _navigationManager.NavigateTo(
                $"/.auth/login/aad?post_login_redirect_uri={returnUrl}",
                forceLoad: true);
        }

        return response;
    }
}
