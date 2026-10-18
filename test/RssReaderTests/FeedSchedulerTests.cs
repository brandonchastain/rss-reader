using Microsoft.Extensions.Logging.Abstractions;
using RssApp.ComponentServices;
using RssApp.Config;
using RssApp.Contracts;

namespace RssReaderTests;

[TestClass]
public class FeedSchedulerTests
{
    // Counts scheduler ticks and signals the first one, so tests can assert on
    // whether a tick happened without sleeping for a fixed wall-clock budget.
    private sealed class CountingRefresher : IFeedRefresher
    {
        private int tickCount;

        public int TickCount => Volatile.Read(ref this.tickCount);

        public TaskCompletionSource FirstTick { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<DateTimeOffset?> RunSchedulerTickAsync(CancellationToken token)
        {
            Interlocked.Increment(ref this.tickCount);
            this.FirstTick.TrySetResult();
            return Task.FromResult<DateTimeOffset?>(null);
        }

        public Task AddFeedAsync(NewsFeed feed) => Task.CompletedTask;
        public Task RefreshAsync(RssUser user) => Task.CompletedTask;
        public RefreshStatusResponse GetRefreshStatus(RssUser user) => new();
        public void ResetRefreshCooldown() { }
    }

    private static FeedScheduler Create(RssAppConfig config, IFeedRefresher refresher)
        => new(NullLogger<FeedScheduler>.Instance, refresher, config);

    [TestMethod]
    public async Task FeedScheduler_HoldsFirstTickUntilStartupDelayElapses()
    {
        var config = new RssAppConfig
        {
            SchedulerEnabled = true,
            SchedulerStartupDelay = TimeSpan.FromSeconds(30),
            SchedulerTickInterval = TimeSpan.FromMilliseconds(10),
        };
        var refresher = new CountingRefresher();
        var scheduler = Create(config, refresher);

        using var cts = new CancellationTokenSource();
        await scheduler.StartAsync(cts.Token);

        // Well past the tick interval, but nowhere near the startup delay: the
        // cold-start window must stay clear of feed fetches.
        var ticked = await Task.WhenAny(
            refresher.FirstTick.Task,
            Task.Delay(TimeSpan.FromMilliseconds(500)));

        await cts.CancelAsync();
        try { await scheduler.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

        Assert.AreNotSame(refresher.FirstTick.Task, ticked, "Scheduler ticked before the startup delay elapsed.");
        Assert.AreEqual(0, refresher.TickCount);
    }

    [TestMethod]
    public async Task FeedScheduler_TicksOnceStartupDelayElapses()
    {
        var config = new RssAppConfig
        {
            SchedulerEnabled = true,
            SchedulerStartupDelay = TimeSpan.FromMilliseconds(50),
            SchedulerTickInterval = TimeSpan.FromSeconds(30),
        };
        var refresher = new CountingRefresher();
        var scheduler = Create(config, refresher);

        using var cts = new CancellationTokenSource();
        await scheduler.StartAsync(cts.Token);

        var completed = await Task.WhenAny(
            refresher.FirstTick.Task,
            Task.Delay(TimeSpan.FromSeconds(10)));

        await cts.CancelAsync();
        try { await scheduler.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

        Assert.AreSame(refresher.FirstTick.Task, completed, "Scheduler never ticked after the startup delay.");
    }

    [TestMethod]
    public void SleepUntil_SleepsExactlyUntilNextDueFeed()
    {
        var now = new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
        var max = TimeSpan.FromMinutes(30);

        Assert.AreEqual(TimeSpan.FromMinutes(7), FeedScheduler.SleepUntil(now.AddMinutes(7), now, max));
        Assert.AreEqual(max, FeedScheduler.SleepUntil(now.AddHours(3), now, max), "capped at max sleep");
        Assert.AreEqual(max, FeedScheduler.SleepUntil(null, now, max), "nothing scheduled: max sleep");
        Assert.AreEqual(TimeSpan.FromSeconds(1), FeedScheduler.SleepUntil(now.AddMinutes(-5), now, max), "overdue: floor, never spin");
        Assert.AreEqual(TimeSpan.FromMilliseconds(10), FeedScheduler.SleepUntil(null, now, TimeSpan.FromMilliseconds(10)), "floor never exceeds max");
    }

    [TestMethod]
    public async Task FeedScheduler_DisabledNeverTicks()
    {
        var config = new RssAppConfig
        {
            SchedulerEnabled = false,
            SchedulerStartupDelay = TimeSpan.Zero,
            SchedulerTickInterval = TimeSpan.FromMilliseconds(10),
        };
        var refresher = new CountingRefresher();
        var scheduler = Create(config, refresher);

        using var cts = new CancellationTokenSource();
        await scheduler.StartAsync(cts.Token);
        await Task.Delay(200);

        await cts.CancelAsync();
        try { await scheduler.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

        Assert.AreEqual(0, refresher.TickCount);
    }
}
