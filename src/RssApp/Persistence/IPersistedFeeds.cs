
using RssApp.Contracts;

namespace RssApp.Persistence;

public interface IPersistedFeeds
{

    IEnumerable<NewsFeed> GetFeeds();

    void AddFeed(NewsFeed feed);

    void Update(NewsFeed feed);

    void DeleteFeed(string url);
}