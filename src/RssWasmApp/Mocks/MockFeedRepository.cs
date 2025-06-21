using RssApp.Data;
using RssApp.Contracts;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace RssWasmApp.Mocks
{
    public class MockFeedRepository : IFeedRepository
    {

        public NewsFeed GetFeed(RssUser user, string url) => new NewsFeed(url, user.Id);
        public IEnumerable<NewsFeed> GetFeeds(RssUser user) => [new NewsFeed("Mock Feed", user.Id)];
        public void AddFeed(NewsFeed feed) { }
        public void Update(NewsFeed feed) { }
        public void DeleteFeed(RssUser user, string url) { }
        public void AddTag(NewsFeed feed, string tag) { }
        public void ImportFeeds(RssUser user, IEnumerable<NewsFeed> feeds) { }
    }
}
