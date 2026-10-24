using RssApp.Config;

namespace RssApp.ComponentServices;

/// <summary>
/// Drives the per-URL feed refresh cadence. On each pass it asks the refresher to
/// fetch every distinct feed URL whose next-earliest-fetch time has arrived (once
/// per URL) and fan the new items out to all subscribers. This decouples fetching
/// from user actions, so a feed shared by many users is fetched on a single schedule
/// rather than once per user per refresh.
///
/// The loop is event-driven rather than polled: after each pass it sleeps until the
/// earliest feed is next due, so an idle replica does no scheduler work at all
/// between fetches. <see cref="RssAppConfig.SchedulerTickInterval"/> only caps the
/// sleep as a safety net (a brand-new feed is fetched inline by
/// FetchNewFeedAsync when it is added, so the scheduler never has to notice new
/// feeds quickly).
/// </summary>
public class FeedScheduler : BackgroundService
{
    private readonly ILogger<FeedScheduler> logger;
    private readonly IFeedRefresher refresher;
    private readonly RssAppConfig config;

    private readonly RssReader.Server.Services.WriterState? writerState;

    public FeedScheduler(
        ILogger<FeedScheduler> logger,
        IFeedRefresher refresher,
        RssAppConfig config,
        RssReader.Server.Services.WriterState? writerState = null)
    {
        this.logger = logger;
        this.refresher = refresher;
        this.config = config;
        this.writerState = writerState;
    }

    protected override async Task ExecuteAsync(CancellationToken hostStopping)
    {
        // Also stops at a cooperative handoff (WriterState.WritersStopping), before the
        // database leaves this process; the loop below exits cleanly on either token.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            hostStopping, this.writerState?.WritersStopping ?? CancellationToken.None);
        var stoppingToken = linked.Token;

        if (!this.config.SchedulerEnabled)
        {
            this.logger.LogInformation("Feed scheduler is disabled (SchedulerEnabled=false).");
            return;
        }

        this.logger.LogInformation(
            "Feed scheduler started: sleeps until next due feed (max {tick}), default interval {interval} (floor {floor}, max {max}).",
            this.config.SchedulerTickInterval, this.config.FeedRefreshInterval,
            this.config.FeedRefreshIntervalFloor, this.config.FeedRefreshIntervalMax);

        // Let startup settle (DB restore, warmup) before the first tick, and leave
        // the cold-start window clear for the request that woke the container.
        if (!await DelayAsync(this.config.SchedulerStartupDelay, stoppingToken))
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            DateTimeOffset? nextDue = null;
            try
            {
                nextDue = await this.refresher.RunSchedulerTickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "Feed scheduler pass failed; will retry after the maximum sleep.");
            }

            if (!await DelayAsync(SleepUntil(nextDue, DateTimeOffset.UtcNow, this.config.SchedulerTickInterval), stoppingToken))
            {
                break;
            }
        }
    }

    /// <summary>
    /// How long to sleep before the next pass: until <paramref name="nextDue"/>,
    /// clamped to [1 s, <paramref name="maxSleep"/>]. The floor stops a feed that is
    /// already overdue (or a clock skew) from spinning the loop; the cap bounds the
    /// damage if the schedule is empty or wrong. Exposed for tests.
    /// </summary>
    internal static TimeSpan SleepUntil(DateTimeOffset? nextDue, DateTimeOffset now, TimeSpan maxSleep)
    {
        var floor = TimeSpan.FromSeconds(1);
        if (maxSleep < floor)
        {
            floor = maxSleep;
        }

        if (nextDue is null)
        {
            return maxSleep;
        }

        var wait = nextDue.Value - now;
        if (wait < floor) return floor;
        if (wait > maxSleep) return maxSleep;
        return wait;
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken token)
    {
        try
        {
            await Task.Delay(delay, token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
