using System.Collections.Generic;
using System.Threading.Tasks;
using RssApp.Contracts;

namespace RssApp.RssClient;

public interface IFeedClient : IDisposable
{
    bool IsFilterUnread { get; set; }
    string FilterTag { get; set; }
    bool IsFilterSaved { get; set; }

    Task AddFeedAsync(NewsFeed feed);
    Task<IEnumerable<NewsFeed>> GetFeedsAsync();
    Task<IEnumerable<NewsFeedItem>> GetTimelineAsync(int page, int pageSize = 20, long? cursorPublishDateOrder = null, long? cursorId = null);
    // Cheap probe for an open timeline: how many items are newer than the client's
    // newest currently-loaded item (cursor). Honors the active filters.
    Task<int> GetNewTimelineCountAsync(long cursorPublishDateOrder, long cursorId);
    Task<IEnumerable<NewsFeedItem>> GetFeedItemsAsync(NewsFeed feed, int page, int pageSize = 20, long? cursorPublishDateOrder = null, long? cursorId = null);
    Task<IEnumerable<NewsFeedItem>> SearchItemsAsync(string query, int page, int pageSize = 20);
    // Fire-and-forget: kick off a server-side background refresh and return
    // immediately (server responds 202). Poll GetRefreshStatusAsync for progress.
    Task TriggerRefreshAsync();
    Task<RefreshStatusResponse> GetRefreshStatusAsync();
    Task<string> GetItemContentAsync(NewsFeedItem item);
    Task AddTagAsync(NewsFeed feed, string tag);
    Task<IEnumerable<string>> GetUserTagsAsync(RssUser user);
    Task<IEnumerable<TagSetting>> GetTagSettingsAsync();
    Task<IEnumerable<TagSetting>> SetTagHiddenAsync(string tag, bool isHidden);
    Task MarkAsReadAsync(NewsFeedItem item, bool isRead);
    Task SavePostAsync(NewsFeedItem item);
    Task UnsavePostAsync(NewsFeedItem item);
    Task DeleteFeedAsync(string feedHref);
    Task<bool> ClearAllItemsAsync();
    Task ImportOpmlAsync(string opmlContent);
    Task<string> ExportOpmlAsync();
}