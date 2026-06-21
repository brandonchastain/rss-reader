namespace RssApp.Data;

/// <summary>
/// Persisted HTTP conditional-GET validators for a feed URL. Stored keyed by
/// URL (not per user), so multiple subscribers to the same feed share one
/// validator — and so the values survive an app restart, letting the refresher
/// resume sending conditional GETs instead of cold-fetching every feed.
/// </summary>
/// <param name="ETag">Serialized ETag (the full header form, e.g. <c>"abc"</c> or <c>W/"abc"</c>), or null.</param>
/// <param name="LastModified">Last-Modified timestamp, or null.</param>
public sealed record FeedValidator(string ETag, DateTimeOffset? LastModified);

public interface IFeedValidatorStore
{
    /// <summary>Returns the stored validators for a URL, or null if none are recorded.</summary>
    FeedValidator Get(string url);

    /// <summary>
    /// Upserts the validators for a URL. Passing both values null removes the row.
    /// </summary>
    void Set(string url, string etag, DateTimeOffset? lastModified);
}
