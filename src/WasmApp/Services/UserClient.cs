using System;
using System.Net.Http.Json;
using RssApp.Contracts;
using RssApp.Config;

namespace WasmApp.Services;

public class UserClient : IUserClient
{
    private readonly HttpClient _apiClient;
    private readonly RssWasmConfig _config;
    private readonly IAuthClient _authClient;

    public UserClient(RssWasmConfig config, ILogger<UserClient> logger, IHttpClientFactory httpClientFactory, IAuthClient authClient)
    {
        _apiClient = httpClientFactory.CreateClient("ApiClient"); // sends the session cookie
        _config = config;
        _authClient = authClient;
        logger.LogInformation(_config.ApiBaseUrl);
    }

    public async Task<string> GetAccountIdAsync()
    {
        var principal = await _authClient.GetMeAsync();
        return string.IsNullOrEmpty(principal?.UserId) ? null : principal.UserId;
    }

    public async Task<RssUser> GetFeedUserAsync()
    {
        return await _apiClient.GetFromJsonAsync<RssUser>("api/user");
    }

    public async Task<UserDataReport> GetDataReportAsync()
    {
        return await _apiClient.GetFromJsonAsync<UserDataReport>("api/user/report");
    }

    public async Task DeleteAccountAsync()
    {
        var response = await _apiClient.DeleteAsync("api/user");
        if (!response.IsSuccessStatusCode)
        {
            var reason = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException(
                string.IsNullOrWhiteSpace(reason) ? $"{(int)response.StatusCode} {response.ReasonPhrase}" : reason,
                null,
                response.StatusCode);
        }
    }

    public void Dispose()
    {
        _apiClient.Dispose();
    }
}