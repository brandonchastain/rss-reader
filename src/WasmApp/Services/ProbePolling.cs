namespace WasmApp.Services;

/// <summary>
/// Delay policy for the timeline's "are there new posts?" probe.
///
/// The first delay is deliberately short: when the timeline is hydrated from
/// cache, the probe is the only thing that will surface new posts, and hydration
/// finishes after the post table's first render -- waiting a full interval there
/// would leave the reader on a stale list with no sign anything is coming.
///
/// That short delay must not survive a failing API. An unreachable backend fails
/// fast (Container Apps' ingress answers 404 in ~25ms when no revision is
/// active), so a fixed short retry becomes one request per second for as long as
/// the app is open. Failures therefore back off exponentially up to the normal
/// interval, and any success drops straight back to it.
/// </summary>
public static class ProbePolling
{
    public const int IntervalMs = 30_000;
    public const int InitialDelayMs = 1_000;

    /// <summary>Delay after a failed or skipped probe: double, capped at the interval.</summary>
    public static int Backoff(int currentMs)
    {
        if (currentMs <= 0)
        {
            return InitialDelayMs;
        }

        var doubled = currentMs >= IntervalMs / 2 ? IntervalMs : currentMs * 2;
        return Math.Min(doubled, IntervalMs);
    }

    /// <summary>Delay after a successful probe: back to the steady-state interval.</summary>
    public static int Steady() => IntervalMs;
}
