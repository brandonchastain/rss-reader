using RssApp.Contracts;

namespace RssApp.Data;

public interface IFeedRepository
{

    IEnumerable<NewsFeed> GetFeeds(RssUser user);

    void AddFeed(NewsFeed feed);

    void Update(NewsFeed feed);

    void DeleteFeed(RssUser user, string url);

    void AddTag(NewsFeed feed, string tag);

    void ImportFeeds(RssUser user, IEnumerable<NewsFeed> feeds);

}