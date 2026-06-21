using RssApp.Contracts;

namespace RssApp.Data;

public interface IFeedRepository
{
    IEnumerable<NewsFeed> GetFeeds(RssUser user);

    NewsFeed GetFeed(RssUser user, string url);

    void AddFeeds(RssUser user, IEnumerable<NewsFeed> feeds);

    void AddFeed(NewsFeed feed);

    void AddTag(NewsFeed feed, string tag);

    void DeleteFeed(RssUser user, string url);

    // Scheduler support: the set of DISTINCT feed URLs across all users (the unit
    // the background scheduler fetches), and every subscriber row for a given URL
    // (so one fetch can fan its items out to all subscribers).
    IEnumerable<string> GetAllDistinctFeedUrls();

    IEnumerable<NewsFeed> GetFeedsByUrl(string url);

    IEnumerable<TagSetting> GetTagSettings(RssUser user);

    void SetTagHidden(RssUser user, string tag, bool isHidden);

    IEnumerable<string> GetHiddenFeedUrls(RssUser user);

    void DeleteAllFeeds(RssUser user);
}