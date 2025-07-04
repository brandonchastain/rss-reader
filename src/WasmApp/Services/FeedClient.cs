using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using RssApp.Contracts;
using RssApp.RssClient;

namespace WasmApp.Services
{
    public class FeedClient : IFeedClient
    {
        private readonly HttpClient _httpClient;
        public bool IsFilterUnread { get; set; }
        public string FilterTag { get; set; }
        public bool IsFilterSaved { get; set; }
        private bool _disposed;

        public FeedClient(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public async Task<IEnumerable<NewsFeed>> GetFeedsAsync()
        {
            return await _httpClient.GetFromJsonAsync<IEnumerable<NewsFeed>>("api/feed");
        }

        public async Task AddFeedAsync(NewsFeed feed)
        {
            await _httpClient.PostAsJsonAsync("api/feed", feed);
        }

        public async Task AddTagAsync(NewsFeed feed, string tag)
        {
            await _httpClient.PostAsJsonAsync($"api/feed/{feed.Href}/tags", tag);
        }

        public async Task<IEnumerable<NewsFeedItem>> GetTimelineAsync(int page, int pageSize = 20)
        {
            var url = $"api/item/timeline?username={feedUser()}&isFilterUnread={IsFilterUnread}&isFilterSaved={IsFilterSaved}&filterTag={FilterTag}&page={page}&pageSize={pageSize}";
            return await _httpClient.GetFromJsonAsync<IEnumerable<NewsFeedItem>>(url);
        }

        public async Task<IEnumerable<NewsFeedItem>> GetFeedItemsAsync(NewsFeed feed, int page)
        {
            var url = $"api/item/feed?username={feedUser()}&href={Uri.EscapeDataString(feed.Href)}&isFilterUnread={IsFilterUnread}&isFilterSaved={IsFilterSaved}&filterTag={FilterTag}&page={page}";
            return await _httpClient.GetFromJsonAsync<IEnumerable<NewsFeedItem>>(url);
        }

        public async Task<IEnumerable<NewsFeedItem>> SearchItemsAsync(string query, int page, int pageSize = 20)
        {
            var url = $"api/item/search?query={Uri.EscapeDataString(query)}&isFilterUnread={IsFilterUnread}&isFilterSaved={IsFilterSaved}&filterTag={FilterTag}&page={page}&pageSize={pageSize}";
            return await _httpClient.GetFromJsonAsync<IEnumerable<NewsFeedItem>>(url);
        }

        public void MarkAsRead(NewsFeedItem item, bool isRead)
        {
            // Implement as needed, e.g., send PATCH/POST to API
        }

        public async Task<RssUser> RegisterUserAsync(string username)
        {
            var response = await _httpClient.PostAsJsonAsync($"api/user/register", username);
            return await response.Content.ReadFromJsonAsync<RssUser>();
        }

        public IEnumerable<string> GetUserTags(RssUser user)
        {
            // Implement as needed, e.g., GET api/user/{user.Id}/tags
            return new List<string>();
        }

        public async Task SavePostAsync(NewsFeedItem item)
        {
            await _httpClient.PostAsJsonAsync("api/item/save", item);
        }

        public async Task UnsavePostAsync(NewsFeedItem item)
        {
            await _httpClient.PostAsJsonAsync("api/item/unsave", item);
        }

        public string GetItemContent(NewsFeedItem item)
        {
            // This should be async, but IFeedClient defines it as sync
            // Consider refactoring interface if possible
            throw new NotImplementedException();
        }

        private string feedUser() => "demo"; // Replace with actual user context

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
            }
        }
    }
}
