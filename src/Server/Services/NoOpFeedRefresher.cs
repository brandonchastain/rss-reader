using RssApp.Contracts;

namespace RssApp.ComponentServices
{
    /// <summary>
    /// No-op implementation of IFeedRefresher for read-only replica mode.
    /// Write operations are silently ignored since readers don't process feeds.
    /// </summary>
    public class NoOpFeedRefresher : IFeedRefresher
    {
        public Task AddFeedAsync(NewsFeed feed) => Task.CompletedTask;
        public Task RefreshAsync(RssUser user) => Task.CompletedTask;
        public Task<bool> HasNewItemsAsync(RssUser user) => Task.FromResult(false);
        public void ResetRefreshCooldown() { }
    }
}
