using System.Collections.Generic;
using System.Threading.Tasks;
using RssApp.Contracts;

namespace RssApp.RssClient;

public interface IFeedClient : IDisposable
{
    Task<IEnumerable<NewsFeed>> GetFeedsAsync();
    Task AddFeedAsync(NewsFeed feed);
    Task AddTagAsync(NewsFeed feed, string tag);
    Task<RssUser> GetFeedUserAsync();

    Task<IEnumerable<NewsFeedItem>> GetTimelineAsync(int page, int pageSize = 20);
    Task<IEnumerable<NewsFeedItem>> GetFeedItemsAsync(NewsFeed feed, int page);
    Task<IEnumerable<NewsFeedItem>> SearchItemsAsync(string query, int page, int pageSize = 20);
    Task MarkAsReadAsync(NewsFeedItem item, bool isRead);
    Task RefreshFeedsAsync();
    Task<IEnumerable<string>> GetUserTagsAsync(RssUser user);
    Task SavePostAsync(NewsFeedItem item);
    Task UnsavePostAsync(NewsFeedItem item);

    Task DeleteFeedAsync(string feedHref);

    Task<string> GetItemContentAsync(NewsFeedItem item);
    Task ImportOpmlAsync(string opmlContent);

    
    bool IsFilterUnread { get; set; }
    string FilterTag { get; set; }
    bool IsFilterSaved { get; set; }
}