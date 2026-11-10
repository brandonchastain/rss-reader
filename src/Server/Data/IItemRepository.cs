using RssApp.Contracts;

namespace RssApp.Data;

/// <summary>What a feed write changed: posts added, and existing posts renamed or re-linked.</summary>
public readonly record struct ItemWriteResult(int Inserted, int Updated);

public interface IItemRepository : IDisposable
{
    Task<IEnumerable<NewsFeedItem>> GetItemsAsync(NewsFeed feed, bool isFilterUnread, bool isFilterSaved, string filterTag, int? page = null, int? pageSize = null, long? lastId = null, long? lastPublishDateOrder = null, IEnumerable<string> excludeFeedUrls = null, bool includeContent = true);
    /// <summary>
    /// Counts the user's timeline items strictly newer than the cursor
    /// (PublishDateOrder, Id), mirroring the timeline ordering and filters.
    /// Cheap indexed COUNT — fetches no rows.
    /// </summary>
    Task<int> GetNewItemCountAsync(NewsFeed feed, bool isFilterUnread, bool isFilterSaved, string filterTag, long cursorPublishDateOrder, long cursorId, IEnumerable<string> excludeFeedUrls = null);
    Task<IEnumerable<NewsFeedItem>> SearchItemsAsync(string query, RssUser user, int page, int pageSize);
    NewsFeedItem GetItem(RssUser user, string href);
    NewsFeedItem GetItem(RssUser user, int itemId);
    /// <summary>Stores a feed's items (see <see cref="UpsertItemsAsync"/>). Returns the number of new posts.</summary>
    Task<int> AddItemsAsync(IEnumerable<NewsFeedItem> item);

    /// <summary>
    /// Stores a feed's items: new posts are inserted, and a post the feed has
    /// renamed or re-linked (same guid, new title or URL) is updated in place.
    /// The default covers implementations that only insert.
    /// </summary>
    async Task<ItemWriteResult> UpsertItemsAsync(IEnumerable<NewsFeedItem> items)
        => new ItemWriteResult(await AddItemsAsync(items), 0);
    void MarkAsRead(NewsFeedItem item, bool isRead, RssUser user);
    void SavePost(NewsFeedItem item, RssUser user);
    void UnsavePost(NewsFeedItem item, RssUser user);
    void UpdateTags(NewsFeedItem item, string tags);
    string GetItemContent(NewsFeedItem item);
    Task DeleteAllItemsAsync(RssUser user);
    int GetItemCountForFeed(RssUser user, string feedUrl);
}