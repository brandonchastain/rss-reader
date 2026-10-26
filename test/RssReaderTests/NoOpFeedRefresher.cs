using RssApp.ComponentServices;
using RssApp.Contracts;

namespace RssReaderTests;

/// <summary>
/// Offline stand-in for <see cref="IFeedRefresher"/>: the API contract tests must not fetch
/// feeds, so every refresh is a no-op.
/// </summary>
public class NoOpFeedRefresher : IFeedRefresher
{
    public Task RefreshAsync(RssUser user) => Task.CompletedTask;
    public Task FetchNewFeedAsync(string url) => Task.CompletedTask;
    public RefreshStatusResponse GetRefreshStatus(RssUser user) => new()
    {
        HasNewItems = false,
        IsRefreshing = false,
        PendingFeeds = 0
    };
    public void ResetRefreshCooldown() { }
    public Task<DateTimeOffset?> RunSchedulerTickAsync(CancellationToken token) => Task.FromResult<DateTimeOffset?>(null);
}
