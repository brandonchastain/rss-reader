using RssApp.Contracts;

namespace RssApp.ComponentServices
{
    public interface IFeedRefresher : IDisposable
    {
        Task AddFeedAsync(NewsFeed feed);
        Task RefreshAsync(RssUser user);
        Task<bool> HasNewItemsAsync(RssUser user);
    }
}
