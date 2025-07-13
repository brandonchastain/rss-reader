using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using RssApp.Contracts;
using RssApp.RssClient;
using Microsoft.AspNetCore.Components.Authorization;

namespace WasmApp.Services
{
    public class FeedClient : IFeedClient
    {
        private readonly HttpClient _httpClient;
        private readonly AuthenticationStateProvider _authenticationStateProvider;
        public bool IsFilterUnread { get; set; }
        public string FilterTag { get; set; }
        public bool IsFilterSaved { get; set; }
        private bool _disposed;

        public FeedClient(HttpClient httpClient, AuthenticationStateProvider authenticationStateProvider)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _authenticationStateProvider = authenticationStateProvider ?? throw new ArgumentNullException(nameof(authenticationStateProvider));
        }

        public async Task<IEnumerable<NewsFeed>> GetFeedsAsync()
        {
            var user = await GetFeedUser();
            return await _httpClient.GetFromJsonAsync<IEnumerable<NewsFeed>>($"api/feed?username={user.Username}");
        }

        public async Task AddFeedAsync(NewsFeed feed)
        {
            var user = await GetFeedUser();
            feed.UserId = user.Id;
            
            await _httpClient.PostAsJsonAsync("api/feed", feed);
        }

        public async Task AddTagAsync(NewsFeed feed, string tag)
        {
            await _httpClient.PostAsJsonAsync($"api/feed/{feed.Href}/tags", tag);
        }

        public async Task<IEnumerable<NewsFeedItem>> GetTimelineAsync(int page, int pageSize = 20)
        {
            var user = await GetFeedUser();
            var url = $"api/item/timeline?username={user.Username}&isFilterUnread={IsFilterUnread}&isFilterSaved={IsFilterSaved}&filterTag={FilterTag}&page={page}&pageSize={pageSize}";
            return await _httpClient.GetFromJsonAsync<IEnumerable<NewsFeedItem>>(url);
        }

        public async Task<IEnumerable<NewsFeedItem>> GetFeedItemsAsync(NewsFeed feed, int page)
        {
            var user = await GetFeedUser();
            var url = $"api/item/feed?username={user.Username}&href={Uri.EscapeDataString(feed.Href)}&isFilterUnread={IsFilterUnread}&isFilterSaved={IsFilterSaved}&filterTag={FilterTag}&page={page}";
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

        public async Task<string> GetItemContent(NewsFeedItem item)
        {
            var user = await GetFeedUser();
            var content = await _httpClient.GetFromJsonAsync<string>($"api/item/content?username={user.Username}&itemId={item.Id}");
            return content;
        }

        public async Task DeleteFeedAsync(string feedHref)
        {
            var user = await GetFeedUser();
            var url = $"api/feed/delete?href={Uri.EscapeDataString(feedHref)}&username={user.Username}";
            await _httpClient.PostAsync(url, null);
        }

        

        public async Task<RssUser> GetFeedUser()
        {
            var state = await _authenticationStateProvider.GetAuthenticationStateAsync();
            var username = state.User.Claims.FirstOrDefault(c => c.Type == "email").Value;
            return await this.RegisterUserAsync(username);
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
