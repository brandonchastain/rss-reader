using Microsoft.Extensions.Logging.Abstractions;
using RssApp.ComponentServices;
using RssApp.Config;
using RssApp.Contracts;

namespace SerializerTests;

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

        public Task RunSchedulerTickAsync(CancellationToken token)
        {
            Interlocked.Increment(ref this.tickCount);
            this.FirstTick.TrySetResult();
            return Task.CompletedTask;
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
