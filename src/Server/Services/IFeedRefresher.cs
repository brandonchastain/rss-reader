using RssApp.Contracts;

namespace RssApp.ComponentServices
{
    public interface IFeedRefresher
    {
        Task AddFeedAsync(NewsFeed feed);
        Task RefreshAsync(RssUser user);
        RefreshStatusResponse GetRefreshStatus(RssUser user);
        void ResetRefreshCooldown();

        /// <summary>
        /// Runs one pass of the background scheduler: fetches every distinct feed
        /// URL whose next-earliest-fetch time has arrived (once per URL) and fans
        /// the new items out to all subscribers. No-op in read-only mode.
        /// </summary>
        Task RunSchedulerTickAsync(CancellationToken token);
    }
}
