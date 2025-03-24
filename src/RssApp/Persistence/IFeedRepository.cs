
using RssApp.Contracts;

namespace RssApp.Persistence;

public interface IFeedRepository
{

    IEnumerable<NewsFeed> GetFeeds(RssUser user);

    void AddFeed(NewsFeed feed);

    void Update(NewsFeed feed);

    void DeleteFeed(RssUser user, string url);
}