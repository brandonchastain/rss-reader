using System.Collections.Generic;
using System.Threading.Tasks;
using RssApp.Contracts;

namespace RssApp.RssClient;

public interface IFeedClient : IDisposable
{
    bool IsFilterUnread { get; set; }
    string FilterTag { get; set; }
    bool IsFilterSaved { get; set; }

    Task AddFeedAsync(NewsFeed feed);
    Task<IEnumerable<NewsFeed>> GetFeedsAsync();
    Task<IEnumerable<NewsFeedItem>> GetTimelineAsync(int page, int pageSize = 20);
    Task<IEnumerable<NewsFeedItem>> GetFeedItemsAsync(NewsFeed feed, int page);
    Task<IEnumerable<NewsFeedItem>> SearchItemsAsync(string query, int page, int pageSize = 20);
    Task RefreshFeedsAsync();
    Task<string> GetItemContentAsync(NewsFeedItem item);
    Task AddTagAsync(NewsFeed feed, string tag);
    Task<IEnumerable<string>> GetUserTagsAsync(RssUser user);
    Task MarkAsReadAsync(NewsFeedItem item, bool isRead);
    Task SavePostAsync(NewsFeedItem item);
    Task UnsavePostAsync(NewsFeedItem item);
    Task DeleteFeedAsync(string feedHref);
    Task ImportOpmlAsync(string opmlContent);
    Task<string> ExportOpmlAsync();
}