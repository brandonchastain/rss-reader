#nullable enable
using System;
using System.Globalization;
using System.Threading.Tasks;
using RssApp.Data;

namespace RssReader.Server.Services
{
    /// <summary>
    /// Whether feed refresh is still bringing posts in, for an outside monitor: reported
    /// anonymously on <c>/api/health/feeds</c>, so it carries aggregates only and names no
    /// feed, post or user. The site can be up while refresh has stopped (an image that
    /// will not pull leaves the old revision serving), which <c>/api/healthz</c> does not
    /// show.
    /// </summary>
    /// <param name="NewestItemAgeHours">Hours since the newest post was stored; null with no posts.</param>
    /// <param name="FeedsFailing">Subscribed feeds whose last fetch failed.</param>
    /// <param name="LastRefreshUtc">Latest fetch of any subscribed feed, failed or not; null if none has been fetched.</param>
    public sealed record FeedFreshness(double? NewestItemAgeHours, long FeedsFailing, DateTime? LastRefreshUtc)
    {
        // Newest by store time (FirstSeenUtc), not publish date: a feed that dates its
        // posts in the future must not mask a stall. Posts.Id is the rowid, so the newest
        // row is one btree seek; Feeds holds one row per distinct URL. Only subscribed
        // feeds are fetched (GetFeedFetchStates), so orphans waiting for retention don't count.
        public const string Sql = """
            SELECT (SELECT FirstSeenUtc FROM Posts ORDER BY Id DESC LIMIT 1),
                   (SELECT count(*) FROM Feeds f
                     WHERE f.ConsecutiveFailures > 0
                       AND EXISTS (SELECT 1 FROM Subscriptions s WHERE s.FeedId = f.Id)),
                   (SELECT max(f.LastFetchedUtc) FROM Feeds f
                     WHERE EXISTS (SELECT 1 FROM Subscriptions s WHERE s.FeedId = f.Id))
            """;

        public static async Task<FeedFreshness> ReadAsync(IDbConnections db, DateTimeOffset now)
        {
            using var conn = await db.OpenReadAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = Sql;
            using var reader = await cmd.ExecuteReaderAsync();
            await reader.ReadAsync();
            var newest = ParseUtc(reader.IsDBNull(0) ? null : reader.GetString(0));
            var lastRefresh = ParseUtc(reader.IsDBNull(2) ? null : reader.GetString(2));
            return new FeedFreshness(
                newest == null ? null : Math.Round(Math.Max(0, (now - newest.Value).TotalHours), 1),
                reader.GetInt64(1),
                lastRefresh?.UtcDateTime);
        }

        private static DateTimeOffset? ParseUtc(string? value)
            => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : null;
    }
}
