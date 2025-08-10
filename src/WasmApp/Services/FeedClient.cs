using System;
using System.Net.Http.Json;
using RssApp.Contracts;
using RssApp.RssClient;
using Microsoft.AspNetCore.Components.Authorization;
using RssApp.Config;

namespace WasmApp.Services
{
    public class FeedClient : IFeedClient
    {
        private readonly HttpClient _httpClient;
        private readonly RssWasmConfig _config;
        private readonly UserClient userClient;
        public bool IsFilterUnread { get; set; }
        public string FilterTag { get; set; }
        public bool IsFilterSaved { get; set; }
        public string[][] DefaultFeeds { get; private set; } = [
            ["https://api.quantamagazine.org/feed/", "sci"],
            ["https://css-tricks.com/feed", "tech"],
            ["https://www.theguardian.com/us-news/rss", "news"]
        ];

        private bool _disposed;

        public FeedClient(RssWasmConfig config, ILogger<FeedClient> logger, UserClient userClient)
        {
            _httpClient = new HttpClient();
            _config = config;
            logger.LogInformation(_config.ApiBaseUrl);
            this.userClient = userClient;
        }

        public async Task<IEnumerable<NewsFeed>> GetFeedsAsync()
        {
            var user = await GetFeedUserAsync();
            return await _httpClient.GetFromJsonAsync<IEnumerable<NewsFeed>>($"{_config.ApiBaseUrl}api/feed?username={user.Username}");
        }

        public async Task AddFeedAsync(NewsFeed feed)
        {
            var user = await GetFeedUserAsync();
            feed.UserId = user.Id;

            await _httpClient.PostAsJsonAsync($"{_config.ApiBaseUrl}api/feed", feed);
        }

        public async Task AddTagAsync(NewsFeed feed, string tag)
        {
            feed.Tags ??= new List<string>();
            feed.Tags.Add(tag);
            await _httpClient.PostAsJsonAsync($"{_config.ApiBaseUrl}api/feed/tags", feed);
        }

        public async Task<IEnumerable<NewsFeedItem>> GetTimelineAsync(int page, int pageSize = 20)
        {
            var user = await GetFeedUserAsync();
            var url = $"{_config.ApiBaseUrl}api/item/timeline?username={user.Username}&isFilterUnread={IsFilterUnread}&isFilterSaved={IsFilterSaved}&filterTag={FilterTag}&page={page}&pageSize={pageSize}";
            return await _httpClient.GetFromJsonAsync<IEnumerable<NewsFeedItem>>(url);
        }

        public async Task<IEnumerable<NewsFeedItem>> GetFeedItemsAsync(NewsFeed feed, int page)
        {
            var user = await GetFeedUserAsync();
            var url = $"{_config.ApiBaseUrl}api/item/feed?username={user.Username}&href={Uri.EscapeDataString(feed.Href)}&isFilterUnread={IsFilterUnread}&isFilterSaved={IsFilterSaved}&filterTag={FilterTag}&page={page}";
            return await _httpClient.GetFromJsonAsync<IEnumerable<NewsFeedItem>>(url);
        }

        public async Task<IEnumerable<NewsFeedItem>> SearchItemsAsync(string query, int page, int pageSize = 20)
        {
            var url = $"{_config.ApiBaseUrl}api/item/search?query={Uri.EscapeDataString(query)}&isFilterUnread={IsFilterUnread}&isFilterSaved={IsFilterSaved}&filterTag={FilterTag}&page={page}&pageSize={pageSize}";
            return await _httpClient.GetFromJsonAsync<IEnumerable<NewsFeedItem>>(url);
        }

        public async Task MarkAsReadAsync(NewsFeedItem item, bool isRead)
        {
            var user = await GetFeedUserAsync();
            await _httpClient.GetFromJsonAsync<string>($"{_config.ApiBaseUrl}api/item/markAsRead?username={user.Username}&itemId={item.Id}");
        }

        public async Task<IEnumerable<string>> GetUserTagsAsync(RssUser _)
        {
            var user = await GetFeedUserAsync();
            var content = await _httpClient.GetFromJsonAsync<List<string>>($"{_config.ApiBaseUrl}api/feed/tags?userId={user.Id}");
            return content;
        }

        public async Task SavePostAsync(NewsFeedItem item)
        {
            await _httpClient.PostAsJsonAsync($"{_config.ApiBaseUrl}api/item/save", item);
        }

        public async Task UnsavePostAsync(NewsFeedItem item)
        {
            await _httpClient.PostAsJsonAsync($"{_config.ApiBaseUrl}api/item/unsave", item);
        }

        public async Task<string> GetItemContentAsync(NewsFeedItem item)
        {
            var user = await GetFeedUserAsync();
            var content = await _httpClient.GetFromJsonAsync<string>($"{_config.ApiBaseUrl}api/item/content?username={user.Username}&itemId={item.Id}");
            var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(content));
            return decoded;
        }

        public async Task DeleteFeedAsync(string feedHref)
        {
            var user = await GetFeedUserAsync();
            var url = $"{_config.ApiBaseUrl}api/feed/delete?href={Uri.EscapeDataString(feedHref)}&username={user.Username}";
            await _httpClient.PostAsync(url, null);
        }

        public async Task RefreshFeedsAsync()
        {
            var user = await this.GetFeedUserAsync();
            var url = $"{_config.ApiBaseUrl}api/feed/refresh?username={user.Username}";
            await _httpClient.GetAsync(url);
        }

        public async Task<string> GetUsernameAsync()
        {
            return await this.userClient.GetUsernameAsync();
        }

        public async Task<RssUser> GetFeedUserAsync()
        {
            (var user, bool isNew) = await this.userClient.GetFeedUserAsync();

            if (isNew)
            {
                foreach (string[] parts in DefaultFeeds)
                {
                    var href = parts[0];
                    var tag = parts[1];
                    var newFeed = new NewsFeed(href, user.Id);
                    newFeed.Tags = new List<string> { tag };
                    await this.AddFeedAsync(newFeed);
                }
            }

            return user;
        }

        public async Task ImportOpmlAsync(string opmlContent)
        {
            var user = await GetFeedUserAsync();
            var url = $"{_config.ApiBaseUrl}api/feed/importOpml/";
            var data = new OpmlImport()
            {
                UserId = user.Id,
                OpmlContent = opmlContent,
            };
            await _httpClient.PostAsJsonAsync<OpmlImport>(url, data);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
            }
        }
    }
}
