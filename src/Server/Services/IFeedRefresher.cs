using RssApp.Contracts;

namespace RssApp.ComponentServices
{
    public interface IFeedRefresher
    {
        Task RefreshAsync(RssUser user);
        RefreshStatusResponse GetRefreshStatus(RssUser user);
        void ResetRefreshCooldown();

        /// <summary>
        /// Runs one pass of the background scheduler: fetches every subscribed
        /// feed whose next-fetch time has arrived (once per feed) and stores the
        /// new posts once. No-op in read-only mode.
        /// Returns the earliest time any feed is next due, so the caller can sleep
        /// until there is work instead of polling; null when nothing is scheduled.
        /// </summary>
        Task<DateTimeOffset?> RunSchedulerTickAsync(CancellationToken token);
    }
}
