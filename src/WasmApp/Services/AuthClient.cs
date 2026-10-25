using System.Net.Http.Json;
using Microsoft.AspNetCore.Components.WebAssembly.Http;
using RssApp.Config;

namespace WasmApp.Services;

/// <summary>
/// The API owns login. Every auth URL hangs off ApiBaseUrl, and the browser
/// is sent there as a top-level navigation (login/logout) or asked with
/// credentials (me/providers) so the API's session cookie rides along.
/// </summary>
public interface IAuthClient
{
    string LoginUrl(string provider, string returnUrl);
    string LogoutUrl(string returnUrl);
    Task<IReadOnlyList<string>> GetProvidersAsync();
    Task<ClientPrincipal> GetMeAsync();
}

public class AuthClient : IAuthClient
{
    private static readonly string[] NoProviders = Array.Empty<string>();

    private readonly RssWasmConfig _config;
    private readonly HttpClient _client;

    public AuthClient(RssWasmConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _client = new HttpClient();
    }

    public string LoginUrl(string provider, string returnUrl)
    {
        return $"{_config.ApiBaseUrl}auth/login/{Uri.EscapeDataString(provider)}?returnUrl={Uri.EscapeDataString(returnUrl)}";
    }

    public string LogoutUrl(string returnUrl)
    {
        return $"{_config.ApiBaseUrl}auth/logout?returnUrl={Uri.EscapeDataString(returnUrl)}";
    }

    public async Task<IReadOnlyList<string>> GetProvidersAsync()
    {
        try
        {
            using var request = Credentialed(HttpMethod.Get, "auth/providers");
            using var response = await _client.SendAsync(request);
            if (!response.IsSuccessStatusCode) return NoProviders;
            return await response.Content.ReadFromJsonAsync<string[]>() ?? NoProviders;
        }
        catch
        {
            return NoProviders;
        }
    }

    public async Task<ClientPrincipal> GetMeAsync()
    {
        using var request = Credentialed(HttpMethod.Get, "auth/me");
        using var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var state = await response.Content.ReadFromJsonAsync<UserAuthenticationState>();
        return state?.ClientPrincipal;
    }

    private HttpRequestMessage Credentialed(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, _config.ApiBaseUrl + path);
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        request.SetBrowserRequestCache(BrowserRequestCache.NoStore);
        return request;
    }
}
