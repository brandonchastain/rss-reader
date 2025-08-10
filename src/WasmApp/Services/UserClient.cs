using System;
using System.Net.Http.Json;
using RssApp.Contracts;
using RssApp.Config;

namespace WasmApp.Services;

public class UserClient : IUserClient
{
    private readonly HttpClient _httpClient;
    private readonly RssWasmConfig _config;

    public UserClient(RssWasmConfig config, ILogger<UserClient> logger)
    {
        _httpClient = new HttpClient();
        _config = config;
        logger.LogInformation(_config.ApiBaseUrl);
    }

    public async Task<string> GetUsernameAsync()
    {
        var url = $"{_config.AuthApiBaseUrl}.auth/me";
        var user = await _httpClient.GetFromJsonAsync<AadUser>(url);
        return user?.ClientPrincipal?.UserDetails ?? "Guest";
    }

    public async Task<(RssUser, bool)> GetFeedUserAsync()
    {
        string username = await this.GetUsernameAsync();
        (RssUser user, bool isNew) = await this.RegisterUserAsync(username);
        return (user, isNew);
    }

    public async Task<(RssUser, bool)> RegisterUserAsync(string username)
    {
        var response = await _httpClient.PostAsJsonAsync($"{_config.ApiBaseUrl}api/user/register", username);
        var user = await response.Content.ReadFromJsonAsync<RssUser>();

        if (response.StatusCode == System.Net.HttpStatusCode.Created)
        {
            return (user, true);
        }
        return (user, false);
    }

    private class AadUser
    {
        public ClientPrincipal ClientPrincipal { get; set; }
    }

    // "identityProvider": "aad",
    // "userId": "c2a1d6db9a3b46df81797d589ff232a5",
    // "userDetails": "brandonchastain@protonmail.com",
    // "userRoles": [
    // "anonymous",
    // "authenticated"
    // ]
    private class ClientPrincipal
    {
        public string IdentityProvider { get; set; }
        public string UserId { get; set; }
        public string UserDetails { get; set; }
        public List<string> UserRoles { get; set; }
    }
}