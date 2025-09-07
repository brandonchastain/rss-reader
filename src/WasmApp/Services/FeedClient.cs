using System;
using System.Net.Http;
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
        private readonly HttpClient _refreshHttpClient;
        private readonly RssWasmConfig _config;
        private readonly IUserClient userClient;
        private readonly ILogger<FeedClient> _logger;
        public bool IsFilterUnread { get; set; }
        public string FilterTag { get; set; }
        public bool IsFilterSaved { get; set; }
        private bool _disposed;

        public FeedClient(RssWasmConfig config, ILogger<FeedClient> logger, IUserClient userClient, IHttpClientFactory httpClientFactory)
        {
            _httpClient = httpClientFactory.CreateClient("api");
            _refreshHttpClient = httpClientFactory.CreateClient("refresh");
            _config = config;
            this.userClient = userClient;
            _logger = logger;
        }

        public async Task<IEnumerable<NewsFeed>> GetFeedsAsync()
        {
            var user = await userClient.GetFeedUserAsync();
            return await _httpClient.GetFromJsonAsync<IEnumerable<NewsFeed>>($"api/feed?username={user.Username}");
        }

        public async Task AddFeedAsync(NewsFeed feed)
        {
            var user = await userClient.GetFeedUserAsync();
            feed.UserId = user.Id;

            await _httpClient.PostAsJsonAsync($"api/feed", feed);
        }

        public async Task AddTagAsync(NewsFeed feed, string tag)
        {
            feed.Tags ??= new List<string>();
            feed.Tags.Add(tag);
            await _httpClient.PostAsJsonAsync($"api/feed/tags", feed);
        }

        public async Task<IEnumerable<NewsFeedItem>> GetTimelineAsync(int page, int pageSize = 20)
        {
            pageSize = Math.Min(pageSize, 100);
            var user = await userClient.GetFeedUserAsync();
            var url = $"api/item/timeline?username={user.Username}&isFilterUnread={IsFilterUnread}&isFilterSaved={IsFilterSaved}&filterTag={FilterTag}&page={page}&pageSize={pageSize}";
            return await _httpClient.GetFromJsonAsync<IEnumerable<NewsFeedItem>>(url);
        }

        public async Task<IEnumerable<NewsFeedItem>> GetFeedItemsAsync(NewsFeed feed, int page)
        {
            var user = await userClient.GetFeedUserAsync();
            var url = $"api/item/feed?username={user.Username}&href={Uri.EscapeDataString(feed.Href)}&isFilterUnread={IsFilterUnread}&isFilterSaved={IsFilterSaved}&filterTag={FilterTag}&page={page}";
            return await _httpClient.GetFromJsonAsync<IEnumerable<NewsFeedItem>>(url);
        }

        public async Task<IEnumerable<NewsFeedItem>> SearchItemsAsync(string query, int page, int pageSize = 20)
        {
            pageSize = Math.Min(pageSize, 100);
            var user = await userClient.GetFeedUserAsync();
            var url = $"api/item/search?username={user.Username}&query={Uri.EscapeDataString(query)}&isFilterUnread={IsFilterUnread}&isFilterSaved={IsFilterSaved}&filterTag={FilterTag}&page={page}&pageSize={pageSize}";
            return await _httpClient.GetFromJsonAsync<IEnumerable<NewsFeedItem>>(url);
        }   

        public async Task MarkAsReadAsync(NewsFeedItem item, bool isRead)
        {
            var user = await userClient.GetFeedUserAsync();
            await _httpClient.GetAsync($"api/item/markAsRead?username={user.Username}&itemId={item.Id}&isRead={isRead}");
        }

        public async Task<IEnumerable<string>> GetUserTagsAsync(RssUser _)
        {
            var user = await userClient.GetFeedUserAsync();
            return await _httpClient.GetFromJsonAsync<List<string>>($"api/feed/tags?userId={user.Id}");
        }

        public async Task SavePostAsync(NewsFeedItem item)
        {
            await _httpClient.PostAsJsonAsync($"api/item/save", item);
        }

        public async Task UnsavePostAsync(NewsFeedItem item)
        {
            await _httpClient.PostAsJsonAsync($"api/item/unsave", item);
        }

        public async Task<string> GetItemContentAsync(NewsFeedItem item)
        {
            var user = await userClient.GetFeedUserAsync();
            try
            {
                var content = await _httpClient.GetFromJsonAsync<string>($"api/item/content?username={user.Username}&itemId={item.Id}");
                var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(content));
                return decoded;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // a 404 can happen. Ignore this. It probably means content was totally empty.
            }

            return "[no content found]";
        }

        public async Task DeleteFeedAsync(string feedHref)
        {
            var user = await userClient.GetFeedUserAsync();
            var url = $"api/feed/delete?href={Uri.EscapeDataString(feedHref)}&username={user.Username}";
            await _httpClient.PostAsync(url, null);
        }

        public async Task RefreshFeedsAsync()
        {
            var user = await this.userClient.GetFeedUserAsync();
            var url = $"api/feed/refresh?username={user.Username}";

            try
            {
                await _refreshHttpClient.GetAsync(url);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Feed refresh for user {user.Username} timed out ({_refreshHttpClient.Timeout}). Ignoring error.");
            }
        }

        public async Task ImportOpmlAsync(string opmlContent)
        {
            var user = await userClient.GetFeedUserAsync();
            var url = $"api/feed/importOpml/";
            var data = new OpmlImport()
            {
                UserId = user.Id,
                OpmlContent = opmlContent,
            };
            await _httpClient.PostAsJsonAsync<OpmlImport>(url, data);
        }

        public async Task<string> ExportOpmlAsync()
        {
            var user = await userClient.GetFeedUserAsync();
            var url = $"api/feed/exportOpml?userId={user.Id}";
            try
            {
                return await _httpClient.GetStringAsync(url);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogWarning($"No OPML export found for user {user.Username}. Returning empty string.");
                return string.Empty;
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                // Clients from factory and DI-managed services should not be disposed here
                _disposed = true;
            }
        }
    }
}
