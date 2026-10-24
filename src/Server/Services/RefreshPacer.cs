using RssApp.Config;

namespace RssReader.Server.Services;

/// <summary>
/// Keeps the background feed refresher from competing with people for the one
/// half-core the replica has. Two mechanisms, both consulted by <c>FeedRefresher</c>:
///
/// <list type="bullet">
/// <item><b>Budget</b> (<see cref="WaitForFetchSlotAsync"/>): the scheduler may start at
/// most <see cref="RssAppConfig.SchedulerFetchesPerSecond"/> fetches a second. Due feeds
/// are spread evenly over time instead of fetched as fast as the concurrency gate
/// allows, so a tick with thousands of due feeds is a long trickle, not a burst.
/// Manual refreshes do not take a slot: the person asked for them.</item>
/// <item><b>Yield</b> (<see cref="YieldToRequestsAsync"/>): before each fetch and each
/// store, if any user request is in flight (<see cref="RequestActivity"/>), wait a short
/// poll interval and look again, for at most <see cref="RssAppConfig.RefreshYieldMaxWait"/>.
/// The bound means refreshes still progress under sustained traffic; they just lose
/// every race for the CPU to a request that is already waiting.</item>
/// </list>
///
/// The clock and the delay are injectable so the tests run in milliseconds.
/// </summary>
public class RefreshPacer
{
    private readonly RssAppConfig config;
    private readonly RequestActivity activity;
    private readonly Func<DateTimeOffset> now;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly object slotLock = new();
    private DateTimeOffset nextSlot = DateTimeOffset.MinValue;

    public RefreshPacer(RssAppConfig config, RequestActivity activity)
        : this(config, activity, () => DateTimeOffset.UtcNow, Task.Delay)
    {
    }

    internal RefreshPacer(
        RssAppConfig config,
        RequestActivity activity,
        Func<DateTimeOffset> now,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        this.config = config;
        this.activity = activity;
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

    /// <summary>
    /// Returns once no user request is in flight, or after
    /// <see cref="RssAppConfig.RefreshYieldMaxWait"/>, whichever is first. Returns the
    /// time spent waiting, for logging and tests.
    /// </summary>
    public virtual async Task<TimeSpan> YieldToRequestsAsync(CancellationToken token)
    {
        if (this.activity.InFlight <= 0)
        {
            return TimeSpan.Zero;
        }

        var maxWait = this.config.RefreshYieldMaxWait;
        var poll = this.config.RefreshYieldPollInterval;
        if (maxWait <= TimeSpan.Zero || poll <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var started = this.now();
        while (this.activity.InFlight > 0)
        {
            var waited = this.now() - started;
            if (waited >= maxWait)
            {
                return waited;
            }

            var remaining = maxWait - waited;
            await this.delay(poll < remaining ? poll : remaining, token);
        }

        return this.now() - started;
    }
}
