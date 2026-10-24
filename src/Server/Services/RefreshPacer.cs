using RssApp.Config;

namespace RssReader.Server.Services;

/// <summary>
/// Keeps the background feed refresher from taking the one half-core the replica has
/// away from people. The scheduler may start at most
/// <see cref="RssAppConfig.SchedulerFetchesPerSecond"/> fetches a second
/// (<see cref="WaitForFetchSlotAsync"/>): due feeds are spread evenly over time instead
/// of fetched as fast as the concurrency gate allows, so a tick with thousands of due
/// feeds is a long trickle, not a burst. Manual refreshes do not take a slot: the person
/// asked for them.
///
/// A yield-to-live-traffic step (wait while any request is in flight) was built and
/// measured alongside this and dropped: alone it moved p95 at 50 req/s from 513 ms to
/// only 429 ms, because the pipeline is usually empty at the instant of the check and the
/// parse that then starts runs to completion, and under load it held budgets below
/// nominal. See test/load/REPORT.md, "Refresh pacing".
///
/// The clock and the delay are injectable so the tests run in milliseconds.
/// </summary>
public class RefreshPacer
{
    private readonly RssAppConfig config;
    private readonly Func<DateTimeOffset> now;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly object slotLock = new();
    private DateTimeOffset nextSlot = DateTimeOffset.MinValue;

    public RefreshPacer(RssAppConfig config)
        : this(config, () => DateTimeOffset.UtcNow, Task.Delay)
    {
    }

    internal RefreshPacer(
        RssAppConfig config,
        Func<DateTimeOffset> now,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        this.config = config;
        this.now = now;
        this.delay = delay;
    }

    /// <summary>
    /// Takes the next slot in the scheduler's fetch budget and waits until it arrives.
    /// Slots are handed out in call order, one per <c>1 / SchedulerFetchesPerSecond</c>;
    /// a caller that arrives after a quiet spell starts at once, the budget does not
    /// bank unused slots. A rate of zero or less disables pacing.
    /// </summary>
    public virtual Task WaitForFetchSlotAsync(CancellationToken token)
    {
        var rate = this.config.SchedulerFetchesPerSecond;
        if (rate <= 0 || double.IsNaN(rate) || double.IsInfinity(rate))
        {
            return Task.CompletedTask;
        }

        var spacing = TimeSpan.FromSeconds(1 / rate);
        TimeSpan wait;
        lock (this.slotLock)
        {
            var current = this.now();
            var slot = this.nextSlot > current ? this.nextSlot : current;
            this.nextSlot = slot + spacing;
            wait = slot - current;
        }

        return wait > TimeSpan.Zero ? this.delay(wait, token) : Task.CompletedTask;
    }
}
