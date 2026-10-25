namespace RssReaderTests;

using RssApp.Config;
using RssReader.Server.Services;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// The refresher's pacing budget on a fake clock: delays are recorded instead of
/// slept, and advance the clock.
/// </summary>
[TestClass]
public sealed class RefreshPacerTests
{
    private sealed class FakeClock
    {
        public DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        public List<TimeSpan> Delays { get; } = new();

        public Task Delay(TimeSpan wait, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            this.Delays.Add(wait);
            this.Now += wait;
            return Task.CompletedTask;
        }
    }

    private static (RefreshPacer pacer, FakeClock clock) Create(RssAppConfig config = null)
    {
        config ??= new RssAppConfig();
        var clock = new FakeClock();
        var pacer = new RefreshPacer(config, () => clock.Now, clock.Delay);
        return (pacer, clock);
    }

    [TestMethod]
    public async Task Budget_SpacesFetchStarts_AtTheConfiguredRate()
    {
        var (pacer, clock) = Create(new RssAppConfig { SchedulerFetchesPerSecond = 4 });

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
        var (pacer, clock) = Create(new RssAppConfig { SchedulerFetchesPerSecond = 5 });

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
        var (pacer, clock) = Create(new RssAppConfig { SchedulerFetchesPerSecond = 0 });

        for (int i = 0; i < 100; i++)
        {
            await pacer.WaitForFetchSlotAsync(CancellationToken.None);
        }

        Assert.AreEqual(0, clock.Delays.Count);
    }

    [TestMethod]
    public async Task Budget_FiveHundredCallers_SpanTheExpectedWindow()
    {
        var (pacer, clock) = Create(new RssAppConfig { SchedulerFetchesPerSecond = 5 });
        var start = clock.Now;

        for (int i = 0; i < 500; i++)
        {
            await pacer.WaitForFetchSlotAsync(CancellationToken.None);
        }

        // 500 feeds at 5/s: the last one starts 99.8 s after the first.
        Assert.AreEqual(TimeSpan.FromSeconds(99.8), clock.Now - start);
    }
}
