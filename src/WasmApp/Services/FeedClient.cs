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
        private readonly IPostCache postCache;
        private readonly ILogger<FeedClient> _logger;
        public bool IsFilterUnread { get; set; }
        public string FilterTag { get; set; }
        public bool IsFilterSaved { get; set; }
        private bool _disposed;

        public FeedClient(RssWasmConfig config, ILogger<FeedClient> logger, IUserClient userClient, IPostCache postCache, IHttpClientFactory httpClientFactory)
        {
            _httpClient = httpClientFactory.CreateClient("ApiClient");
            _config = config;
            this.userClient = userClient;
            this.postCache = postCache;
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

        public async Task<int> GetNewTimelineCountAsync(long cursorPublishDateOrder, long cursorId)
        {
            var url = $"{_config.ApiBaseUrl}api/item/newCount?cursorPublishDateOrder={cursorPublishDateOrder}&cursorId={cursorId}&isFilterUnread={IsFilterUnread}&isFilterSaved={IsFilterSaved}&filterTag={FilterTag}";
            return await _httpClient.GetFromJsonAsync<int>(url);
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
            var delivered = await TrySendAsync(
                () => _httpClient.GetAsync($"{_config.ApiBaseUrl}api/item/markAsRead?itemId={item.Id}&isRead={isRead}"));

            if (!delivered)
            {
                await this.postCache.EnqueuePendingWriteAsync(item, PendingWrite.ReadKind, isRead);
            }

            // The cached copy tracks what the user did either way, so a reload
            // shows their own state rather than the server's older value.
            await this.postCache.PatchTimelineItemAsync(item.Id, isRead: isRead, isSaved: null);
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

        public Task SavePostAsync(NewsFeedItem item) => SetSavedAsync(item, true);

        public Task UnsavePostAsync(NewsFeedItem item) => SetSavedAsync(item, false);

        private async Task SetSavedAsync(NewsFeedItem item, bool isSaved)
        {
            var route = isSaved ? "save" : "unsave";
            var delivered = await TrySendAsync(
                () => _httpClient.PostAsJsonAsync($"{_config.ApiBaseUrl}api/item/{route}", item));

            if (!delivered)
            {
                await this.postCache.EnqueuePendingWriteAsync(item, PendingWrite.SavedKind, isSaved);
            }

            await this.postCache.PatchTimelineItemAsync(item.Id, isRead: null, isSaved: isSaved);
        }

        // Replays queued read/saved changes. Every write is idempotent, so a
        // replay that races a successful original is harmless. Entries that fail
        // again stay queued for the next attempt.
        public async Task<int> FlushPendingWritesAsync()
        {
            var pending = await this.postCache.GetPendingWritesAsync();
            if (pending.Count == 0)
            {
                return 0;
            }

            var remaining = new List<PendingWrite>();
            foreach (var write in pending)
            {
                bool delivered = write.Kind switch
                {
                    PendingWrite.ReadKind => await TrySendAsync(
                        () => _httpClient.GetAsync($"{_config.ApiBaseUrl}api/item/markAsRead?itemId={write.ItemId}&isRead={write.Value}")),
                    PendingWrite.SavedKind => await TrySendAsync(
                        () => _httpClient.PostAsJsonAsync(
                            $"{_config.ApiBaseUrl}api/item/{(write.Value ? "save" : "unsave")}", write.ToItem())),
                    // An unknown kind can only come from a newer build's queue;
                    // drop it rather than retrying forever.
                    _ => true,
                };

                if (!delivered)
                {
                    remaining.Add(write);
                }
            }

            await this.postCache.SetPendingWritesAsync(remaining);

            var flushed = pending.Count - remaining.Count;
            if (flushed > 0)
            {
                _logger.LogInformation("Replayed {Count} pending write(s).", flushed);
            }

            return flushed;
        }

        // A non-2xx response is a failure. HttpClient only throws on transport
        // errors, so without this check a 503 from the proxy -- exactly what a
        // stopped backend returns -- would look like a successful write.
        private static async Task<bool> TrySendAsync(Func<Task<HttpResponseMessage>> send)
        {
            try
            {
                using var response = await send();
                return response.IsSuccessStatusCode;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public async Task<string> GetItemContentAsync(NewsFeedItem item)
        {
            // Read through the browser cache first: a body stored on a previous
            // visit renders immediately instead of waiting out a container wake.
            var cached = await this.postCache.GetContentBase64Async(item.Id);
            var fromCache = TryDecodeContent(cached);
            if (fromCache != null)
            {
                return fromCache;
            }

            try
            {
                var content = await _httpClient.GetFromJsonAsync<string>($"{_config.ApiBaseUrl}api/item/content?itemId={item.Id}");
                var decoded = TryDecodeContent(content);
                if (decoded != null)
                {
                    // Only successful fetches are cached. A 404 today can become
                    // real content after a later refresh, so caching the failure
                    // would pin it permanently.
                    await this.postCache.MergeContentAsync(new Dictionary<string, string> { [item.Id] = content });
                    return decoded;
                }
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // a 404 can happen. Ignore this. It probably means content was totally empty.
            }

            return "[no content found]";
        }

        // Fetches the bodies for a page of items in a single request and stores
        // them, so the next cold start can show full posts and not just titles.
        // Best-effort: a failure here costs nothing but a cache miss later.
        public async Task<int> PrefetchContentAsync(IEnumerable<string> itemIds)
        {
            var ids = (itemIds ?? Enumerable.Empty<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct()
                .Take(PostCache.MaxTimelineItems)
                .ToList();

            if (ids.Count == 0)
            {
                return 0;
            }

            try
            {
                var url = $"{_config.ApiBaseUrl}api/item/contentBatch?itemIds={Uri.EscapeDataString(string.Join(",", ids))}";
                var map = await _httpClient.GetFromJsonAsync<Dictionary<string, string>>(url);
                if (map == null || map.Count == 0)
                {
                    return 0;
                }

                await this.postCache.MergeContentAsync(map);
                return map.Count;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Content prefetch failed.");
                return 0;
            }
        }

        private static string TryDecodeContent(string base64)
        {
            if (string.IsNullOrWhiteSpace(base64))
            {
                return null;
            }

            try
            {
                return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            }
            catch (FormatException)
            {
                return null;
            }
        }

        public async Task DeleteFeedAsync(string feedHref)
        {
            var url = $"{_config.ApiBaseUrl}api/feed/delete?href={Uri.EscapeDataString(feedHref)}";
            await _httpClient.PostAsync(url, null);
        }

        public async Task TriggerRefreshAsync()
        {
            // Kick off the server-side background refresh and return immediately.
            // The server enqueues the work and responds 202 Accepted.
            await _httpClient.GetAsync($"{_config.ApiBaseUrl}api/feed/refresh");
        }

        public async Task<RefreshStatusResponse> GetRefreshStatusAsync()
        {
            try
            {
                return await _httpClient.GetFromJsonAsync<RefreshStatusResponse>(
                    $"{_config.ApiBaseUrl}api/feed/refresh/status");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read refresh status.");
                return null;
            }
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
