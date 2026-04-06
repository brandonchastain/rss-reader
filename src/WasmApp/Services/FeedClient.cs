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
        private readonly IUserClient userClient;
        private readonly ILogger<FeedClient> _logger;
        public bool IsFilterUnread { get; set; }
        public string FilterTag { get; set; }
        public bool IsFilterSaved { get; set; }
        private bool _disposed;

        public FeedClient(RssWasmConfig config, ILogger<FeedClient> logger, IUserClient userClient, IHttpClientFactory httpClientFactory)
        {
            _httpClient = httpClientFactory.CreateClient("ApiClient");
            _config = config;
            this.userClient = userClient;
            _logger = logger;
        }

        public async Task<IEnumerable<NewsFeed>> GetFeedsAsync()
        {
            return await _httpClient.GetFromJsonAsync<IEnumerable<NewsFeed>>($"{_config.ApiBaseUrl}api/feed");
        }

        public async Task AddFeedAsync(NewsFeed feed)
        {
            var user = await userClient.GetFeedUserAsync();
            feed.UserId = user.Id;

            await _httpClient.PostAsJsonAsync($"{_config.ApiBaseUrl}api/feed", feed);
        }

        public async Task AddTagAsync(NewsFeed feed, string tag)
        {
            feed.Tags ??= new List<string>();
            feed.Tags.Add(tag);
            await _httpClient.PostAsJsonAsync($"{_config.ApiBaseUrl}api/feed/tags", feed);
        }

        public async Task<IEnumerable<NewsFeedItem>> GetTimelineAsync(int page, int pageSize = 20, long? cursorPublishDateOrder = null, long? cursorId = null)
        {
            pageSize = Math.Min(pageSize, 500);
            var url = $"{_config.ApiBaseUrl}api/item/timeline?isFilterUnread={IsFilterUnread}&isFilterSaved={IsFilterSaved}&filterTag={FilterTag}&page={page}&pageSize={pageSize}";
            if (cursorPublishDateOrder.HasValue && cursorId.HasValue)
            {
                url += $"&cursorPublishDateOrder={cursorPublishDateOrder.Value}&cursorId={cursorId.Value}";
            }
            return await _httpClient.GetFromJsonAsync<IEnumerable<NewsFeedItem>>(url);
        }

        public async Task<IEnumerable<NewsFeedItem>> GetFeedItemsAsync(NewsFeed feed, int page, int pageSize = 20, long? cursorPublishDateOrder = null, long? cursorId = null)
        {
            pageSize = Math.Min(pageSize, 500);
            var url = $"{_config.ApiBaseUrl}api/item/feed?href={Uri.EscapeDataString(feed.Href)}&isFilterUnread={IsFilterUnread}&isFilterSaved={IsFilterSaved}&filterTag={FilterTag}&page={page}&pageSize={pageSize}";
            if (cursorPublishDateOrder.HasValue && cursorId.HasValue)
            {
                url += $"&cursorPublishDateOrder={cursorPublishDateOrder.Value}&cursorId={cursorId.Value}";
            }
            return await _httpClient.GetFromJsonAsync<IEnumerable<NewsFeedItem>>(url);
        }

        public async Task<IEnumerable<NewsFeedItem>> SearchItemsAsync(string query, int page, int pageSize = 20)
        {
            pageSize = Math.Min(pageSize, 500);
            var url = $"{_config.ApiBaseUrl}api/item/search?query={Uri.EscapeDataString(query)}&isFilterUnread={IsFilterUnread}&isFilterSaved={IsFilterSaved}&filterTag={FilterTag}&page={page}&pageSize={pageSize}";
            return await _httpClient.GetFromJsonAsync<IEnumerable<NewsFeedItem>>(url);
        }   

        public async Task MarkAsReadAsync(NewsFeedItem item, bool isRead)
        {
            await _httpClient.GetAsync($"{_config.ApiBaseUrl}api/item/markAsRead?itemId={item.Id}&isRead={isRead}");
        }

        public async Task<IEnumerable<string>> GetUserTagsAsync(RssUser _)
        {
            var user = await userClient.GetFeedUserAsync();
            return await _httpClient.GetFromJsonAsync<List<string>>($"{_config.ApiBaseUrl}api/feed/tags?userId={user.Id}");
        }

        public async Task<IEnumerable<TagSetting>> GetTagSettingsAsync()
        {
            return await _httpClient.GetFromJsonAsync<List<TagSetting>>($"{_config.ApiBaseUrl}api/feed/tagSettings");
        }

        public async Task<IEnumerable<TagSetting>> SetTagHiddenAsync(string tag, bool isHidden)
        {
            var setting = new TagSetting { Tag = tag, IsHidden = isHidden };
            var response = await _httpClient.PutAsJsonAsync($"{_config.ApiBaseUrl}api/feed/tagSettings", setting);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<List<TagSetting>>();
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
            try
            {
                var content = await _httpClient.GetFromJsonAsync<string>($"{_config.ApiBaseUrl}api/item/content?itemId={item.Id}");
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
            var url = $"{_config.ApiBaseUrl}api/feed/delete?href={Uri.EscapeDataString(feedHref)}";
            await _httpClient.PostAsync(url, null);
        }

        public async Task<bool> RefreshFeedsAsync()
        {
            var url = $"{_config.ApiBaseUrl}api/feed/refresh";
            var statusUrl = $"{_config.ApiBaseUrl}api/feed/refresh/status";

            try
            {
                // Fire the refresh — returns immediately (202 Accepted)
                var triggerResponse = await _httpClient.GetAsync(url);
                if (!triggerResponse.IsSuccessStatusCode)
                {
                    return false;
                }

                // Poll for completion
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while (!cts.IsCancellationRequested)
                {
                    await Task.Delay(1000, cts.Token);
                    var statusResponse = await _httpClient.GetAsync(statusUrl, cts.Token);
                    if (statusResponse.StatusCode == System.Net.HttpStatusCode.OK)
                    {
                        return true;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Polling timed out
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Feed refresh failed or timed out.");
            }

            return false;
        }

        public async Task ImportOpmlAsync(string opmlContent)
        {
            var user = await userClient.GetFeedUserAsync();
            var url = $"{_config.ApiBaseUrl}api/feed/importOpml/";
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
            var url = $"{_config.ApiBaseUrl}api/feed/exportOpml?userId={user.Id}";
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

        public async Task<bool> ClearAllItemsAsync()
        {
            var url = $"{_config.ApiBaseUrl}api/item/all";
            try
            {
                var response = await _httpClient.DeleteAsync(url);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clear all items.");
                return false;
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _httpClient.Dispose();
                userClient.Dispose();
                _disposed = true;
            }
        }
    }
}
