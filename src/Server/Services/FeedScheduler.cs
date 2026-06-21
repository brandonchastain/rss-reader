using RssApp.Config;

namespace RssApp.ComponentServices;

/// <summary>
/// Drives the per-URL feed refresh cadence. On each tick it asks the refresher to
/// fetch every distinct feed URL whose next-earliest-fetch time has arrived (once
/// per URL) and fan the new items out to all subscribers. This decouples fetching
/// from user actions, so a feed shared by many users is fetched on a single schedule
/// rather than once per user per refresh.
/// </summary>
public class FeedScheduler : BackgroundService
{
    private readonly ILogger<FeedScheduler> logger;
    private readonly IFeedRefresher refresher;
    private readonly RssAppConfig config;

    public FeedScheduler(
        ILogger<FeedScheduler> logger,
        IFeedRefresher refresher,
        RssAppConfig config)
    {
        this.logger = logger;
        this.refresher = refresher;
        this.config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!this.config.SchedulerEnabled)
        {
            this.logger.LogInformation("Feed scheduler is disabled (SchedulerEnabled=false).");
            return;
        }

        this.logger.LogInformation(
            "Feed scheduler started: tick every {tick}, default interval {interval} (floor {floor}, max {max}).",
            this.config.SchedulerTickInterval, this.config.FeedRefreshInterval,
            this.config.FeedRefreshIntervalFloor, this.config.FeedRefreshIntervalMax);

        // Let startup settle (DB restore, warmup) before the first tick.
        if (!await DelayAsync(TimeSpan.FromSeconds(15), stoppingToken))
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await this.refresher.RunSchedulerTickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "Feed scheduler tick failed; will retry next tick.");
            }

            if (!await DelayAsync(this.config.SchedulerTickInterval, stoppingToken))
            {
                break;
            }
        }
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
