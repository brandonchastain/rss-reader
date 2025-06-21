using RssApp.Data;
using RssApp.Contracts;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace RssWasmApp.Mocks
{
    public class MockItemRepository : IItemRepository
    {
        public Task<IEnumerable<NewsFeedItem>> GetItemsAsync(NewsFeed feed, bool isFilterUnread, bool isFilterSaved, string filterTag, int? page = null, int? pageSize = null, long? lastId = null, string? lastPublishDate = null)
        {
            return Task.FromResult<IEnumerable<NewsFeedItem>>([new NewsFeedItem("1", 1, "fake", "https://abc.com", null, "1/1/2021", "abcdef", null)]);
        }

        public Task<IEnumerable<NewsFeedItem>> SearchItemsAsync(string query, RssUser user, int page, int pageSize)
        {
            return Task.FromResult<IEnumerable<NewsFeedItem>>([new NewsFeedItem("1", 1, "fake", "https://abc.com", null, "1/1/2021", "abcdef", null)]);
        }

        public NewsFeedItem? GetItem(RssUser user, string href)
        {
            return new NewsFeedItem("1", 1, "fake", href, null, "1/1/2021", "abcdef", null);
        }

        public void AddItems(IEnumerable<NewsFeedItem> item) { }
        public void MarkAsRead(NewsFeedItem item, bool isRead) { }
        public void SavePost(NewsFeedItem item, RssUser user) { }
        public void UnsavePost(NewsFeedItem item, RssUser user) { }
        public void UpdateTags(NewsFeedItem item, string tags) { }
    }
}
