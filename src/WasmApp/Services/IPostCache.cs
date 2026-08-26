using RssApp.Contracts;

namespace WasmApp.Services;

/// <summary>
/// Browser-local cache of the last-seen timeline, the user record, the tag list,
/// and post bodies. The API scales to zero, so waking it costs ~20s of platform
/// time no request can avoid; this lets the app paint from the previous session
/// immediately and reconcile with the server in the background.
///
/// All members no-op (returning null/empty) until <see cref="Username"/> is set,
/// and every operation swallows storage failures -- a browser with storage
/// disabled degrades to the uncached behaviour rather than breaking.
/// </summary>
public interface IPostCache
{
    /// <summary>
    /// Cache partition key. Set from the Easy Auth identity, which resolves
    /// against the always-warm Static Web App rather than the container.
    /// </summary>
    string Username { get; set; }

    /// <summary>
    /// The cached view for one filter signature (see
    /// <see cref="PostCache.SignatureFor"/>). Each filter -- unfiltered, unread,
    /// saved, a tag -- gets its own slot, so switching filters hydrates from the
    /// last time that view was open instead of waiting on the API.
    /// </summary>
    Task<List<NewsFeedItem>> GetTimelineAsync(string signature);
    Task SetTimelineAsync(string signature, IEnumerable<NewsFeedItem> items);

    Task<RssUser> GetUserAsync();
    Task SetUserAsync(RssUser user);

    Task<List<string>> GetTagsAsync();
    Task SetTagsAsync(IEnumerable<string> tags);

    /// <summary>Base64 body for an item, or null when not cached.</summary>
    Task<string> GetContentBase64Async(string itemId);
    Task MergeContentAsync(IDictionary<string, string> base64ById);

    /// <summary>
    /// Read/saved changes that have not reached the server. Writes are attempted
    /// immediately and only queued when the API rejects or cannot be reached, so
    /// this is normally empty.
    /// </summary>
    Task<List<PendingWrite>> GetPendingWritesAsync();
    Task SetPendingWritesAsync(IEnumerable<PendingWrite> writes);
    Task EnqueuePendingWriteAsync(NewsFeedItem item, string kind, bool value);

    /// <summary>
    /// Updates one item's flags in the cached timeline so a reload reflects what
    /// the user actually did, whether or not the server has heard about it yet.
    /// </summary>
    Task PatchTimelineItemAsync(string itemId, bool? isRead, bool? isSaved);

    /// <summary>Drops every slot for the current user (sign-out, account delete).</summary>
    Task ClearAsync();

    /// <summary>Drops slots belonging to other users or older cache versions.</summary>
    Task PruneAsync();
}
