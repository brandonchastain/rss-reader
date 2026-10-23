#nullable enable
using RssApp.Contracts;

namespace RssReader.Server.Services;

/// <summary>
/// The admin dashboard's trend ranges. Snapshots are taken at most hourly, so
/// a year is up to ~8,800 points; the chart only needs a few hundred, and
/// thinning server-side keeps the response small on a phone.
/// </summary>
public static class StatsHistoryRange
{
    public static readonly int[] AllowedDays = { 1, 7, 30, 365 };

    /// <summary>Snapshots are kept a little longer than the longest range.</summary>
    public const int RetentionDays = 400;

    /// <summary>Upper bound on points returned for one chart.</summary>
    public const int MaxPoints = 400;

    public static bool IsAllowed(int days) => Array.IndexOf(AllowedDays, days) >= 0;

    /// <summary>
    /// Keeps at most <paramref name="maxPoints"/> evenly spaced snapshots,
    /// always including the first and the last so the range's ends are exact.
    /// </summary>
    public static List<SystemStatsSnapshot> Thin(IReadOnlyList<SystemStatsSnapshot> ordered, int maxPoints = MaxPoints)
    {
        if (maxPoints < 2) throw new ArgumentOutOfRangeException(nameof(maxPoints));
        if (ordered.Count <= maxPoints) return ordered.ToList();

        var result = new List<SystemStatsSnapshot>(maxPoints);
        var step = (ordered.Count - 1) / (double)(maxPoints - 1);
        for (var i = 0; i < maxPoints; i++)
        {
            var index = (int)Math.Round(i * step);
            result.Add(ordered[Math.Min(index, ordered.Count - 1)]);
        }
        return result;
    }
}
