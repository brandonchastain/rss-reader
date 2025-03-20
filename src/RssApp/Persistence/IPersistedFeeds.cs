
namespace RssApp.Persistence;

public interface IPersistedFeeds
{

    IEnumerable<string> GetFeeds();

    void AddFeed(string url);

    void DeleteFeed(string url);
}