using System.Text.Json;
using Microsoft.JSInterop;
using RssApp.Contracts;

namespace WasmApp.Services;

/// <inheritdoc />
public class PostCache : IPostCache
{
    // Cap on how many timeline items are persisted. The timeline itself can hold
    // far more after paging (or a scroll restore), but only the newest page-worth
    // is worth rehydrating -- and it bounds what a cold start has to parse.
    public const int MaxTimelineItems = 50;

    private readonly IJSRuntime jsRuntime;
    private readonly ILogger<PostCache> logger;

    public PostCache(IJSRuntime jsRuntime, ILogger<PostCache> logger)
    {
        this.jsRuntime = jsRuntime;
        this.logger = logger;
    }

    public string Username { get; set; }

    public async Task<List<NewsFeedItem>> GetTimelineAsync()
    {
        var json = await GetSlotAsync("getTimeline");
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<List<NewsFeedItem>>(json);
        }
        catch (JsonException ex)
        {
            // A shape change or a truncated write: treat as a miss rather than
            // failing the page load.
            this.logger.LogWarning(ex, "Cached timeline could not be parsed; ignoring.");
            return null;
        }
    }

    public Task SetTimelineAsync(IEnumerable<NewsFeedItem> items)
    {
        if (items == null)
        {
            return Task.CompletedTask;
        }

        // Content lives in its own slot keyed by id; carrying it here too would
        // store every body twice.
        var trimmed = items
            .Take(MaxTimelineItems)
            .Select(i => new NewsFeedItem
            {
                Id = i.Id,
                FeedUrl = i.FeedUrl,
                UserId = i.UserId,
                Title = i.Title,
                Href = i.Href,
                CommentsHref = i.CommentsHref,
                PublishDate = i.PublishDate,
                PublishDateOrder = i.PublishDateOrder,
                ThumbnailUrl = i.ThumbnailUrl,
                IsRead = i.IsRead,
                IsSaved = i.IsSaved,
                FeedTags = i.FeedTags,
                Content = null,
            })
            .ToList();

        return SetSlotAsync("setTimeline", JsonSerializer.Serialize(trimmed));
    }

    public async Task<RssUser> GetUserAsync()
    {
        var json = await GetSlotAsync("getUser");
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RssUser>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task SetUserAsync(RssUser user)
        => user == null ? Task.CompletedTask : SetSlotAsync("setUser", JsonSerializer.Serialize(user));

    public async Task<List<string>> GetTagsAsync()
    {
        var json = await GetSlotAsync("getTags");
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task SetTagsAsync(IEnumerable<string> tags)
        => tags == null ? Task.CompletedTask : SetSlotAsync("setTags", JsonSerializer.Serialize(tags.ToList()));

    public async Task<string> GetContentBase64Async(string itemId)
    {
        if (string.IsNullOrEmpty(this.Username) || string.IsNullOrEmpty(itemId))
        {
            return null;
        }

        try
        {
            return await this.jsRuntime.InvokeAsync<string>("rssApp.cache.getContent", this.Username, itemId);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task MergeContentAsync(IDictionary<string, string> base64ById)
    {
        if (string.IsNullOrEmpty(this.Username) || base64ById == null || base64ById.Count == 0)
        {
            return;
        }

        try
        {
            await this.jsRuntime.InvokeVoidAsync(
                "rssApp.cache.mergeContent", this.Username, JsonSerializer.Serialize(base64ById));
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Failed to cache post content.");
        }
    }

    public async Task<List<PendingWrite>> GetPendingWritesAsync()
    {
        var json = await GetSlotAsync("getOutbox");
        if (string.IsNullOrWhiteSpace(json))
        {
            return new List<PendingWrite>();
        }

        try
        {
            var writes = JsonSerializer.Deserialize<List<PendingWrite>>(json) ?? new List<PendingWrite>();
            return PendingWrites.Prune(writes, NowMs());
        }
        catch (JsonException)
        {
            return new List<PendingWrite>();
        }
    }

    public Task SetPendingWritesAsync(IEnumerable<PendingWrite> writes)
        => SetSlotAsync("setOutbox", JsonSerializer.Serialize((writes ?? Enumerable.Empty<PendingWrite>()).ToList()));

    public async Task EnqueuePendingWriteAsync(NewsFeedItem item, string kind, bool value)
    {
        if (string.IsNullOrEmpty(this.Username) || string.IsNullOrEmpty(item?.Id))
        {
            return;
        }

        var now = NowMs();
        var existing = await GetPendingWritesAsync();
        var collapsed = PendingWrites.Collapse(existing, PendingWrite.For(item, kind, value, now), now);
        await SetPendingWritesAsync(collapsed);
    }

    public async Task PatchTimelineItemAsync(string itemId, bool? isRead, bool? isSaved)
    {
        if (string.IsNullOrEmpty(this.Username) || string.IsNullOrEmpty(itemId))
        {
            return;
        }

        var cached = await GetTimelineAsync();
        if (cached == null || cached.Count == 0)
        {
            return;
        }

        var target = cached.FirstOrDefault(i => i.Id == itemId);
        if (target == null)
        {
            return;
        }

        if (isRead.HasValue) target.IsRead = isRead.Value;
        if (isSaved.HasValue) target.IsSaved = isSaved.Value;

        await SetTimelineAsync(cached);
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public async Task ClearAsync()
    {
        try
        {
            await this.jsRuntime.InvokeVoidAsync("rssApp.cache.clear", this.Username);
        }
        catch (Exception)
        {
            // best-effort
        }
    }

    public async Task PruneAsync()
    {
        if (string.IsNullOrEmpty(this.Username))
        {
            return;
        }

        try
        {
            await this.jsRuntime.InvokeVoidAsync("rssApp.cache.prune", this.Username);
        }
        catch (Exception)
        {
            // best-effort
        }
    }

    private async Task<string> GetSlotAsync(string fn)
    {
        if (string.IsNullOrEmpty(this.Username))
        {
            return null;
        }

        try
        {
            return await this.jsRuntime.InvokeAsync<string>($"rssApp.cache.{fn}", this.Username);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task SetSlotAsync(string fn, string json)
    {
        if (string.IsNullOrEmpty(this.Username))
        {
            return;
        }

        try
        {
            await this.jsRuntime.InvokeVoidAsync($"rssApp.cache.{fn}", this.Username, json);
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Failed to write cache slot {Slot}.", fn);
        }
    }
}
