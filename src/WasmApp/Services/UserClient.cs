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
        if (_config.EnableTestAuth)
        {
            return string.IsNullOrWhiteSpace(_config.TestAuthUsername) ? "testuser" : _config.TestAuthUsername;
        }

        var url = $"{_config.AuthApiBaseUrl}.auth/me";
        var user = await _httpClient.GetFromJsonAsync<AadUser>(url);
        return user?.ClientPrincipal?.UserDetails ?? "Guest";
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
        var response = await _httpClient.PostAsJsonAsync($"{_config.ApiBaseUrl}api/user/register", username);
        var user = await response.Content.ReadFromJsonAsync<RssUser>();

        if (response.StatusCode == System.Net.HttpStatusCode.Created)
        {
            return (user, true);
        }
        return (user, false);
    }
}