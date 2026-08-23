using WasmApp.Services;

namespace SerializerTests;

/// <summary>
/// Pins the new-posts probe's delay policy. A fixed short retry here previously
/// turned a fast-failing API into one request per second for as long as the app
/// stayed open.
/// </summary>
[TestClass]
public class ProbePollingTests
{
    [TestMethod]
    public void FirstDelayIsShortSoTheToastIsNotHeldBack()
    {
        Assert.IsTrue(ProbePolling.InitialDelayMs < ProbePolling.IntervalMs);
    }

    [TestMethod]
    public void Backoff_GrowsGeometrically()
    {
        Assert.AreEqual(2_000, ProbePolling.Backoff(1_000));
        Assert.AreEqual(4_000, ProbePolling.Backoff(2_000));
        Assert.AreEqual(8_000, ProbePolling.Backoff(4_000));
    }

    [TestMethod]
    public void Backoff_NeverExceedsTheSteadyInterval()
    {
        int delay = ProbePolling.InitialDelayMs;
        for (int i = 0; i < 20; i++)
        {
            delay = ProbePolling.Backoff(delay);
            Assert.IsTrue(delay <= ProbePolling.IntervalMs, $"delay {delay} exceeded the interval");
        }

        Assert.AreEqual(ProbePolling.IntervalMs, delay, "sustained failure should settle at the interval");
    }

    [TestMethod]
    public void Backoff_AlwaysMakesProgress()
    {
        // A delay that failed to grow would reproduce the original hot loop.
        int delay = ProbePolling.InitialDelayMs;
        for (int i = 0; i < 6 && delay < ProbePolling.IntervalMs; i++)
        {
            int next = ProbePolling.Backoff(delay);
            Assert.IsTrue(next > delay, $"backoff stalled at {delay}ms");
            delay = next;
        }
    }

    [TestMethod]
    public void Backoff_RecoversFromANonPositiveDelay()
    {
        Assert.AreEqual(ProbePolling.InitialDelayMs, ProbePolling.Backoff(0));
        Assert.AreEqual(ProbePolling.InitialDelayMs, ProbePolling.Backoff(-5));
    }

    [TestMethod]
    public void Steady_ReturnsToTheNormalIntervalAfterSuccess()
    {
        // One success must undo any accumulated backoff, so recovery is prompt.
        Assert.AreEqual(ProbePolling.IntervalMs, ProbePolling.Steady());
    }
}
