using RssApp.Contracts;

namespace RssApp.Data;

/// <summary>
/// Fetch state of one feed row. <see cref="NextFetchUtc"/> null means the feed
/// has never been scheduled and is due now.
/// </summary>
public sealed record FeedFetchState(
    int FeedId,
    string Url,
    string ETag,
    DateTimeOffset? LastModified,
    DateTimeOffset? LastFetchedUtc,
    DateTimeOffset? NextFetchUtc,
    int ConsecutiveFailures);

/// <summary>
/// Feeds are shared rows keyed by URL; a user's "feeds" are their Subscriptions.
/// <see cref="AddFeed"/>/<see cref="AddFeeds"/> subscribe, <see cref="DeleteFeed"/>/
/// <see cref="DeleteAllFeeds"/> unsubscribe (the method names are kept so the
/// controllers do not change). Tags are per-user entities attached to subscriptions.
/// </summary>
public interface IFeedRepository
{
    /// <summary>The user's subscriptions as NewsFeed views (FeedId = Feeds.Id, Href = Feeds.Url).</summary>
    IEnumerable<NewsFeed> GetFeeds(RssUser user);

    NewsFeed GetFeed(RssUser user, string url);

    /// <summary>Subscribe: upsert the feed by URL, insert the subscription, attach feed.Tags. Sets feed.FeedId.</summary>
    void AddFeed(NewsFeed feed);

    /// <summary>Subscribe to several feeds at once (OPML import), skipping ones already subscribed.</summary>
    void AddFeeds(RssUser user, IEnumerable<NewsFeed> feeds);

    /// <summary>
    /// The most-subscribed feeds the user is not yet subscribed to. Only feeds with at
    /// least <paramref name="minSubscribers"/> subscribers and no query string or
    /// credentials in the address are returned, so one user's private feed URL is
    /// never revealed to others.
    /// </summary>
    IReadOnlyList<PopularFeed> GetPopularFeeds(RssUser user, int limit, int minSubscribers);

    void AddTag(NewsFeed feed, string tag);

    /// <summary>Unsubscribe. The shared feed row and its posts stay.</summary>
    void DeleteFeed(RssUser user, string url);

    IEnumerable<TagSetting> GetTagSettings(RssUser user);

    void SetTagHidden(RssUser user, string tag, bool isHidden);

    /// <summary>URLs of the user's subscribed feeds that carry at least one hidden tag.</summary>
    IEnumerable<string> GetHiddenFeedUrls(RssUser user);

    /// <summary>Unsubscribe from everything (tags cascade).</summary>
    void DeleteAllFeeds(RssUser user);

    // --- Fetch scheduling (feed-level; the Feeds row is the source of truth) ---

    /// <summary>Fetch state of every feed that has at least one subscriber.</summary>
    IReadOnlyList<FeedFetchState> GetFeedFetchStates();

    /// <summary>Fetch state of one feed by URL, or null when no such feed row exists.</summary>
    FeedFetchState GetFeedFetchState(string url);

    /// <summary>Feeds whose NextFetchUtc is null or not after <paramref name="now"/>, among subscribed feeds.</summary>
    IReadOnlyList<FeedFetchState> GetDueFeeds(DateTimeOffset now);

    /// <summary>
    /// Records the outcome of a fetch attempt: schedule, validators (null keeps
    /// the stored ones only when <paramref name="keepValidators"/> is set) and
    /// the failure counter.
    /// </summary>
    void RecordFetchResult(
        string url,
        DateTimeOffset fetchedUtc,
        DateTimeOffset nextFetchUtc,
        TimeSpan interval,
        string etag,
        DateTimeOffset? lastModified,
        bool keepValidators,
        bool failed,
        string error);
}
