using RssApp.Contracts;

namespace RssApp.ComponentServices
{
    public interface IFeedRefresher
    {
        Task RefreshAsync(RssUser user);

        /// <summary>
        /// First fetch of a feed row that has never been fetched (just created by
        /// a subscription), so the subscriber sees posts right away instead of
        /// at the scheduler's next pass. A feed that was fetched before is left
        /// alone: its posts are already stored. One shared fetch, no per-user copy.
        /// </summary>
        Task FetchNewFeedAsync(string url);
        RefreshStatusResponse GetRefreshStatus(RssUser user);
        void ResetRefreshCooldown();

        /// <summary>
        /// Runs one pass of the background scheduler: fetches every subscribed
        /// feed whose next-fetch time has arrived (once per feed) and stores the
        /// new posts once.
        /// Returns the earliest time any feed is next due, so the caller can sleep
        /// until there is work instead of polling; null when nothing is scheduled.
        /// </summary>
        Task<DateTimeOffset?> RunSchedulerTickAsync(CancellationToken token);
    }
}
