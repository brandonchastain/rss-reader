using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Authorization;

namespace WasmApp.Services;

/// <summary>
/// HTTP handler that ensures credentials (cookies) are sent with API requests.
/// 
/// SECURITY NOTE: We do NOT manually send identity headers. With Easy Auth:
/// 1. User authenticates via /.auth/login/... 
/// 2. SWA sets an auth cookie
/// 3. Browser sends cookie with API requests
/// 4. SWA platform validates cookie and injects x-ms-client-principal server-side
/// 
/// The browser cannot and should not forge identity headers - that's the platform's job.
/// </summary>
public class AuthenticationHeaderHandler : DelegatingHandler
{
    private readonly AuthenticationStateProvider _authStateProvider;

    public AuthenticationHeaderHandler(AuthenticationStateProvider authStateProvider)
    {
        _authStateProvider = authStateProvider;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Just forward the request - Easy Auth uses cookies, not headers from the browser
        // The SWA platform will inject x-ms-client-principal after validating the auth cookie
        return await base.SendAsync(request, cancellationToken);
    }
}
