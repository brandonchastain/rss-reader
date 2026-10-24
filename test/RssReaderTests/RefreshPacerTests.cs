namespace RssReaderTests;

using Microsoft.AspNetCore.Http;
using RssApp.Config;
using RssReader.Server.Services;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// The refresher's pacing budget and its yield to live traffic, on a fake clock:
/// delays are recorded instead of slept, and advance the clock.
/// </summary>
[TestClass]
public sealed class RefreshPacerTests
{
    private sealed class FakeClock
    {
        public DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        public List<TimeSpan> Delays { get; } = new();
        public Action OnDelay { get; set; }

        public Task Delay(TimeSpan wait, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            this.Delays.Add(wait);
            this.Now += wait;
            this.OnDelay?.Invoke();
            return Task.CompletedTask;
        }
    }

    private static (RefreshPacer pacer, FakeClock clock, RequestActivity activity) Create(RssAppConfig config = null)
    {
        config ??= new RssAppConfig();
        var clock = new FakeClock();
        var activity = new RequestActivity();
        var pacer = new RefreshPacer(config, activity, () => clock.Now, clock.Delay);
        return (pacer, clock, activity);
    }

    [TestMethod]
    public async Task Budget_SpacesFetchStarts_AtTheConfiguredRate()
    {
        var (pacer, clock, _) = Create(new RssAppConfig { SchedulerFetchesPerSecond = 4 });

        // Five callers one after another (the clock advances as each waits): the
        // first goes now, each of the rest a quarter second after the previous.
        var start = clock.Now;
        for (int i = 0; i < 5; i++)
        {
            await pacer.WaitForFetchSlotAsync(CancellationToken.None);
        }

        CollectionAssert.AreEqual(
            Enumerable.Repeat(TimeSpan.FromSeconds(0.25), 4).ToList(),
            clock.Delays,
            "the first slot is free; each later one is 1/rate after the previous");
        Assert.AreEqual(TimeSpan.FromSeconds(1), clock.Now - start);
    }

    [TestMethod]
    public async Task Budget_DoesNotBankUnusedSlots()
    {
        var (pacer, clock, _) = Create(new RssAppConfig { SchedulerFetchesPerSecond = 5 });

        await pacer.WaitForFetchSlotAsync(CancellationToken.None);
        clock.Now += TimeSpan.FromMinutes(10); // a long quiet spell
        await pacer.WaitForFetchSlotAsync(CancellationToken.None);
        await pacer.WaitForFetchSlotAsync(CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { TimeSpan.FromMilliseconds(200) }, clock.Delays,
            "after a quiet spell the next caller starts at once and only the one after it waits one spacing");
    }

    [TestMethod]
    public async Task Budget_IsOff_WhenRateIsZero()
    {
        var (pacer, clock, _) = Create(new RssAppConfig { SchedulerFetchesPerSecond = 0 });

        for (int i = 0; i < 100; i++)
        {
            await pacer.WaitForFetchSlotAsync(CancellationToken.None);
        }

        Assert.AreEqual(0, clock.Delays.Count);
    }

    [TestMethod]
    public async Task Budget_FiveHundredCallers_SpanTheExpectedWindow()
    {
        var (pacer, clock, _) = Create(new RssAppConfig { SchedulerFetchesPerSecond = 5 });
        var start = clock.Now;

        for (int i = 0; i < 500; i++)
        {
            await pacer.WaitForFetchSlotAsync(CancellationToken.None);
        }

        // 500 feeds at 5/s: the last one starts 99.8 s after the first.
        Assert.AreEqual(TimeSpan.FromSeconds(99.8), clock.Now - start);
    }

    [TestMethod]
    public async Task Yield_ReturnsAtOnce_WhenNoRequestIsInFlight()
    {
        var (pacer, clock, _) = Create();

        var waited = await pacer.YieldToRequestsAsync(CancellationToken.None);

        Assert.AreEqual(TimeSpan.Zero, waited);
        Assert.AreEqual(0, clock.Delays.Count);
    }

