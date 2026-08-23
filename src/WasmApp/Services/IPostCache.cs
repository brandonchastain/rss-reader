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

    Task<List<NewsFeedItem>> GetTimelineAsync();
    Task SetTimelineAsync(IEnumerable<NewsFeedItem> items);

    Task<RssUser> GetUserAsync();
    Task SetUserAsync(RssUser user);

    Task<List<string>> GetTagsAsync();
    Task SetTagsAsync(IEnumerable<string> tags);

    /// <summary>Base64 body for an item, or null when not cached.</summary>
    Task<string> GetContentBase64Async(string itemId);
    Task MergeContentAsync(IDictionary<string, string> base64ById);

    /// <summary>Drops every slot for the current user (sign-out, account delete).</summary>
    Task ClearAsync();

    /// <summary>Drops slots belonging to other users or older cache versions.</summary>
    Task PruneAsync();
}
