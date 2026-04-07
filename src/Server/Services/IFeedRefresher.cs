using RssApp.Contracts;

namespace RssApp.ComponentServices
{
    public interface IFeedRefresher
    {
        Task AddFeedAsync(NewsFeed feed);
        Task RefreshAsync(RssUser user);
        RefreshStatusResponse GetRefreshStatus(RssUser user);
        void ResetRefreshCooldown();
    }
}
