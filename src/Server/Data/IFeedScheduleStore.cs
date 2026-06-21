namespace RssApp.Data;

/// <summary>
/// Persisted per-URL fetch schedule for the background feed scheduler. Records
/// when each distinct feed URL was last fetched and the earliest time it should
/// be fetched again, so the cadence survives restarts (no thundering herd) and a
/// feed shared by many users is still fetched on a single schedule.
/// </summary>
public interface IFeedScheduleStore
{
    /// <summary>
    /// Snapshot of <c>url → next-earliest-fetch (UTC)</c>. A URL absent from the
    /// map has never been scheduled and is therefore due immediately.
    /// </summary>
    IReadOnlyDictionary<string, DateTimeOffset> GetSchedule();

    /// <summary>Upserts the schedule for a URL after a fetch attempt.</summary>
    void Record(string url, DateTimeOffset lastFetchedUtc, DateTimeOffset nextEarliestFetchUtc, TimeSpan interval);
}
