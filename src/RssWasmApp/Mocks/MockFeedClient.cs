using RssApp.RssClient;
using RssApp.Contracts;
using RssApp.ComponentServices;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace RssWasmApp.Mocks
{
    public class MockFeedClient : IFeedClient
    {
        public Task<IEnumerable<NewsFeed>> GetFeedsAsync() => Task.FromResult<IEnumerable<NewsFeed>>(new List<NewsFeed>{ new NewsFeed("Mock Feed", 1) });
        public Task AddFeedAsync(NewsFeed feed) => Task.CompletedTask;
        public Task AddTagAsync(NewsFeed feed, string tag) => Task.CompletedTask;
        public Task<IEnumerable<NewsFeedItem>> GetTimelineAsync(int page, int pageSize = 20) => Task.FromResult<IEnumerable<NewsFeedItem>>(new List<NewsFeedItem> { new NewsFeedItem("1", 1, "fake", "https://abc.com", null, "1/1/2021", "abcdef", null) });
        public Task<IEnumerable<NewsFeedItem>> GetFeedItemsAsync(NewsFeed feed, int page) => Task.FromResult<IEnumerable<NewsFeedItem>>([new NewsFeedItem("1", 1, "fake", "https://abc.com", null, "1/1/2021", "abcdef", null)]);
        public Task<IEnumerable<NewsFeedItem>> SearchItemsAsync(string query, int page, int pageSize = 20) => Task.FromResult<IEnumerable<NewsFeedItem>>(new List<NewsFeedItem>());
        public void MarkAsRead(NewsFeedItem item, bool isRead) { }
        public Task<RssUser> RegisterUserAsync(string username) => Task.FromResult(new RssUser(username, 1));
        public IEnumerable<string> GetUserTags(RssUser user) => new List<string>();
        public Task SavePostAsync(NewsFeedItem item) => Task.CompletedTask;
        public Task UnsavePostAsync(NewsFeedItem item) => Task.CompletedTask;
        public bool IsFilterUnread { get; set; }
        public string FilterTag { get; set; } = string.Empty;
        public bool IsFilterSaved { get; set; }
    }
}
