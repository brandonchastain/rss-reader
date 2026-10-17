using System;
using System.Net.Http.Json;
using RssApp.Contracts;
using RssApp.Config;

namespace WasmApp.Services;

public class UserClient : IUserClient
{
    private static readonly string[][] DefaultFeeds = [
        ["https://api.quantamagazine.org/feed/", "sci"],
        ["https://css-tricks.com/feed", "tech"],
        ["https://www.theguardian.com/us-news/rss", "news"]
    ];

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

    public async Task<string> GetUsernameAsync()
    {
        if (_config.EnableTestAuth)
        {
            return string.IsNullOrWhiteSpace(_config.TestAuthUsername) ? "testuser" : _config.TestAuthUsername;
        }

        var principal = await _authClient.GetMeAsync();
        return principal?.UserDetails ?? "Guest";
    }

    public async Task<RssUser> GetFeedUserAsync()
    {
        string username = await this.GetUsernameAsync();
        (RssUser user, bool isNew) = await this.RegisterUserAsync(username);

        if (isNew)
        {
            foreach (string[] parts in DefaultFeeds)
            {
                var href = parts[0];
                var tag = parts[1];
                var newFeed = new NewsFeed(href, user.Id);
                newFeed.Tags = new List<string> { tag };
                // TODO: fix adding default feeds
                //await this.AddFeedAsync(newFeed);
            }
        }
        return user;
    }

    public async Task<(RssUser, bool)> RegisterUserAsync(string username)
    {
        var response = await _apiClient.PostAsync($"api/user/register", null);
        var user = await response.Content.ReadFromJsonAsync<RssUser>();

        if (response.StatusCode == System.Net.HttpStatusCode.Created)
        {
            return (user, true);
        }
        return (user, false);
    }

    public async Task<UserDataReport> GetDataReportAsync()
    {
        return await _apiClient.GetFromJsonAsync<UserDataReport>("api/user/report");
    }

    public async Task DeleteAccountAsync()
    {
        await _apiClient.DeleteAsync("api/user");
    }

    public void Dispose()
    {
        _apiClient.Dispose();
    }
}