    [TestMethod]
    public async Task Yield_PollsWhileRequestsAreInFlight_ThenProceeds()
    {
        var config = new RssAppConfig { RefreshYieldPollInterval = TimeSpan.FromMilliseconds(25), RefreshYieldMaxWait = TimeSpan.FromSeconds(3) };
        var (pacer, clock, activity) = Create(config);
        activity.Enter();
        activity.Enter();
        int polls = 0;
        clock.OnDelay = () =>
        {
            // The two requests finish after the first and third poll.
            if (++polls == 1) activity.Exit();
            if (polls == 3) activity.Exit();
        };

        var waited = await pacer.YieldToRequestsAsync(CancellationToken.None);

        Assert.AreEqual(3, clock.Delays.Count, "polls until the pipeline is empty");
        Assert.IsTrue(clock.Delays.All(d => d == TimeSpan.FromMilliseconds(25)));
        Assert.AreEqual(TimeSpan.FromMilliseconds(75), waited);
    }

    [TestMethod]
    public async Task Yield_GivesUp_AfterTheMaximumWait_UnderSustainedTraffic()
    {
        var config = new RssAppConfig { RefreshYieldPollInterval = TimeSpan.FromMilliseconds(25), RefreshYieldMaxWait = TimeSpan.FromSeconds(3) };
        var (pacer, clock, activity) = Create(config);
        activity.Enter(); // never exits: traffic is continuous

        var waited = await pacer.YieldToRequestsAsync(CancellationToken.None);

        Assert.AreEqual(TimeSpan.FromSeconds(3), waited, "the wait is bounded so refreshes always progress");
        Assert.AreEqual(120, clock.Delays.Count, "3 s / 25 ms polls");
        Assert.AreEqual(1, activity.InFlight, "yielding never touches the counter");
    }

    [TestMethod]
    public async Task Yield_IsOff_WhenMaxWaitIsZero()
    {
        var (pacer, clock, activity) = Create(new RssAppConfig { RefreshYieldMaxWait = TimeSpan.Zero });
        activity.Enter();

        var waited = await pacer.YieldToRequestsAsync(CancellationToken.None);

        Assert.AreEqual(TimeSpan.Zero, waited);
        Assert.AreEqual(0, clock.Delays.Count);
    }

    [TestMethod]
    public async Task Yield_HonoursCancellation()
    {
        var (pacer, _, activity) = Create();
        activity.Enter();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => pacer.YieldToRequestsAsync(cts.Token));
    }

    [TestMethod]
    public async Task Middleware_CountsARequestForItsWholeLife_AndSkipsTheHealthProbe()
    {
        var activity = new RequestActivity();
        int seenInside = -1;
        var middleware = new RequestActivityMiddleware(ctx =>
        {
            seenInside = activity.InFlight;
            return Task.CompletedTask;
        }, activity);

        var request = new DefaultHttpContext();
        request.Request.Path = "/api/item/timeline";
        await middleware.InvokeAsync(request);
        Assert.AreEqual(1, seenInside, "counted while the rest of the pipeline runs");
        Assert.AreEqual(0, activity.InFlight, "released when the response is done");

        var probe = new DefaultHttpContext();
        probe.Request.Path = "/api/healthz";
        await middleware.InvokeAsync(probe);
        Assert.AreEqual(0, seenInside, "the health probe is not a user and must not hold the refresher back");
    }

    [TestMethod]
    public async Task Middleware_ReleasesTheCount_WhenThePipelineThrows()
    {
        var activity = new RequestActivity();
        var middleware = new RequestActivityMiddleware(_ => throw new InvalidOperationException("boom"), activity);
        var request = new DefaultHttpContext();
        request.Request.Path = "/api/feed";

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => middleware.InvokeAsync(request));

        Assert.AreEqual(0, activity.InFlight);
    }
}
